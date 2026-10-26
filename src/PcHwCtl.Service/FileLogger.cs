namespace PcHwCtl.Service;

public sealed class FileLogger
{
    private readonly string _logPath;
    private readonly string _oldLogPath;
    private readonly object _lock = new();
    private readonly bool _consoleOutput;

    public FileLogger(string? customDir = null, bool consoleOutput = false)
    {
        _consoleOutput = consoleOutput;
        string dir = customDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcHwCtl");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Directory creation error will be handled on write
        }

        _logPath = Path.Combine(dir, "log.txt");
        _oldLogPath = Path.Combine(dir, "log.old.txt");
    }

    public void Log(string message)
    {
        string timestamped = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        if (_consoleOutput)
        {
            Console.WriteLine(timestamped);
        }

        lock (_lock)
        {
            try
            {
                var fileInfo = new FileInfo(_logPath);
                if (fileInfo.Exists && fileInfo.Length >= 4 * 1024 * 1024)
                {
                    File.Copy(_logPath, _oldLogPath, overwrite: true);
                    File.Delete(_logPath);
                }

                File.AppendAllText(_logPath, timestamped + Environment.NewLine);
            }
            catch
            {
                // Suppress file I/O logging errors to prevent crash
            }
        }
    }
}
