using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PcHwCtl.Core.Devices;
using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Service.Devices;

/// <summary>
/// Implements <see cref="ISmbus"/> using the PawnIO user-mode library and kernel driver.
/// </summary>
public sealed class PawnIoSmbus : ISmbus
{
    private const string MutexName = @"Global\Access_SMBUS.HTP.Method";
    private const string ModuleResourceName = "LibreHardwareMonitor.Resources.PawnIo.SmbusI801.bin";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoOpenDelegate(out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoLoadDelegate(nint handle, [In] byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoExecuteDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        [In] ulong[] inArgs,
        nuint inCount,
        [Out] ulong[] outArgs,
        nuint outCount,
        out nuint returnCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoCloseDelegate(nint handle);

    private readonly nint _dllHandle;
    private readonly nint _pawnHandle;
    private readonly PawnIoExecuteDelegate _pawnIoExecute;
    private readonly PawnIoCloseDelegate _pawnIoClose;
    private readonly Mutex _mutex;
    private readonly Action<string> _log;
    private bool _disposed;

    private PawnIoSmbus(
        nint dllHandle,
        nint pawnHandle,
        PawnIoExecuteDelegate pawnIoExecute,
        PawnIoCloseDelegate pawnIoClose,
        Mutex mutex,
        Action<string> log)
    {
        _dllHandle = dllHandle;
        _pawnHandle = pawnHandle;
        _pawnIoExecute = pawnIoExecute;
        _pawnIoClose = pawnIoClose;
        _mutex = mutex;
        _log = log;
    }

    /// <summary>
    /// Loads PawnIOLib.dll, binds exports, opens a PawnIO handle, loads the SmbusI801 module,
    /// logs the PCI vendor/device identity, and returns an initialized <see cref="PawnIoSmbus"/> instance.
    /// Returns <c>null</c> on any failure.
    /// </summary>
    public static PawnIoSmbus? Open(EneConfig cfg, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        log ??= (_ => { });

        nint dllHandle = 0;
        nint pawnHandle = 0;
        PawnIoCloseDelegate? pawnIoClose = null;

        try
        {
            if (string.IsNullOrWhiteSpace(cfg.PawnIoLibPath) || !File.Exists(cfg.PawnIoLibPath))
            {
                log($"PawnIO library not found at: '{cfg.PawnIoLibPath}'");
                return null;
            }

            dllHandle = NativeLibrary.Load(cfg.PawnIoLibPath);

            nint pOpen = NativeLibrary.GetExport(dllHandle, "pawnio_open");
            nint pLoad = NativeLibrary.GetExport(dllHandle, "pawnio_load");
            nint pExecute = NativeLibrary.GetExport(dllHandle, "pawnio_execute");
            nint pClose = NativeLibrary.GetExport(dllHandle, "pawnio_close");

            var pawnIoOpen = Marshal.GetDelegateForFunctionPointer<PawnIoOpenDelegate>(pOpen);
            var pawnIoLoad = Marshal.GetDelegateForFunctionPointer<PawnIoLoadDelegate>(pLoad);
            var pawnIoExecute = Marshal.GetDelegateForFunctionPointer<PawnIoExecuteDelegate>(pExecute);
            pawnIoClose = Marshal.GetDelegateForFunctionPointer<PawnIoCloseDelegate>(pClose);

            int hr = pawnIoOpen(out pawnHandle);
            if (hr != 0)
            {
                log($"PawnIO pawnio_open failed: 0x{hr:X8}");
                NativeLibrary.Free(dllHandle);
                return null;
            }

            using Stream? stream = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetManifestResourceStream(ModuleResourceName);
            if (stream == null)
            {
                log($"PawnIO embedded resource '{ModuleResourceName}' not found.");
                pawnIoClose(pawnHandle);
                NativeLibrary.Free(dllHandle);
                return null;
            }

            byte[] moduleBlob = new byte[stream.Length];
            stream.ReadExactly(moduleBlob);

            hr = pawnIoLoad(pawnHandle, moduleBlob, (nuint)moduleBlob.Length);
            if (hr != 0)
            {
                log($"PawnIO pawnio_load failed: 0x{hr:X8}");
                pawnIoClose(pawnHandle);
                NativeLibrary.Free(dllHandle);
                return null;
            }

            ulong[] inArgs = new ulong[1];
            ulong[] outArgs = new ulong[3];
            hr = pawnIoExecute(pawnHandle, "ioctl_identity", inArgs, 1, outArgs, 3, out nuint _);
            if (hr != 0)
            {
                log($"PawnIO ioctl_identity failed: 0x{hr:X8}");
                pawnIoClose(pawnHandle);
                NativeLibrary.Free(dllHandle);
                return null;
            }

            uint vendor = (uint)(outArgs[2] & 0xFFFF);
            uint device = (uint)((outArgs[2] >> 16) & 0xFFFF);
            log($"PawnIO SMBus PCI identity: vendor=0x{vendor:X4}, device=0x{device:X4}");

            var mutex = new Mutex(false, MutexName);
            return new PawnIoSmbus(dllHandle, pawnHandle, pawnIoExecute, pawnIoClose, mutex, log);
        }
        catch (Exception ex)
        {
            log($"PawnIO initialization failed: {ex.Message}");
            if (pawnHandle != 0 && pawnIoClose != null)
            {
                try { pawnIoClose(pawnHandle); } catch { }
            }
            if (dllHandle != 0)
            {
                try { NativeLibrary.Free(dllHandle); } catch { }
            }
            return null;
        }
    }

    /// <inheritdoc />
    public bool Quick(byte address)
    {
        return ExecuteTransfer(address, direction: 0, command: 0, size: 0, writeData: default, readData: default);
    }

    /// <inheritdoc />
    public int ReadByteData(byte address, byte command)
    {
        Span<byte> readData = stackalloc byte[1];
        bool success = ExecuteTransfer(address, direction: 1, command: command, size: 2, writeData: default, readData: readData);
        return success ? readData[0] : -1;
    }

    /// <inheritdoc />
    public bool WriteByteData(byte address, byte command, byte value)
    {
        ReadOnlySpan<byte> writeData = stackalloc byte[1] { value };
        return ExecuteTransfer(address, direction: 0, command: command, size: 2, writeData: writeData, readData: default);
    }

    /// <inheritdoc />
    public bool WriteWordData(byte address, byte command, ushort value)
    {
        Span<byte> writeData = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(writeData, value);
        return ExecuteTransfer(address, direction: 0, command: command, size: 3, writeData: writeData, readData: default);
    }

    /// <inheritdoc />
    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data.Length > 32)
        {
            return false;
        }

        Span<byte> writeData = stackalloc byte[data.Length + 1];
        writeData[0] = (byte)data.Length;
        data.CopyTo(writeData.Slice(1));

        return ExecuteTransfer(address, direction: 0, command: command, size: 5, writeData: writeData, readData: default);
    }

