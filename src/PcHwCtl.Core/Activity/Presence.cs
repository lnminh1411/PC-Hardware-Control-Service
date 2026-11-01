namespace PcHwCtl.Core.Activity;

/// <summary>
/// Presence: is someone at the PC right now? One check per `presence` command, never on a timer: the ESP32 helper
/// decides when to ask and what an answer means (intrusion alert and so on). The user-session agent grabs one webcam
/// frame, runs YOLO26x person detection on it (NPU through OpenVINO, CPU fallback) and wipes every copy of the frame
/// before it answers. Only the verdict leaves the agent; no image is stored or sent.
/// </summary>
public sealed class PresenceConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>OpenVINO device, tried first; CPU when it fails.</summary>
    public string Device { get; set; } = "NPU";

    /// <summary>OpenVINO IR .xml (YOLO11/YOLO26, [1, 3, 640, 640] in, [1, 84, 8400] out); empty = the one shipped in models\.</summary>
    public string Model { get; set; } = "";

    /// <summary>Camera index for DirectShow, 0 = the first one.</summary>
    public int Camera { get; set; }

    /// <summary>Person confidence for "yes". npu-bench, YOLO26x in a near-black room: away at most 0.001, seated 0.55-0.93.</summary>
    public double MinConf { get; set; } = 0.1;

    /// <summary>Mean frame brightness (0-255) below which "nobody seen" becomes "unknown": the camera can't see. YOLO26x
    /// still finds a seated person at luma 1.6 on a C270, so only a covered lens or a black frame counts.</summary>
    public double DarkLuma { get; set; } = 0.5;

    /// <summary>Frames read and dropped while the camera's exposure settles (npu-bench used 26, about 1 s).</summary>
    public int SettleFrames { get; set; } = 26;
}

/// <param name="State">yes, no or unknown.</param>
/// <param name="Why">"-", or why the state is unknown: dark, off, camera, model, agent.</param>
/// <param name="Device">Device the model ran on (NPU, CPU), "-" when it did not run.</param>
public sealed record PresenceResult(string State, double Conf, double Luma, string Why, string Device, long Ms)
{
    public static PresenceResult Unknown(string why, long ms = 0) => new("unknown", 0, 0, why, "-", ms);
}

public interface IPresence
{
    /// <summary>Runs one check now (about 1-2 s: camera open, settle, one inference). Never throws.</summary>
    PresenceResult Check();
}

public static class PresenceLogic
{
    public const int Size = 640;
    public const int Anchors = 8400;
    public const int Rows = 84;   // 4 box values + 80 COCO classes; class 0 (row 4) is person

    public static (string State, string Why) Decide(double conf, double luma, PresenceConfig cfg)
    {
        if (conf >= cfg.MinConf) return ("yes", "-");
        if (luma < cfg.DarkLuma) return ("unknown", "dark");
        return ("no", "-");
    }

    /// <summary>Best person score in the [1, 84, 8400] output (YOLO11 and YOLO26 export alike): row 4, one value per anchor.</summary>
    public static double MaxPersonConf(ReadOnlySpan<float> output)
    {
        if (output.Length != Rows * Anchors) throw new ArgumentException($"YOLO output has {output.Length} values, expected {Rows * Anchors}.");
        float max = 0;
        foreach (float v in output.Slice(4 * Anchors, Anchors))
            if (v > max) max = v;
        return max;
    }

    /// <inheritdoc cref="MaxPersonConf(ReadOnlySpan{float})"/>
    public static double MaxPersonConf(ReadOnlySpan<Half> output)
    {
        if (output.Length != Rows * Anchors) throw new ArgumentException($"YOLO output has {output.Length} values, expected {Rows * Anchors}.");
        float max = 0;
        foreach (Half v in output.Slice(4 * Anchors, Anchors))
            if ((float)v > max) max = (float)v;
        return max;
    }

    /// <summary>Scale that fits a width x height frame into 640 x 640 (never above 1: small frames are padded, not enlarged).</summary>
    public static double Scale(int width, int height) => Math.Min(1.0, Math.Min((double)Size / width, (double)Size / height));

    /// <summary>
    /// Letterboxes a BGR image already scaled to fit 640 x 640 into the model input: RGB, channel-first, 0-1, centred
    /// and padded with YOLO's grey (114), as ultralytics and npu-bench do.
    /// </summary>
    public static void FillInput(ReadOnlySpan<byte> bgr, int width, int height, int stride, Span<float> input)
    {
        if (width > Size || height > Size) throw new ArgumentException("Scale the image to fit 640 x 640 first.");
        if (input.Length != 3 * Size * Size) throw new ArgumentException($"Model input has {input.Length} values, expected {3 * Size * Size}.");
        input.Fill(114f / 255f);
        int x0 = (Size - width) / 2, y0 = (Size - height) / 2, plane = Size * Size;
        for (int y = 0; y < height; y++)
        {
            var row = bgr.Slice(y * stride, width * 3);
            int o = (y0 + y) * Size + x0;
            for (int x = 0; x < width; x++)
            {
                input[o + x] = row[x * 3 + 2] / 255f;              // R
                input[plane + o + x] = row[x * 3 + 1] / 255f;      // G
                input[2 * plane + o + x] = row[x * 3] / 255f;      // B
            }
        }
    }
}
