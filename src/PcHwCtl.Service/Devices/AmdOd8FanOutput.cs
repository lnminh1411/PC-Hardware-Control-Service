using System.Runtime.InteropServices;
using PcHwCtl.Core;

namespace PcHwCtl.Service.Devices;

/// <summary>
/// RX 6600 fan through ADL Overdrive8 (docs/protocols/amd-od8.md): writes the 5-point firmware fan curve and the
/// driver Zero RPM flag. Only fan entries are requested; clocks, power and voltage pass through untouched.
/// Used from the fan tick thread only.
/// </summary>
public sealed class AmdOd8FanOutput : INativeFanOutput, IDisposable
{
    private const int TableCount = 72;
    private const int ZeroRpmIndex = 15;
    private const int CurveFirstIndex = 19; // temperature 1; speed 1 at 20, temperature 2 at 21 ... speed 5 at 28
    private const int CurvePoints = 5;

    private delegate IntPtr AdlMalloc(int size);

    // Kept in a static so the GC never collects the callback ADL holds.
    private static readonly AdlMalloc Malloc = size => Marshal.AllocCoTaskMem(size);

    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Create(AdlMalloc callback, int enumConnectedAdapters, out IntPtr context);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Destroy(IntPtr context);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive_Caps(IntPtr context, int adapter, out int supported, out int enabled, out int version);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Init_Setting_Get(IntPtr context, int adapter, int[] initSetting);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Current_Setting_Get(IntPtr context, int adapter, int[] currentSetting);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Setting_Set(IntPtr context, int adapter, int[] setSetting, int[] currentSetting);

    private readonly IntPtr _context;
    private readonly int _adapter;
    private readonly int[] _min = new int[TableCount];
    private readonly int[] _max = new int[TableCount];
    private bool _disposed;

    private AmdOd8FanOutput(IntPtr context, int adapter, int[] init)
    {
        _context = context;
        _adapter = adapter;
        for (int i = 0; i < TableCount; i++)
        {
            // ADLOD8InitSetting: count, capabilities, then { featureID, min, max, default } per entry
            _min[i] = init[2 + i * 4 + 1];
            _max[i] = init[2 + i * 4 + 2];
        }
    }

    /// <summary>Opens the first adapter with a readable Overdrive8 table, or returns null (logged).</summary>
    public static AmdOd8FanOutput? TryCreate(Action<string> log)
    {
        IntPtr context;
        try
        {
            if (ADL2_Main_Control_Create(Malloc, 1, out context) != 0)
            {
                log("AMD Overdrive8: ADL init failed.");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            log($"AMD Overdrive8: atiadlxx.dll unusable: {ex.Message}");
            return null;
        }

        ADL2_Adapter_NumberOfAdapters_Get(context, out int count);
        for (int adapter = 0; adapter < count; adapter++)
        {
            if (ADL2_Overdrive_Caps(context, adapter, out _, out _, out int version) != 0 || version != 8) continue;

            int[] init = NewTable(2 + TableCount * 4);
            int[] current = NewTable(1 + TableCount);
            if (ADL2_Overdrive8_Init_Setting_Get(context, adapter, init) != 0 ||
                ADL2_Overdrive8_Current_Setting_Get(context, adapter, current) != 0) continue;

            log($"AMD Overdrive8: adapter {adapter}, caps 0x{init[1]:X}, Zero RPM {current[1 + ZeroRpmIndex]}, " +
                $"curve {string.Join(" ", Enumerable.Range(0, CurvePoints).Select(p => $"{current[1 + CurveFirstIndex + p * 2]}/{current[2 + CurveFirstIndex + p * 2]}"))}");
            return new AmdOd8FanOutput(context, adapter, init);
        }

        ADL2_Main_Control_Destroy(context);
        log("AMD Overdrive8: no adapter with a readable Overdrive8 table.");
        return null;
    }

    public void Apply(string controlId, NativeFanCurve curve)
    {
        if (curve.Points.Count != CurvePoints) throw new ArgumentException($"Overdrive8 needs {CurvePoints} curve points, got {curve.Points.Count}.");

        var values = new Dictionary<int, int> { [ZeroRpmIndex] = curve.ZeroRpm ? 1 : 0 };
        for (int p = 0; p < CurvePoints; p++)
        {
            values[CurveFirstIndex + p * 2] = (int)MathF.Round(curve.Points[p].TempC);
            values[CurveFirstIndex + p * 2 + 1] = (int)MathF.Round(curve.Points[p].Pct);
        }
        Write(values, reset: false);
    }

    public void Reset(string controlId)
    {
        var values = new Dictionary<int, int> { [ZeroRpmIndex] = 0 };
        for (int i = CurveFirstIndex; i < CurveFirstIndex + CurvePoints * 2; i++) values[i] = 0;
        Write(values, reset: true);
    }

    private void Write(Dictionary<int, int> values, bool reset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int[] current = NewTable(1 + TableCount);
        int rc = ADL2_Overdrive8_Current_Setting_Get(_context, _adapter, current);
        if (rc != 0) throw new InvalidOperationException($"Overdrive8 read failed ({rc}).");

        // ADLOD8SetSetting: count, then { value, requested, reset } per entry
        int[] set = NewTable(1 + TableCount * 3);
        for (int i = 0; i < TableCount; i++)
        {
            bool mine = values.TryGetValue(i, out int value);
            set[1 + i * 3] = mine ? Math.Clamp(value, _min[i], _max[i]) : current[1 + i];
            set[2 + i * 3] = mine ? 1 : 0;
            set[3 + i * 3] = mine && reset ? 1 : 0;
        }

        rc = ADL2_Overdrive8_Setting_Set(_context, _adapter, set, NewTable(1 + TableCount));
        if (rc != 0) throw new InvalidOperationException($"Overdrive8 write failed ({rc}).");
    }

    private static int[] NewTable(int length)
    {
        var table = new int[length];
        table[0] = TableCount;
        return table;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ADL2_Main_Control_Destroy(_context);
    }
}
