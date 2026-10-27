using System.Diagnostics;
using System.Runtime.InteropServices;
using PcHwCtl.Core.Lasso;

namespace PcHwCtl.Service.Lasso;

/// <summary>
/// Process Lasso control, service side (needs SYSTEM for prolasso.ini and other users' processes). Every affinity
/// change goes into the ini, so Process Lasso keeps it for processes started later, and onto the running processes
/// now. Efficiency mode is set on running processes and swept onto new instances every EcoSweepS.
/// </summary>
public sealed class LassoControl : ILassoControl, IDisposable
{
    private readonly Func<LassoConfig> _config;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    // exe (no .exe) -> pid -> (create time, the process's own setting before ours)
    private readonly Dictionary<string, Dictionary<int, (DateTime Created, uint Control, uint State)>> _eco = new(StringComparer.OrdinalIgnoreCase);

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
            lock (_gate) return new LassoState(rules, _eco.Keys.Select(k => k + ".exe").Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, _config().EcoSweepS)));
                try
                {
                    lock (_gate)
                    {
                        foreach (string exe in _eco.Keys.ToList()) EcoApply(exe);
                    }
                }
                catch (Exception ex)
                {
                    _log($"Lasso: efficiency sweep failed: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-EcoSweep"
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

    public LassoResult Eco(string exe, bool on)
    {
        string name = LassoIni.ExeName(exe);
        string key = LassoIni.RuleExe(name);
        lock (_gate)
        {
            if (on)
            {
                if (!_eco.ContainsKey(key)) _eco[key] = new();
                return Result($"{name} efficiency mode on", EcoApply(key));
            }
            if (!_eco.Remove(key, out var held)) return new LassoResult($"{name}: efficiency mode was not on", 0);
            return Result($"{name} efficiency mode off", EcoRestore(held));
        }
    }

    public LassoResult EcoOffAll()
    {
        lock (_gate)
        {
            int changed = 0;
            foreach (var held in _eco.Values) changed += EcoRestore(held);
            int exes = _eco.Count;
            _eco.Clear();
            return Result($"efficiency mode off for {exes} exes", changed);
        }
    }

    /// <summary>Instances of exe not yet in efficiency mode get it; their own setting is kept for off. Under _gate.</summary>
    private int EcoApply(string exe)
    {
        var held = _eco[exe];
        int changed = 0;
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(exe))
        {
            using (p)
            {
                alive.Add(p.Id);
                DateTime created;
                try
                {
                    created = p.StartTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    continue; // protected or gone
                }
                if (held.TryGetValue(p.Id, out var h) && h.Created == created) continue;
                if (!EcoQos.Get(p.Id, out uint control, out uint state)) continue;
                if (!EcoQos.Set(p.Id, EcoQos.ExecutionSpeed, EcoQos.ExecutionSpeed)) continue;
                held[p.Id] = (created, control, state);
                changed++;
            }
        }
        foreach (int pid in held.Keys.Where(pid => !alive.Contains(pid)).ToList()) held.Remove(pid);
        return changed;
    }

    private static int EcoRestore(Dictionary<int, (DateTime Created, uint Control, uint State)> held)
    {
        int changed = 0;
        foreach (var (pid, h) in held)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime != h.Created) continue; // pid reused
                if (EcoQos.Set(pid, h.Control, h.State)) changed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone.
            }
        }
        return changed;
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

    public void Dispose() => _cts.Cancel();

    /// <summary>ProcessPowerThrottling: ExecutionSpeed in both masks = efficiency mode; the process's own masks otherwise.</summary>
    private static class EcoQos
    {
        public const uint ExecutionSpeed = 1;
        private const int ProcessPowerThrottling = 4;
        private const uint ProcessSetInformation = 0x0200, ProcessQueryLimitedInformation = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public uint Version, ControlMask, StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

        public static bool Get(int pid, out uint control, out uint state)
        {
            control = state = 0;
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var s = new State { Version = 1 };
                if (!GetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<State>())) return false;
                control = s.ControlMask;
                state = s.StateMask;
                return true;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static bool Set(int pid, uint control, uint state)
        {
            IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var s = new State { Version = 1, ControlMask = control, StateMask = state };
                return SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<State>());
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
