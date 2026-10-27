using System.Runtime.InteropServices;
using System.Text;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// Per-exe CPU from one NtQuerySystemInformation(SystemProcessInformation) call per second: no process handles,
/// so protected processes count too, and ~480 processes cost about a millisecond.
/// </summary>
public sealed class ProcessCpuSampler : IProcessActivity, IFeedSource, IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<ProcessLoad> _loads = [];
    private bool _valid;
    private int _bufferSize = 1 << 20;

    public ProcessCpuSampler(Action<string> log)
    {
        _log = log;
    }

    public void Start()
    {
        var thread = new Thread(() =>
        {
            List<ProcessSample>? before = null;
            long beforeTicks = 0;
            string? lastError = null;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var now = Read();
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (before != null)
                    {
                        double s = (ticks - beforeTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                        _loads = ProcessCpu.Loads(before, now, s);
                        _valid = true;
                    }
                    before = now;
                    beforeTicks = ticks;
                    lastError = null;
                }
                catch (Exception ex)
                {
                    _valid = false;
                    if (lastError != ex.Message) _log($"Process CPU: {ex.Message}");
                    lastError = ex.Message;
                }
                _cts.Token.WaitHandle.WaitOne(1000);
            }
        })
        {
            IsBackground = true,
            Name = "PcHwCtl-ProcessCpu"
        };
        thread.Start();
    }

    public IReadOnlyList<ProcessLoad> Top(int n) => _loads.Take(Math.Max(0, n)).ToList();

    /// <summary>proc_top (cores) and proc_top_by (exe) of the busiest program; missing until two samples exist.</summary>
    public void AddTo(List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts)
    {
        var top = _valid ? _loads.FirstOrDefault() : null;
        values.Add(new("proc_top", top == null ? null : Math.Round(top.Cores, 2)));
        texts.Add(new("proc_top_by", top?.Exe ?? ""));
    }

    // SYSTEM_PROCESS_INFORMATION, x64
    private const int OffNext = 0, OffCreateTime = 32, OffUserTime = 40, OffKernelTime = 48, OffImageLength = 56, OffImageBuffer = 64, OffPid = 80;

    private List<ProcessSample> Read()
    {
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(_bufferSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, _bufferSize, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    _bufferSize = Math.Max(_bufferSize * 2, needed + (64 << 10));
                    continue;
                }
                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation status 0x{status:X8}");

                var list = new List<ProcessSample>(512);
                int off = 0;
                while (true)
                {
                    IntPtr e = buf + off;
                    int pid = (int)Marshal.ReadIntPtr(e, OffPid);
                    long cpu = Marshal.ReadInt64(e, OffUserTime) + Marshal.ReadInt64(e, OffKernelTime);
                    long created = Marshal.ReadInt64(e, OffCreateTime);
                    int len = Marshal.ReadInt16(e, OffImageLength) & 0xFFFF;
                    IntPtr name = Marshal.ReadIntPtr(e, OffImageBuffer);
                    string exe = pid == 0 ? "idle" : len > 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name, len / 2) : "system";
                    list.Add(new ProcessSample(pid, created, exe.ToLowerInvariant(), cpu));

                    int next = Marshal.ReadInt32(e, OffNext);
                    if (next == 0) break;
                    off += next;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
