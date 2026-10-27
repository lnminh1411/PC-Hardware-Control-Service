using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;

namespace PcHwCtl.Service.Ipc;

/// <summary>
/// Named pipe IPC server hosting up to 4 concurrent client connections.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private const int MaxClients = 4;
    private const int MaxLineBytes = 4096;

    private readonly CommandProcessor _processor;
    private readonly IpcConfig _cfg;
    private readonly Func<int> _tickMs;
    private readonly Action<string> _log;
    private readonly SensorFeedHub? _sensors;

    private readonly SemaphoreSlim _concurrencySemaphore = new(MaxClients, MaxClients);
    private readonly ConcurrentDictionary<Task, bool> _activeTasks = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipeServer"/> class.
    /// </summary>
    public PipeServer(CommandProcessor processor, IpcConfig cfg, Func<int> tickMs, Action<string> log, SensorFeedHub? sensors = null)
    {
        _sensors = sensors;
        _processor = processor;
        _cfg = cfg;
        _tickMs = tickMs;
        _log = log;
    }

    /// <summary>
    /// Starts the pipe server listener in the background.
    /// </summary>
    public void Start()
    {
        if (_cts != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        TrackTask(_listenerTask);
    }

    /// <summary>
    /// Stops the server, cancels active client connections, and waits at most 2 seconds.
    /// </summary>
    public void Stop()
    {
        if (_cts == null || _cts.IsCancellationRequested)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            Task[] tasks = _activeTasks.Keys.ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore cancellation or wait timeout
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts?.Dispose();
        _concurrencySemaphore.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        string pipeName = GetCleanPipeName();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _concurrencySemaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            NamedPipeServerStream pipe;
            try
            {
                var security = CreatePipeSecurity();
                pipe = NamedPipeServerStreamAcl.Create(
                    pipeName,
                    PipeDirection.InOut,
                    MaxClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    security);
            }
            catch (Exception ex)
            {
                _concurrencySemaphore.Release();
                _log($"PipeServer: failed to create pipe '{pipeName}': {ex.Message}");
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                _concurrencySemaphore.Release();
                break;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                _concurrencySemaphore.Release();
                _log($"PipeServer: connection wait failed: {ex.Message}");
                continue;
            }

            Task clientTask = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(pipe, ct);
                }
                finally
                {
                    _concurrencySemaphore.Release();
                }
            });

            TrackTask(clientTask);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                byte[] readBuffer = new byte[1024];
                using var lineStream = new MemoryStream();

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    int bytesRead = await pipe.ReadAsync(readBuffer, 0, readBuffer.Length, ct);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = readBuffer[i];
                        if (b == (byte)'\n')
                        {
                            if (lineStream.Length > MaxLineBytes)
                            {
                                string err = "{\"ok\":false,\"error\":\"Line exceeds 4096 bytes.\"}\n";
                                byte[] errBytes = Encoding.UTF8.GetBytes(err);
                                await pipe.WriteAsync(errBytes, 0, errBytes.Length, ct);
                                await pipe.FlushAsync(ct);
                                return;
                            }

                            byte[] raw = lineStream.ToArray();
                            lineStream.SetLength(0);

                            int len = raw.Length;
                            if (len > 0 && raw[len - 1] == (byte)'\r')
                            {
                                len--;
                            }
                            string line = Encoding.UTF8.GetString(raw, 0, len);

                            string response = _processor.Execute(line);
                            byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                            await pipe.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
                            await pipe.FlushAsync(ct);

                            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (tokens.Length > 0 && tokens[0].Equals("subscribe", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!response.StartsWith("{\"ok\":true", StringComparison.Ordinal)) continue;
                                if (tokens.Length == 2 && _sensors != null) await StreamSensorsAsync(pipe, _sensors, ct);
                                else await StreamSubscribeLoopAsync(pipe, ct);
                                return;
                            }
                        }
                        else
                        {
                            lineStream.WriteByte(b);
                            if (lineStream.Length > MaxLineBytes)
                            {
                                string err = "{\"ok\":false,\"error\":\"Line exceeds 4096 bytes.\"}\n";
                                byte[] errBytes = Encoding.UTF8.GetBytes(err);
                                await pipe.WriteAsync(errBytes, 0, errBytes.Length, ct);
                                await pipe.FlushAsync(ct);
                                return;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation on server shutdown
            }
            catch (Exception ex)
            {
                _log($"PipeServer: client exception: {ex.Message}");
            }
        }
    }

    /// <summary>The latest snapshot at once, then each new one as the sensor loop publishes it.</summary>
    private static async Task StreamSensorsAsync(NamedPipeServerStream pipe, SensorFeedHub hub, CancellationToken ct)
    {
        long seq = 0;
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var snapshot = await hub.NextAsync(seq, ct);
            seq = snapshot.Seq;
            byte[] bytes = Encoding.UTF8.GetBytes(snapshot.ToText());
            await pipe.WriteAsync(bytes, 0, bytes.Length, ct);
            await pipe.FlushAsync(ct);
        }
    }

    private async Task StreamSubscribeLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            int ms = _tickMs();
            if (ms <= 0)
            {
                ms = 1000;
            }

            await Task.Delay(ms, ct);

            if (!pipe.IsConnected)
            {
                break;
            }

            string state = _processor.StateJson();
            string push = $"{{\"ok\":true,\"data\":{state}}}\n";
            byte[] bytes = Encoding.UTF8.GetBytes(push);
            await pipe.WriteAsync(bytes, 0, bytes.Length, ct);
            await pipe.FlushAsync(ct);
        }
    }

    private PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        // SYSTEM and Administrators get FullControl: creating the 2nd..4th pipe instance needs
        // CreateNewInstance, and the server runs as one of them. Clients only need ReadWrite.
        var sidSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(
            sidSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        var sidAdmin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(
            sidAdmin,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        if (!string.IsNullOrWhiteSpace(_cfg.AllowAccount))
        {
            try
            {
                var account = new NTAccount(_cfg.AllowAccount);
                var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                security.AddAccessRule(new PipeAccessRule(
                    sid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            catch (Exception ex)
            {
                _log($"PipeServer: could not translate allow account '{_cfg.AllowAccount}': {ex.Message}");
            }
        }

        return security;
    }

    private string GetCleanPipeName()
    {
        string name = _cfg.PipeName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "PcHwCtl";
        }

        if (name.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(@"\\.\pipe\".Length);
        }
        else if (name.StartsWith(@"\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(@"\pipe\".Length);
        }

        return name;
    }

    private void TrackTask(Task task)
    {
        _activeTasks.TryAdd(task, true);
        _ = task.ContinueWith(t => _activeTasks.TryRemove(t, out _), TaskScheduler.Default);
    }
}
