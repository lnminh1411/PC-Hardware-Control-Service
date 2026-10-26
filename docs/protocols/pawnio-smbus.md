# SMBus through PawnIO

The PC already runs the PawnIO kernel driver (service `PawnIO`). User-mode access goes through `C:\Program Files\PawnIO\PawnIOLib.dll` plus the PawnIO module `SmbusI801.bin`.

## Module source

LibreHardwareMonitorLib 0.9.6 embeds the module as manifest resource
`LibreHardwareMonitor.Resources.PawnIo.SmbusI801.bin` in `LibreHardwareMonitorLib.dll`.
Load it with `typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetManifestResourceStream(name)`.

## PawnIOLib exports (stdcall, return HRESULT, 0 = success)

```c
HRESULT pawnio_open(HANDLE* handle);
HRESULT pawnio_load(HANDLE handle, const BYTE* blob, SIZE_T size);
HRESULT pawnio_execute(HANDLE handle, const char* name,
                       const ULONG64* in, SIZE_T in_count,
                       ULONG64* out, SIZE_T out_count, SIZE_T* return_count);
HRESULT pawnio_close(HANDLE handle);
```

Sizes are counts of ULONG64, not bytes.

## Module functions

`ioctl_identity`: in 1 (zero), out 3. `out[2]` low 16 bits = PCI vendor, next 16 = PCI device. Log it.

`ioctl_smbus_xfer`: in 9, out 5.

| in | Value |
|---|---|
| 0 | 7-bit address |
| 1 | direction: 0 = write, 1 = read |
| 2 | command byte |
| 3 | size: 0 quick, 1 byte, 2 byte data, 3 word data, 5 block data |
| 4..8 | 40-byte data area: byte at [0]; word little-endian at [0..1]; block: [0] = length, [1..32] = bytes |

`out[0..4]` = the same 40-byte data area after the transfer (read results). HRESULT non-zero = transfer failed (no device / NAK).

## Locking

Hold the global mutex `Global\Access_SMBUS.HTP.Method` around every transfer (create-or-open, wait up to 1 s, release after). Other tools on this PC (LibreHardwareMonitor in ESP32PcStats, HWiNFO) use the same name.
