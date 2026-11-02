namespace PcHwCtl.Core.Activity;

/// <summary>
/// Presence, off unless the config turns it on: is someone at the PC right now? One check per `presence` command, never
/// on a timer: the ESP32 helper decides when to ask and what an answer means (intrusion alert and so on). The
/// user-session agent grabs one webcam frame, runs YOLO26x person detection on it (NPU through OpenVINO, CPU fallback)
/// and wipes its own copies of the frame before it answers. Only the verdict leaves the agent; no image is stored or sent.
/// </summary>
public sealed class PresenceConfig
{
    public bool Enabled { get; set; }

    /// <summary>OpenVINO device, tried first; CPU when it fails.</summary>
    public string Device { get; set; } = "NPU";

    /// <summary>OpenVINO IR .xml (YOLO11/YOLO26, [1, 3, 640, 640] in, [1, 84, 8400] out); empty = the one shipped in models\.</summary>
    public string Model { get; set; } = "";

    /// <summary>Camera index for DirectShow, 0 = the first one.</summary>
    public int Camera { get; set; }

    /// <summary>Person confidence for "yes". npu-bench, YOLO26x in a near-black room: away at most 0.001, seated 0.55-0.93.</summary>
    public double MinConf { get; set; } = 0.4;

    /// <summary>Mean frame brightness (0-255) below which "nobody seen" becomes "unknown": the camera can't see. YOLO26x
    /// still finds a seated person at luma 1.6 on a C270, so only a covered lens or a black frame counts.</summary>
    public double DarkLuma { get; set; } = 0.5;

    /// <summary>Frames read and dropped while the camera's exposure settles (npu-bench used 26, about 1 s).</summary>
    public int SettleFrames { get; set; } = 26;

    /// <summary>Seconds a good answer stands: a sooner `presence` gets it again and the camera stays off.</summary>
    public int MinIntervalS { get; set; } = 10;

    /// <summary>When someone is seen, also estimate how far they are (YOLO26x-depth, about 0.1 s a frame on the NPU).</summary>
    public bool Depth { get; set; } = true;

    /// <summary>OpenVINO IR .xml of the depth model ([1, 3, 768, 768] in, [1, 1, 768, 768] out); empty = the shipped one.</summary>
    public string DepthModel { get; set; } = "";

    /// <summary>Frames whose readings are medianed: one frame swings about ±10% at 2 m, five steady it.</summary>
    public int DepthFrames { get; set; } = 5;

    /// <summary>Raw depth below which the zone is "desk". npu-bench, C270 under the monitor tilted up: seated 0.55 m
    /// read 0.455, standing 1.1 m behind a chair 0.585.</summary>
    public double DeskBelow { get; set; } = 0.52;

    /// <summary>Raw depth from which the zone is "room": 1.6 m read 0.797, 1.75 m 0.847. Raw is not metres: the model
    /// squeezes distance (2.0 m read 0.857) and can't tell 1.75 m from 2.0 m.</summary>
    public double RoomFrom { get; set; } = 0.80;

    /// <summary>Mean frame brightness below which zone is "-" (depth is still read): luma 15 still read right, at 6.4 one
    /// person's frames scattered 0.81-2.69.</summary>
    public double DepthMinLuma { get; set; } = 12;
}

/// <param name="State">yes, no or unknown.</param>
/// <param name="Why">"-", or why the state is unknown: dark, off, camera, model, agent.</param>
/// <param name="Device">Device the model ran on (NPU, CPU), "-" when it did not run.</param>
/// <param name="Zone">desk, near or room; "-" when depth is 0 or the frame is darker than DepthMinLuma.</param>
/// <param name="Depth">Raw depth-model reading at the person's head (median over DepthFrames); 0 when nobody was seen or depth is off or failed.</param>
public sealed record PresenceResult(string State, double Conf, double Luma, string Why, string Device, long Ms, string Zone = "-", double Depth = 0)
{
    public static PresenceResult Unknown(string why, long ms = 0) => new("unknown", 0, 0, why, "-", ms);
}

public interface IPresence
{
    /// <summary>Runs one check now (about 1-2 s: camera open, settle, one inference). Never throws.</summary>
    PresenceResult Check();
}

/// <summary>The best person: score and box (centre x, centre y, width, height) in the model's 640 x 640 input.</summary>
public readonly record struct PersonBox(double Conf, double Cx, double Cy, double W, double H);

public static class PresenceLogic
{
    public const int Size = 640;
    public const int Anchors = 8400;
    public const int Rows = 84;   // 4 box values + 80 COCO classes; class 0 (row 4) is person
    public const int DepthSize = 768;

    public static (string State, string Why) Decide(double conf, double luma, PresenceConfig cfg)
    {
        if (conf >= cfg.MinConf) return ("yes", "-");
        if (luma < cfg.DarkLuma) return ("unknown", "dark");
        return ("no", "-");
    }

    /// <summary>Best person in the [1, 84, 8400] output (YOLO11 and YOLO26 export alike): rows 0-3 the box, row 4 the
    /// person score, one column per anchor.</summary>
    public static PersonBox BestPerson(ReadOnlySpan<float> output)
    {
        if (output.Length != Rows * Anchors) throw new ArgumentException($"YOLO output has {output.Length} values, expected {Rows * Anchors}.");
        var scores = output.Slice(4 * Anchors, Anchors);
        int best = 0;
        for (int i = 1; i < Anchors; i++)
            if (scores[i] > scores[best]) best = i;
        return new PersonBox(Math.Max(0, scores[best]), output[best], output[Anchors + best], output[2 * Anchors + best], output[3 * Anchors + best]);
    }

