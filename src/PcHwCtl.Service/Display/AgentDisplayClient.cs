using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Display;

/// <summary>
/// The service's view of the monitors: relays commands to the user-session display agent and keeps the last
/// "list" it answered, refreshed every PollS. One request at a time.
/// </summary>
public sealed class AgentDisplayClient : IDisplayControl, IDisposable
{
    private const int ConnectTimeoutMs = 1000;
    private const int ReplyTimeoutMs = 15000; // a power or input change over DDC can take a few seconds

    private readonly Func<int> _pollS;
    private readonly Action<string> _log;
    private readonly object _requestGate = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<DisplayState> _displays = [];
    private volatile string? _status = "display agent not started yet";

    public AgentDisplayClient(Func<int> pollS, Action<string> log)
    {
        _pollS = pollS;
        _log = log;
    }

    public IReadOnlyList<DisplayState> Displays => _displays;

    public string? Status => _status;

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

    /// <summary>True when an agent answers on the pipe.</summary>
    public bool AgentAnswers() => RefreshList();

    public DisplayResult Brightness(string target, int pct) => Command($"bright {target} {pct}");
    public DisplayResult Power(string target, bool on) => Command($"power {target} {(on ? "on" : "off")}");
    public DisplayResult Input(string target, int code) => Command($"input {target} 0x{code:X2}");
    public DisplayResult Sleep() => Command("sleep");
    public DisplayResult Wake() => Command("wake");

    private DisplayResult Command(string line)
    {
        string? reply = Send(line);
        if (reply == null) return DisplayResult.Fail(_status ?? "display agent not reachable");
        RefreshList();

        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        return root.GetProperty("ok").GetBoolean()
            ? DisplayResult.Success
            : DisplayResult.Fail(root.TryGetProperty("error", out var e) ? e.GetString() ?? "failed" : "failed");
    }

    private bool RefreshList()
    {
        string? reply = Send("list");
        if (reply == null) return false;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var view = doc.RootElement.GetProperty("data").Deserialize<DisplaysView>(DisplayCommands.Json);
            _displays = view?.Monitors ?? [];
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _status = $"display agent sent a bad reply: {ex.Message}";
            return false;
        }
    }

    /// <summary>One line out, one line back; null (and Status set) when the agent is unreachable.</summary>
    private string? Send(string line)
    {
        lock (_requestGate)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", DisplayAgent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(ConnectTimeoutMs);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                writer.WriteLine(line);
                var read = reader.ReadLineAsync();
                if (!read.Wait(ReplyTimeoutMs)) throw new TimeoutException($"no reply within {ReplyTimeoutMs / 1000} s");
                string? reply = read.Result ?? throw new IOException("agent closed the pipe");
                if (_status != null) _log("Display agent reachable.");
                _status = null;
                return reply;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
            {
                string status = $"display agent not reachable: {ex.Message}";
                if (_status == null) _log(status);
                _status = status;
                _displays = [];
                return null;
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
