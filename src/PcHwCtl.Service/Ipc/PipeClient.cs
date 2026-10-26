using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PcHwCtl.Service.Ipc;

/// <summary>
/// Named pipe IPC client for sending commands to the PcHwCtl service.
/// </summary>
public static class PipeClient
{
    /// <summary>
    /// Connects to the named pipe, sends commandLine, and outputs responses.
    /// Returns 0 when ok:true, 1 when ok:false, 2 when the service is not reachable.
    /// </summary>
    public static async Task<int> RunAsync(string pipeName, string commandLine, TextWriter output, CancellationToken ct)
    {
        string cleanPipeName = pipeName;
        if (cleanPipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            cleanPipeName = cleanPipeName.Substring(@"\\.\pipe\".Length);
        }
        else if (cleanPipeName.StartsWith(@"\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            cleanPipeName = cleanPipeName.Substring(@"\pipe\".Length);
        }

        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", cleanPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(1000);
                await client.ConnectAsync(connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await output.WriteLineAsync($"Service not reachable at pipe '{pipeName}' (connection timed out after 1 s).");
                return 2;
            }
            catch (TimeoutException)
            {
                await output.WriteLineAsync($"Service not reachable at pipe '{pipeName}' (connection timed out).");
                return 2;
            }
            catch (Exception ex)
            {
                await output.WriteLineAsync($"Service not reachable at pipe '{pipeName}': {ex.Message}");
                return 2;
            }

            var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
            {
                NewLine = "\n"
            };
            await writer.WriteLineAsync(commandLine);
            await writer.FlushAsync();

            var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            string? firstResponse = await reader.ReadLineAsync(ct);
            if (firstResponse == null)
            {
                await output.WriteLineAsync("No response received from service.");
                return 1;
            }

            await output.WriteLineAsync(firstResponse);

            bool isOk = false;
            try
            {
                using var doc = JsonDocument.Parse(firstResponse);
                if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True)
                {
                    isOk = true;
                }
            }
            catch
            {
                isOk = false;
            }

            string[] tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (isOk && tokens.Length > 0 && tokens[0].Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        string? streamLine = await reader.ReadLineAsync(ct);
                        if (streamLine == null)
                        {
                            break;
                        }
                        await output.WriteLineAsync(streamLine);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal completion on cancellation
                }
            }

            return isOk ? 0 : 1;
        }
        finally
        {
            client?.Dispose();
        }
    }
}
