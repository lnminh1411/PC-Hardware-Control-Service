using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Display;

/// <summary>
/// The service's view of the monitors: relays commands to the user-session display agent and keeps the last
/// "list" it answered, refreshed every PollS. One request at a time. Talks only to the agent the launcher started:
/// the pipe name is new for every launch and the server must be that very process (Bind).
/// </summary>
public sealed class AgentDisplayClient : IDisplayControl, IDisposable
{
    private const int ConnectTimeoutMs = 1000;
    private const int ReplyTimeoutMs = 15000; // a power or input change over DDC can take a few seconds
    private const int MaxLineBytes = 64 * 1024;
    private const int MaxMonitors = 16;
    public const int MaxPayloadBytes = 256 * 1024 * 1024; // a PNG of several 4K/8K monitors

    private readonly Func<int> _pollS;
    private readonly Action<string> _log;
    private readonly object _requestGate = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<DisplayState> _displays = [];
    private volatile string? _status = "display agent not started yet";
    private volatile Binding? _binding;

    private sealed record Binding(string Pipe, int Pid);

    public AgentDisplayClient(Func<int> pollS, Action<string> log)
    {
        _pollS = pollS;
        _log = log;
    }

    public IReadOnlyList<DisplayState> Displays => _displays;

    public string? Status => _status;

    /// <summary>The agent the launcher just started: its pipe name and pid. Null = none (requests fail).</summary>
    public void Bind(string? pipeName, int pid)
    {
        lock (_requestGate)
        {
            _binding = pipeName == null ? null : new Binding(pipeName, pid);
            _displays = [];
            _status = pipeName == null ? "display agent not running" : "display agent starting";
        }
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                RefreshList();
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, _pollS())));
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-DisplayClient"
        };
        thread.Start();
    }

    public DisplayResult Brightness(string target, int pct) => Command($"bright {target} {pct}");
    public DisplayResult Power(string target, bool on) => Command($"power {target} {(on ? "on" : "off")}");
    public DisplayResult Input(string target, int code) => Command($"input {target} 0x{code:X2}");
    public DisplayResult Sleep() => Command("sleep");
    public DisplayResult Wake() => Command("wake");

    private DisplayResult Command(string line)
    {
        string? reply = Request(line, ReplyTimeoutMs);
        if (reply == null) return DisplayResult.Fail(_status ?? "display agent not reachable");
        RefreshList();

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            return root.GetProperty("ok").GetBoolean()
                ? DisplayResult.Success
                : DisplayResult.Fail(root.TryGetProperty("error", out var e) ? e.GetString() ?? "failed" : "failed");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return DisplayResult.Fail($"display agent sent a bad reply: {ex.Message}");
        }
    }

    private bool RefreshList()
    {
        string? reply = Request("list", ReplyTimeoutMs);
        if (reply == null) return false;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var view = doc.RootElement.GetProperty("data").Deserialize<DisplaysView>(DisplayCommands.Json);
            // System.Text.Json fills [null] without complaint; the state file and IPC would then fault on it.
            _displays = view?.Monitors?.OfType<DisplayState>().Where(m => m.Name != null && m.PowerMethod != null).Take(MaxMonitors).ToList() ?? [];
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _status = $"display agent sent a bad reply: {ex.Message}";
            return false;
        }
    }

    /// <summary>A raw agent command (presence); the reply line, null when the agent is unreachable.</summary>
    public string? Request(string line, int replyTimeoutMs) => Send(line, replyTimeoutMs, null).Reply;

    /// <summary>
    /// A command whose reply line is followed by raw bytes (shot): the line's data.bytes says how many. The caller owns
    /// the array and must clear it.
    /// </summary>
    public (string? Reply, byte[]? Payload) RequestWithPayload(string line, int replyTimeoutMs, Func<string, int> payloadBytes) =>
        Send(line, replyTimeoutMs, payloadBytes);

    /// <summary>One line out, one line back (and the payload, if any); null (and Status set) when the agent is unreachable.</summary>
    private (string? Reply, byte[]? Payload) Send(string line, int replyTimeoutMs, Func<string, int>? payloadBytes)
    {
        lock (_requestGate)
        {
            var binding = _binding;
            if (binding == null) return (null, null);
            byte[]? payload = null;
            try
            {
                // Anonymous: whoever holds the name must not get to act as SYSTEM.
                using var pipe = new NamedPipeClientStream(".", binding.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                pipe.Connect(ConnectTimeoutMs);
                CheckServer(pipe, binding);
                using var timeout = new CancellationTokenSource(replyTimeoutMs);
                byte[] request = Encoding.UTF8.GetBytes(line + "\n");
                pipe.WriteAsync(request, timeout.Token).AsTask().GetAwaiter().GetResult();
                var buffered = new BufferedStream(pipe, 16 * 1024);
                string reply = ReadLine(buffered, timeout.Token);
                if (payloadBytes != null)
                {
                    int n = payloadBytes(reply);
                    if (n is < 0 or > MaxPayloadBytes) throw new IOException($"agent announced {n} payload bytes");
                    if (n > 0)
                    {
                        payload = new byte[n];
                        buffered.ReadExactlyAsync(payload, timeout.Token).AsTask().GetAwaiter().GetResult();
                    }
                }
                if (_status != null) _log("Display agent reachable.");
                _status = null;
                return (reply, payload);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AggregateException or Win32Exception
                                           or ArgumentException or OperationCanceledException or EndOfStreamException)
            {
                if (payload != null) Array.Clear(payload);
                string status = $"display agent not reachable: {(ex is OperationCanceledException ? $"no reply within {replyTimeoutMs / 1000} s" : ex.Message)}";
                if (_status == null) _log(status);
                _status = status;
                _displays = [];
                return (null, null);
            }
        }
    }

    /// <summary>One UTF-8 line, at most MaxLineBytes: a reply without a newline must not grow without end in SYSTEM.</summary>
    private static string ReadLine(Stream stream, CancellationToken ct)
    {
        var bytes = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (stream.ReadAsync(one, ct).AsTask().GetAwaiter().GetResult() == 0) throw new IOException("agent closed the pipe");
            if (one[0] == (byte)'\n') break;
            if (bytes.Length >= MaxLineBytes) throw new IOException($"agent reply longer than {MaxLineBytes / 1024} KiB");
            bytes.WriteByte(one[0]);
        }
        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length).TrimEnd('\r');
    }

    /// <summary>
    /// Pipe names are machine-wide and first come, first served, and any account can start the installed exe, so
    /// neither the name nor the image proves anything: the server must be the process the launcher created.
    /// </summary>
    private static void CheckServer(NamedPipeClientStream pipe, Binding binding)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new IOException($"no pipe server process (error {Marshal.GetLastWin32Error()})");
        if (pid != (uint)binding.Pid) throw new UnauthorizedAccessException($"{binding.Pipe} is held by pid {pid}, not the agent the service started (pid {binding.Pid})");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    public void Dispose() => _cts.Cancel();
}
