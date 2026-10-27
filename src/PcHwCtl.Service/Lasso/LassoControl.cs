using System.Diagnostics;
using System.Text.RegularExpressions;
using PcHwCtl.Core.Lasso;

namespace PcHwCtl.Service.Lasso;

/// <summary>
/// Process Lasso control, service side (needs SYSTEM for prolasso.ini and other users' process affinity). Every
/// change goes into the ini, so Process Lasso keeps it for processes started later, and onto the running processes
/// now. Also reads display-required power requests from `powercfg /requests` for the board.
/// </summary>
public sealed partial class LassoControl : ILassoControl, IDisposable
{
    private readonly Func<LassoConfig> _config;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile int _displayRequest = -1;
    private volatile string _displayRequestBy = "";

    public LassoControl(Func<LassoConfig> config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    private static long AllCpus => Environment.ProcessorCount >= 63 ? long.MaxValue : (1L << Environment.ProcessorCount) - 1;

    public LassoState State
    {
        get
        {
            int rules;
            try
            {
                rules = LassoIni.ParseAffinities(LassoIni.Value(ReadIni(), "DefaultAffinitiesEx")).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LassoException)
            {
                rules = -1;
            }
            return new LassoState(rules, _displayRequest, _displayRequestBy);
        }
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    PollDisplayRequests();
                }
                catch (Exception)
                {
                    _displayRequest = -1;
                }
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, _config().DisplayRequestPollS)));
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-DisplayRequests"
        };
        thread.Start();
    }

    public IReadOnlyList<(string Name, string Cpus)> Rules() =>
        LassoIni.ParseAffinities(LassoIni.Value(ReadIni(), "DefaultAffinitiesEx")).Select(r => (r.Name, r.Cpus)).ToList();

    public LassoResult SetAffinity(string exe, string cpus)
    {
        string name = LassoIni.ExeName(exe);
        string list = LassoIni.NormalizeCpus(cpus, Environment.ProcessorCount);
        lock (_gate)
        {
            WriteIni(LassoIni.SetRule(ReadIni(), name, list));
            int changed = Pin(LassoIni.RuleExe(name), LassoIni.CoreMask(list));
            return Result($"{name} on {list}", changed);
        }
    }

    public LassoResult ClearAffinity(string exe)
    {
        string name = LassoIni.ExeName(exe);
        lock (_gate)
        {
            var (text, removed) = LassoIni.RemoveRule(ReadIni(), name);
            if (!removed) return new LassoResult($"{name}: no rule", 0);
            WriteIni(text);
            int changed = Pin(LassoIni.RuleExe(name), AllCpus);
            return Result($"{name} rule removed, all CPUs", changed);
        }
    }

    /// <summary>The profile replaces prolasso.ini. Running processes follow the new rules; names whose rule went away get every CPU.</summary>
    public LassoResult Load(string profile)
    {
        string path = Path.Combine(_config().ProfilesDir, LassoIni.ProfileFile(profile));
        if (!File.Exists(path)) throw new LassoException($"no profile {path}");
        string next = File.ReadAllText(path); // BOM detected
        if (!next.Contains('[', StringComparison.Ordinal)) throw new LassoException($"{path} is not a Process Lasso ini.");

        lock (_gate)
        {
            var before = LassoIni.RuleMasks(ReadIni());
            var after = LassoIni.RuleMasks(next);
            File.Copy(path, _config().Ini, overwrite: true);

            int changed = 0;
            foreach (string exe in after.Keys.Union(before.Keys, StringComparer.OrdinalIgnoreCase))
            {
                changed += Pin(exe, after.TryGetValue(exe, out long mask) ? mask : AllCpus);
            }
            return Result($"loaded {path}", changed);
        }
    }

    public LassoResult Save(string profile)
    {
        string dir = _config().ProfilesDir;
        string path = Path.Combine(dir, LassoIni.ProfileFile(profile));
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            File.Copy(IniPath(), path, overwrite: true);
            return Result($"saved {path}", 0);
        }
    }

    public IReadOnlyList<string> Profiles()
    {
        string dir = _config().ProfilesDir;
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.ini").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string IniPath()
    {
        string ini = _config().Ini;
        if (!File.Exists(ini)) throw new LassoException($"no {ini}");
        return ini;
    }

    private string ReadIni() => File.ReadAllText(IniPath()); // UTF-16 with BOM, detected

    private void WriteIni(string text) => File.WriteAllText(_config().Ini, text, LassoIni.FileEncoding);

    /// <summary>Running processes named exe (no .exe) onto mask; returns how many changed.</summary>
    private static int Pin(string exe, long mask)
    {
        mask &= AllCpus;
        if (mask == 0) return 0;
        int changed = 0;
        foreach (var p in Process.GetProcessesByName(exe))
        {
            using (p)
            {
                try
                {
                    if ((long)p.ProcessorAffinity == mask) continue;
                    p.ProcessorAffinity = (IntPtr)mask;
                    changed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Protected or gone.
                }
            }
        }
        return changed;
    }

    private LassoResult Result(string message, int changed)
    {
        _log($"Lasso: {message} ({changed} running processes changed)");
        return new LassoResult(message, changed);
    }

    [GeneratedRegex(@"^[A-Z]+:$")]
    private static partial Regex SectionHeader();

    /// <summary>"DISPLAY:" then "None." or "[PROCESS] \Device\...\brave.exe" + reason lines, until the next "XYZ:" header.</summary>
    private void PollDisplayRequests()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/requests")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        string output;
        using (var p = Process.Start(psi)!)
        {
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0)
            {
                _displayRequest = -1;
                return;
            }
        }

        bool inDisplay = false;
        string? by = null;
        foreach (string raw in output.Split('\n'))
        {
            string l = raw.Trim();
            if (SectionHeader().IsMatch(l))
            {
                inDisplay = l == "DISPLAY:";
                continue;
            }
            if (inDisplay && by == null && l.StartsWith('['))
            {
                string what = l[(l.IndexOf(']') + 1)..].Trim();
                by = Path.GetFileName(what);
                if (string.IsNullOrEmpty(by)) by = what;
            }
        }
        _displayRequestBy = by ?? "";
        _displayRequest = by != null ? 1 : 0;
    }

    public void Dispose() => _cts.Cancel();
}