    /// <summary>desk, near or room for a raw depth reading; "-" when the frame is too dark for depth.</summary>
    public static string Zone(double depth, double luma, PresenceConfig cfg) =>
        luma < cfg.DepthMinLuma ? "-" : depth < cfg.DeskBelow ? "desk" : depth < cfg.RoomFrom ? "near" : "room";

    /// <summary>
    /// Where the head is in the 768 x 768 depth map: the top centre of the person box (x 37.5-62.5%, y 5-25%), which
    /// read within 3% of YOLO26x-pose's head keypoint in npu-bench. The box is in the letterboxed 640 input of a
    /// width x height frame; the depth input is the whole frame stretched to 768 x 768. Never empty.
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1) HeadRegion(PersonBox box, int width, int height)
    {
        double s = Scale(width, height);
        double sw = Math.Round(width * s), sh = Math.Round(height * s);
        double padX = (Size - sw) / 2, padY = (Size - sh) / 2, fx = DepthSize / sw, fy = DepthSize / sh;
        double x1 = Math.Clamp((box.Cx - box.W / 2 - padX) * fx, 0, DepthSize), x2 = Math.Clamp((box.Cx + box.W / 2 - padX) * fx, 0, DepthSize);
        double y1 = Math.Clamp((box.Cy - box.H / 2 - padY) * fy, 0, DepthSize), y2 = Math.Clamp((box.Cy + box.H / 2 - padY) * fy, 0, DepthSize);
        double w = x2 - x1, h = y2 - y1;
        int rx0 = Math.Clamp((int)(x1 + 0.375 * w), 0, DepthSize - 1), ry0 = Math.Clamp((int)(y1 + 0.05 * h), 0, DepthSize - 1);
        int rx1 = Math.Clamp((int)(x1 + 0.625 * w) + 1, rx0 + 1, DepthSize), ry1 = Math.Clamp((int)(y1 + 0.25 * h) + 1, ry0 + 1, DepthSize);
        return (rx0, ry0, rx1, ry1);
    }

    /// <summary>Median of a region of the [1, 1, 768, 768] depth output.</summary>
    public static double RegionMedian(ReadOnlySpan<float> depth, (int X0, int Y0, int X1, int Y1) r)
    {
        if (depth.Length != DepthSize * DepthSize) throw new ArgumentException($"Depth output has {depth.Length} values, expected {DepthSize * DepthSize}.");
        var values = new float[(r.X1 - r.X0) * (r.Y1 - r.Y0)];
        int n = 0;
        for (int y = r.Y0; y < r.Y1; y++)
            foreach (float v in depth.Slice(y * DepthSize + r.X0, r.X1 - r.X0))
                values[n++] = v;
        return Median(values);
    }

    public static double Median(Span<float> values)
    {
        if (values.Length == 0) throw new ArgumentException("No values.");
        values.Sort();
        int m = values.Length / 2;
        return values.Length % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2.0;
    }

    /// <summary>Scale that fits a width x height frame into 640 x 640 (never above 1: small frames are padded, not enlarged).</summary>
    public static double Scale(int width, int height) => Math.Min(1.0, Math.Min((double)Size / width, (double)Size / height));

    /// <summary>
    /// Letterboxes a BGR image already scaled to fit 640 x 640 into the model input: RGB, channel-first, 0-1, centred
    /// and padded with YOLO's grey (114), as ultralytics and npu-bench do.
    /// </summary>
    public static void FillInput(ReadOnlySpan<byte> bgr, int width, int height, int stride, Span<float> input) =>
        Fill(bgr, width, height, stride, input, Size, true);

    /// <summary>The depth model's input: a BGR image already stretched to 768 x 768 (ultralytics' scale_fill), RGB,
    /// channel-first, 0-1, no padding.</summary>
    public static void FillDepthInput(ReadOnlySpan<byte> bgr, int stride, Span<float> input) =>
        Fill(bgr, DepthSize, DepthSize, stride, input, DepthSize, false);

    private static void Fill(ReadOnlySpan<byte> bgr, int width, int height, int stride, Span<float> input, int size, bool pad)
    {
        if (width > size || height > size) throw new ArgumentException($"Scale the image to fit {size} x {size} first.");
        if (input.Length != 3 * size * size) throw new ArgumentException($"Model input has {input.Length} values, expected {3 * size * size}.");
        if (pad) input.Fill(114f / 255f);
        int x0 = (size - width) / 2, y0 = (size - height) / 2, plane = size * size;
        for (int y = 0; y < height; y++)
        {
            var row = bgr.Slice(y * stride, width * 3);
            int o = (y0 + y) * size + x0;
            for (int x = 0; x < width; x++)
            {
                input[o + x] = row[x * 3 + 2] / 255f;              // R
                input[plane + o + x] = row[x * 3 + 1] / 255f;      // G
                input[2 * plane + o + x] = row[x * 3] / 255f;      // B
            }
        }
    }
}
