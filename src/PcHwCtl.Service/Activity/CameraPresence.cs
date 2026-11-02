using System.Diagnostics;
using OpenCvSharp;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// One presence check: open the webcam (DirectShow, as npu-bench's cv2 did), drop SettleFrames while exposure
/// settles, keep one frame, close the camera, run YOLO26x, then zero the frame, its scaled copy and the model input.
/// Frames live only in this process's memory; nothing is written to disk or returned but the verdict. Buffers inside
/// the DirectShow backend and the NPU plugin are out of reach and not wiped.
/// Runs in the user-session agent (session 0 has no camera access), or in the console service.
/// </summary>
public sealed unsafe class CameraPresence : IPresence, IDisposable
{
    private readonly Func<PresenceConfig> _config;
    private readonly Action<string> _log;
    private readonly string _cacheDir;
    private readonly object _lock = new();
    private YoloModel? _model;
    private string? _lastError;
    private PresenceResult? _last;
    private long _nextCheckMs;

    public CameraPresence(Func<PresenceConfig> config, Action<string> log)
    {
        _config = config;
        _log = log;
        // OpenVINO loads compiled blobs from the cache: an elevated console must not read one that unelevated code wrote.
        bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        _cacheDir = Path.Combine(Environment.GetFolderPath(elevated ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData), "PcHwCtl", "ovcache");
    }

    public static string DefaultModel => Path.Combine(AppContext.BaseDirectory, "models", "yolo26x-fp16-ov", "yolo26x.xml");

    public PresenceResult Check()
    {
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            var cfg = _config();
            if (!cfg.Enabled) return PresenceResult.Unknown("off");
            if (_last != null && Environment.TickCount64 < _nextCheckMs) return _last;   // no client keeps the camera on
            try
            {
                var model = Model(cfg);
                if (model == null) return PresenceResult.Unknown("model", sw.ElapsedMilliseconds);
                var r = Run(cfg, model, sw);
                if (_lastError != null) _log("Presence: checks work again.");
                _lastError = null;
                _last = r;
                _nextCheckMs = Environment.TickCount64 + cfg.MinIntervalS * 1000L;
                return r;
            }
            catch (Exception ex)
            {
                string why = ex is CameraException ? "camera" : "model";
                if (_lastError != ex.Message) _log($"Presence: check failed ({why}): {ex.Message}");
                _lastError = ex.Message;
                return PresenceResult.Unknown(why, sw.ElapsedMilliseconds);
            }
        }
    }

    private PresenceResult Run(PresenceConfig cfg, YoloModel model, Stopwatch sw)
    {
        using var frame = new Mat();
        Mat? scaled = null;
        try
        {
            using (var cap = new VideoCapture(cfg.Camera, VideoCaptureAPIs.DSHOW))
            {
                if (!cap.IsOpened()) throw new CameraException($"camera {cfg.Camera} did not open (in use, unplugged, or camera access off for desktop apps)");
                cap.Set(VideoCaptureProperties.FrameWidth, 640);
                cap.Set(VideoCaptureProperties.FrameHeight, 480);
                for (int i = 0; i < cfg.SettleFrames; i++) cap.Read(frame);   // same buffer each time
                if (!cap.Read(frame) || frame.Empty()) throw new CameraException($"camera {cfg.Camera} gave no frame");
            }   // camera released before inference: the LED goes off as soon as possible
            if (frame.Type() != MatType.CV_8UC3 || !frame.IsContinuous()) throw new CameraException($"unexpected frame format {frame.Type()}");

            var mean = Cv2.Mean(frame);
            double luma = (mean.Val0 + mean.Val1 + mean.Val2) / 3;

            double s = PresenceLogic.Scale(frame.Width, frame.Height);
            Mat src = frame;
            if (s < 1)
            {
                scaled = new Mat();
                Cv2.Resize(frame, scaled, new Size((int)Math.Round(frame.Width * s), (int)Math.Round(frame.Height * s)), 0, 0, InterpolationFlags.Area);
                src = scaled;
            }
            IntPtr pixels = src.Data;
            int w = src.Width, h = src.Height, stride = (int)src.Step(), length = checked(stride * h);
            double conf = model.Infer(input => PresenceLogic.FillInput(new ReadOnlySpan<byte>((void*)pixels, length), w, h, stride, input));

            var (state, why) = PresenceLogic.Decide(conf, luma, cfg);
            return new PresenceResult(state, conf, luma, why, model.Device, sw.ElapsedMilliseconds);
        }
        finally
        {
            Wipe(scaled);
            Wipe(frame);
        }
    }

    /// <summary>
    /// The shipped model is the one the repo pins (models/**/*.xml is -text in git, so its bytes match on every
    /// checkout). OpenVINO parses both files natively, so a swapped file is refused before it is read.
    /// </summary>
    private static void CheckShipped(string xml)
    {
        foreach (var (file, sha) in new[] { (xml, ShippedXmlSha256), (Path.ChangeExtension(xml, ".bin"), ShippedBinSha256) })
        {
            using var stream = File.OpenRead(file);
            string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            if (actual != sha) throw new InvalidOperationException($"{Path.GetFileName(file)} is not the shipped model (SHA-256 {actual[..12]}...)");
        }
    }

    private const string ShippedXmlSha256 = "3603de4e62c464a83d579cee72cf64a454eed86b8080819c930b7c2fa236c797";
    private const string ShippedBinSha256 = "4cb93aa68fe28a8b9ba92ddde57a827a831ec6dfdf99ec261ad3a0a71da31250";

    /// <summary>Zeroes the pixels before OpenCV frees them, so this copy is not left in freed memory.</summary>
    private static void Wipe(Mat? mat)
    {
        if (mat == null || mat.IsDisposed) return;
        if (!mat.Empty()) mat.SetTo(Scalar.All(0));
        mat.Dispose();
    }

    private YoloModel? Model(PresenceConfig cfg)
    {
        string path = string.IsNullOrWhiteSpace(cfg.Model) ? DefaultModel : cfg.Model;
        // Keyed by the configured device: a model that fell back to CPU stays until the config changes.
        if (_model != null && _modelKey == (path, cfg.Device)) return _model;
        _model?.Dispose();
        _model = null;
        var sw = Stopwatch.StartNew();
        if (path == DefaultModel) CheckShipped(path);
        _model = YoloModel.Load(path, cfg.Device, _cacheDir, _log);
        _modelKey = (path, cfg.Device);
        _log($"Presence: YOLO ready on {_model.Device} in {sw.ElapsedMilliseconds} ms.");
        return _model;
    }

    private (string Path, string Device) _modelKey;

    public void Dispose()
    {
        lock (_lock)
        {
            _model?.Dispose();
            _model = null;
        }
    }

    private sealed class CameraException(string message) : Exception(message);
}
