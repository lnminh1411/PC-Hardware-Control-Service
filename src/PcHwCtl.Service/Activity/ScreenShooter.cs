using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Activity;

/// <summary>What the agent hands the service: a PNG of every monitor, or why there is none. The caller clears Png.</summary>
public sealed record ShotCapture(string Why, byte[]? Png)
{
    public static ShotCapture Declined(string why) => new(why, null);
}

/// <summary>
/// The user-session half of a screenshot: every monitor in one PNG, in memory only, then a red border with
/// "Screenshot Captured" on every monitor so it is never secret. Refuses when nobody could see the border: the
/// displays are off (Windows' console display state, or every monitor powered off over DDC), or the desktop is not
/// the input desktop (locked, UAC prompt). Never holds the webhook: the service posts the PNG (ScreenUploader).
/// </summary>
public sealed class ScreenShooter : IDisposable
{
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly Func<ScreenshotConfig> _config;
    private readonly Func<IReadOnlyList<DisplayState>> _monitors;
    private readonly Action<string> _log;
    private readonly DisplayStateWindow _displayState;
    private int _busy;

    public ScreenShooter(Func<ScreenshotConfig> config, Func<IReadOnlyList<DisplayState>> monitors, Action<string> log)
    {
        _config = config;
        _monitors = monitors;
        _log = log;
        _displayState = new DisplayStateWindow(log);
    }

    /// <summary>Captures, then shows the border and returns once it is painted (it stays up BorderS). Never throws.</summary>
    public ShotCapture Capture(string nonce)
    {
        var cfg = _config();
        if (!cfg.Enabled) return ShotCapture.Declined("off");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return ShotCapture.Declined("busy");
        try
        {
            if (_displayState.State == 0 || _monitors() is { Count: > 0 } m && m.All(d => d.Power == 0))
            {
                _log($"Screenshot {nonce}: displays are off, nobody would see the border, declined.");
                return ShotCapture.Declined("display");
            }
            if (!OnInputDesktop())
            {
                _log($"Screenshot {nonce}: desktop locked or a secure prompt is up, declined.");
                return ShotCapture.Declined("locked");
            }

            byte[] png;
            try
            {
                png = Grab();   // before the border, so the border is never in the image
            }
            catch (Exception ex)
            {
                _log($"Screenshot {nonce}: capture failed: {ex.Message}");
                return ShotCapture.Declined("capture");
            }
            if (!ShowBorder(cfg.BorderS))
            {
                Array.Clear(png);
                _log($"Screenshot {nonce}: border did not show, discarded.");
                return ShotCapture.Declined("capture");
            }
            _log($"Screenshot {nonce}: captured {png.Length / 1024} KiB, border up for {cfg.BorderS} s.");
            return new ShotCapture("-", png);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => _displayState.Dispose();

    /// <summary>The whole virtual desktop (every monitor) as one PNG, in physical pixels. Its pixels are cleared after.</summary>
    private static byte[] Grab()
    {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) throw new InvalidOperationException("no screen metrics (locked or no desktop?)");
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            // Sized for the raw pixels, so the stream doesn't grow and leave copies behind in old buffers.
            using var ms = new MemoryStream((int)Math.Min(w * 3L * h + 65536, Array.MaxLength));
            try
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                Array.Clear(ms.GetBuffer());
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Black);
            }
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }

    /// <summary>The input desktop is this session's Default one: not locked, no UAC or Ctrl+Alt+Del screen up.</summary>
    private static bool OnInputDesktop()
    {
        IntPtr desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desk == IntPtr.Zero) return false;   // Winlogon's secure desktop: access denied
        try
        {
            var name = new char[64];
            if (!GetUserObjectInformation(desk, UOI_NAME, name, name.Length * 2, out int needed)) return false;
            return new string(name, 0, Math.Max(0, needed / 2 - 1)).Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desk);
        }
    }

    /// <summary>
    /// A click-through, topmost, borderless window per monitor: thick red frame and a centred red "Screenshot
    /// Captured", for seconds. Own STA thread with a short message loop; returns once every window is painted, or
    /// false when that failed.
    /// </summary>
    private bool ShowBorder(int seconds)
    {
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                var forms = Screen.AllScreens.Select(s => new BorderForm(s.Bounds)).ToList();
                if (forms.Count == 0)
                {
                    shown.TrySetResult(false);
                    return;
                }
                foreach (var f in forms)
                {
                    f.Show();
                    f.Refresh();
                }
                shown.TrySetResult(true);
                using var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    foreach (var f in forms) f.Close();
                    Application.ExitThread();
                };
                timer.Start();
                Application.Run();
                foreach (var f in forms) f.Dispose();
            }
            catch (Exception ex)
            {
                _log($"Screenshot: border failed: {ex.Message}");
                shown.TrySetResult(false);
            }
        }) { IsBackground = true, Name = "PcHwCtl-ShotBorder" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        return shown.Task.Wait(TimeSpan.FromSeconds(5)) && shown.Task.Result;
    }

    private sealed class BorderForm : Form
    {
        public BorderForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;   // only the painted pixels show
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int t = Math.Max(8, Width / 180);
            using (var pen = new Pen(Color.Red, t) { Alignment = PenAlignment.Inset })
                e.Graphics.DrawRectangle(pen, t / 2, t / 2, Width - t, Height - t);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI", Math.Max(24, Height / 16), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Red);
            const string msg = "Screenshot Captured";
            SizeF sz = e.Graphics.MeasureString(msg, font);
            e.Graphics.DrawString(msg, font, brush, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
        }
    }

    /// <summary>
    /// Windows' console display state (GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed), from a message-only
    /// window that Windows notifies on every change and once at registration. -1 until then.
    /// </summary>
    private sealed class DisplayStateWindow : IDisposable
    {
        private volatile int _state = -1;
        private Form? _form;

        public DisplayStateWindow(Action<string> log)
        {
            var ui = new Thread(() =>
            {
                try
                {
                    _form = new Listener(this);
                    _ = _form.Handle;   // creates the window
                    var guid = ConsoleDisplayState;
                    if (RegisterPowerSettingNotification(_form.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE) == IntPtr.Zero)
                        log($"Screenshot: display state notifications unavailable ({Marshal.GetLastWin32Error()}).");
                    Application.Run();
                }
                catch (Exception ex)
                {
                    log($"Screenshot: display state window failed: {ex.Message}");
                }
            }) { IsBackground = true, Name = "PcHwCtl-DisplayState" };
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
        }

        public int State => _state;

        public void Dispose()
        {
            try
            {
                _form?.BeginInvoke(Application.ExitThread);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private sealed class Listener(DisplayStateWindow owner) : Form
        {
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Parent = HWND_MESSAGE;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                    if (setting.PowerSetting == ConsoleDisplayState && setting.DataLength >= 4) owner._state = setting.Data;
                }
                base.WndProc(ref m);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public int Data;
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int WM_POWERBROADCAST = 0x218, PBT_POWERSETTINGCHANGE = 0x8013, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;
    private static readonly IntPtr PerMonitorAwareV2 = -4;
    private static readonly IntPtr HWND_MESSAGE = -3;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] char[] info, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
