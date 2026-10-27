using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Display;

/// <summary>
/// DDC/CI through dxva2 plus Windows' display off and mouse nudge (docs/protocols/ddc-ci.md).
/// Only works in the user's session: session 0 has no monitors.
/// </summary>
public sealed partial class DdcDisplayBus : IDisplayBus
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public int Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private static readonly IntPtr HWND_BROADCAST = 0xFFFF;
    private static readonly IntPtr SC_MONITORPOWER = 0xF170;

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex ConnectorSuffix();

    public DisplayScan Scan()
    {
        var owned = new List<PHYSICAL_MONITOR[]>();
        var found = new List<IDisplayHandle>();
        MonitorEnumProc proc = (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint n) || n == 0) return true;
            var physical = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, n, physical)) return true;
            owned.Add(physical);
            foreach (var pm in physical)
            {
                // "DELL E1715S(DisplayPort)" -> "DELL E1715S"
                string name = ConnectorSuffix().Replace(pm.Description ?? "", "").Trim();
                found.Add(new Handle(pm.Handle, name.Length > 0 ? name : "Monitor", info.Left, (info.Flags & 1) != 0));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        return new DisplayScan(found, () =>
        {
            foreach (var physical in owned) DestroyPhysicalMonitors((uint)physical.Length, physical);
        });
    }

    public bool PostDisplayOff() => PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);

    public void NudgeMouse()
    {
        mouse_event(MOUSEEVENTF_MOVE, 0, 1, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_MOVE, 0, -1, 0, UIntPtr.Zero);
    }

    private sealed class Handle(IntPtr handle, string name, int left, bool main) : IDisplayHandle
    {
        public string Name { get; } = name;
        public int Left { get; } = left;
        public bool Main { get; } = main;

        public bool TryRead(byte code, out uint current, out uint maximum) =>
            GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out current, out maximum);

        public bool TryWrite(byte code, uint value) => SetVCPFeature(handle, code, value);
    }
}
