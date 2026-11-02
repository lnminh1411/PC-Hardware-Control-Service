using System.Runtime.InteropServices;
using PcHwCtl.Core.Activity;

namespace PcHwCtl.Service.Activity;

/// <summary>
/// YOLO (YOLO26x and YOLO26x-depth by default) on OpenVINO through its C API (openvino_c.dll from the OpenVINO.runtime.win package), just the calls one
/// static-shape model needs. Compiled once and kept; the input tensor is wiped after every inference.
/// </summary>
public sealed unsafe class YoloModel : IDisposable
{
    private const string Lib = "openvino_c";

    [DllImport(Lib)] private static extern int ov_core_create(out IntPtr core);
    [DllImport(Lib)] private static extern void ov_core_free(IntPtr core);
    // Variadic in C (property key, value, ...); on x64 the extra arguments pass like ordinary ones.
    [DllImport(Lib)] private static extern int ov_core_compile_model_from_file(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string device, nuint propertyArgs, out IntPtr compiled,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern void ov_compiled_model_free(IntPtr compiled);
    [DllImport(Lib)] private static extern int ov_compiled_model_create_infer_request(IntPtr compiled, out IntPtr request);
    [DllImport(Lib)] private static extern void ov_infer_request_free(IntPtr request);
    [DllImport(Lib)] private static extern int ov_infer_request_get_input_tensor_by_index(IntPtr request, nuint index, out IntPtr tensor);
    [DllImport(Lib)] private static extern int ov_infer_request_get_output_tensor_by_index(IntPtr request, nuint index, out IntPtr tensor);
    [DllImport(Lib)] private static extern int ov_infer_request_infer(IntPtr request);
    [DllImport(Lib)] private static extern int ov_tensor_data(IntPtr tensor, out IntPtr data);
    [DllImport(Lib)] private static extern int ov_tensor_get_size(IntPtr tensor, out nuint size);
    [DllImport(Lib)] private static extern int ov_tensor_get_byte_size(IntPtr tensor, out nuint bytes);
    [DllImport(Lib)] private static extern void ov_tensor_free(IntPtr tensor);
    [DllImport(Lib)] private static extern IntPtr ov_get_last_err_msg();

    private IntPtr _core, _compiled, _request;

    public string Path { get; }
    public string Device { get; }

    private YoloModel(string path, string device)
    {
        Path = path;
        Device = device;
    }

    /// <summary>Compiles on device, then on CPU when that fails. Throws when both fail.</summary>
    public static YoloModel Load(string path, string device, string cacheDir, Action<string> log)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"model {path} not found");
        Directory.CreateDirectory(cacheDir);
        var devices = device.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? ["CPU"] : new[] { device, "CPU" };
        Exception? last = null;
        foreach (string d in devices)
        {
            var model = new YoloModel(path, d);
            try
            {
                Check(ov_core_create(out model._core), "core");
                Check(ov_core_compile_model_from_file(model._core, path, d, 2, out model._compiled, "CACHE_DIR", cacheDir), $"compile on {d}");
                Check(ov_compiled_model_create_infer_request(model._compiled, out model._request), "infer request");
                return model;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                model.Dispose();
                log($"Presence: YOLO on {d} failed: {ex.Message}");
                last = ex;
            }
        }
        throw new InvalidOperationException(last?.Message ?? "no device");
    }

    /// <summary>
    /// fill writes the frame into the input; read gets output 0 as f32 (f16 outputs are widened into a copy) and
    /// returns the answer. The input tensor and any copy are zeroed afterwards, whatever happens.
    /// </summary>
    public T Infer<T>(SpanAction fill, OutputFunc<T> read)
    {
        float[]? wide = null;
        IntPtr input = IntPtr.Zero, output = IntPtr.Zero;
        Span<float> data = default;
        try
        {
            Check(ov_infer_request_get_input_tensor_by_index(_request, 0, out input), "input tensor");
            Check(ov_tensor_get_size(input, out nuint count), "input size");
            Check(ov_tensor_get_byte_size(input, out nuint bytes), "input bytes");
            if (bytes != count * sizeof(float)) throw new InvalidOperationException($"model input is not f32 ({bytes} bytes for {count} values)");
            Check(ov_tensor_data(input, out IntPtr ptr), "input data");
            data = new Span<float>((void*)ptr, checked((int)count));
            fill(data);
            Check(ov_infer_request_infer(_request), "infer");

            Check(ov_infer_request_get_output_tensor_by_index(_request, 0, out output), "output tensor");
            Check(ov_tensor_get_size(output, out nuint outCount), "output size");
            Check(ov_tensor_get_byte_size(output, out nuint outBytes), "output bytes");
            Check(ov_tensor_data(output, out IntPtr outPtr), "output data");
            int n = checked((int)outCount);
            if (outBytes == outCount * sizeof(float)) return read(new ReadOnlySpan<float>((void*)outPtr, n));
            if (outBytes != outCount * 2) throw new InvalidOperationException($"model output is not f16 or f32 ({outBytes} bytes for {outCount} values)");
            wide = new float[n];
            var half = new ReadOnlySpan<Half>((void*)outPtr, n);
            for (int i = 0; i < n; i++) wide[i] = (float)half[i];
            return read(wide);
        }
        finally
        {
            data.Clear();   // the frame: gone before the answer leaves
            if (wide != null) Array.Clear(wide);   // a depth map is a picture of the room too
            if (output != IntPtr.Zero) ov_tensor_free(output);
            if (input != IntPtr.Zero) ov_tensor_free(input);
        }
    }

    public delegate void SpanAction(Span<float> input);
    public delegate T OutputFunc<out T>(ReadOnlySpan<float> output);

    public void Dispose()
    {
        if (_request != IntPtr.Zero) ov_infer_request_free(_request);
        if (_compiled != IntPtr.Zero) ov_compiled_model_free(_compiled);
        if (_core != IntPtr.Zero) ov_core_free(_core);
        _request = _compiled = _core = IntPtr.Zero;
    }

    private static void Check(int status, string what)
    {
        if (status == 0) return;
        string? msg = null;
        try
        {
            msg = Marshal.PtrToStringUTF8(ov_get_last_err_msg());
        }
        catch (EntryPointNotFoundException)
        {
        }
        msg = msg?.Trim();
        if (msg is { Length: > 300 }) msg = msg[..300];
        throw new InvalidOperationException($"{what}: OpenVINO status {status}{(string.IsNullOrEmpty(msg) ? "" : $" ({msg})")}");
    }
}