    private bool ExecuteTransfer(
        byte address,
        byte direction,
        byte command,
        byte size,
        ReadOnlySpan<byte> writeData,
        Span<byte> readData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool acquired = false;
        try
        {
            try
            {
                acquired = _mutex.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                _log("PawnIO SMBus: timed out waiting for SMBus mutex (1s).");
                return false;
            }

            ulong[] inArgs = new ulong[9];
            inArgs[0] = address;
            inArgs[1] = direction;
            inArgs[2] = command;
            inArgs[3] = size;

            if (!writeData.IsEmpty)
            {
                Span<byte> inData = MemoryMarshal.AsBytes(inArgs.AsSpan(4, 5));
                writeData.CopyTo(inData);
            }

            ulong[] outArgs = new ulong[5];
            int hr = _pawnIoExecute(_pawnHandle, "ioctl_smbus_xfer", inArgs, 9, outArgs, 5, out nuint _);
            if (hr != 0)
            {
                return false;
            }

            if (!readData.IsEmpty)
            {
                ReadOnlySpan<byte> outData = MemoryMarshal.AsBytes(outArgs.AsSpan(0, 5));
                outData.Slice(0, readData.Length).CopyTo(readData);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"PawnIO SMBus transfer error: {ex.Message}");
            return false;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    _log($"PawnIO SMBus release mutex error: {ex.Message}");
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _mutex.Dispose();
        }
        catch { }

        try
        {
            _pawnIoClose(_pawnHandle);
        }
        catch { }

        try
        {
            NativeLibrary.Free(_dllHandle);
        }
        catch { }
    }
}
