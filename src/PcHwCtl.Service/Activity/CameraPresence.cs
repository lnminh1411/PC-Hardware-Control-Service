using System.Diagnostics;
using OpenCvSharp;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// One presence check: open the webcam (DirectShow, as npu-bench's cv2 did), drop SettleFrames while exposure
/// settles, keep one frame, close the camera, run YOLO11n, then zero the frame, its scaled copy and the model input.
/// Frames live only in this process's memory; nothing is written to disk or returned but the verdict.
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

    public CameraPresence(Func<PresenceConfig> config, Action<string> log)
    {
        _config = config;
        _log = log;
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcHwCtl", "ovcache");
    }

    public static string DefaultModel => Path.Combine(AppContext.BaseDirectory, "models", "yolo11n-fp16-ov", "yolo11n.xml");

    public PresenceResult Check()
    {
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            var cfg = _config();
            if (!cfg.Enabled) return PresenceResult.Unknown("off");
            try
            {
                var model = Model(cfg);
                if (model == null) return PresenceResult.Unknown("model", sw.ElapsedMilliseconds);
                var r = Run(cfg, model, sw);
                if (_lastError != null) _log("Presence: checks work again.");
                _lastError = null;
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

    /// <summary>Zeroes the pixels before OpenCV frees them, so no frame is left in freed memory.</summary>
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
        _model = YoloModel.Load(path, cfg.Device, _cacheDir, _log);
        _modelKey = (path, cfg.Device);
        _log($"Presence: YOLO11n ready on {_model.Device} in {sw.ElapsedMilliseconds} ms.");
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
