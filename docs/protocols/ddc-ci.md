# Monitor control: DDC/CI and Windows display off

Notes for this PC's three monitors, checked live 2026-10-27. Ported from ESP32-Helper's `Monitors.cs`.

## Session

DDC/CI (`dxva2.dll`) and `SC_MONITORPOWER` only work in the logged-on user's session: session 0, where the service runs, enumerates no monitors. The service starts `pchwctl display-agent --config <path> --parent <service pid>` in the active console session with the user's token (`WTSQueryUserToken` + `CreateProcessAsUser`, desktop `winsta0\default`), checks every 10 s, starts it again after a crash or a new logon, and kills it on stop. The agent exits on its own when the parent process is gone. It serves `\\.\pipe\PcHwCtl-display` (SYSTEM, Administrators and the user), one JSON line in, one out, and logs to `%LOCALAPPDATA%\PcHwCtl\display-agent.log`. Run from a console (session > 0), `pchwctl --console` talks to the monitors itself.

## DDC/CI

| Call | Use |
|---|---|
| `EnumDisplayMonitors` + `GetPhysicalMonitorsFromHMONITOR` | physical monitors, numbered from the left (`rcMonitor.left`), as the ESP32 board numbers them |
| `GetVCPFeatureAndVCPFeatureReply` / `SetVCPFeature` | read / write a VCP code |
| `DestroyPhysicalMonitors` | after every scan |

Rules carried over from the helper: one DDC conversation at a time (I2C is slow); one retry after 50 ms on a lost reply; a value just written is reported for 15 s, because monitors answer reads with the old value for a while. Added here: a monitor that stops answering keeps its last answer, since one asleep on "no signal" answers nothing.

| VCP | Meaning |
|---|---|
| 0x10 | brightness, scaled to the monitor's maximum |
| 0x60 | input source (0x01 VGA, 0x0F DisplayPort 1, 0x11 HDMI 1, 0x12 HDMI 2) |
| 0xD6 | power mode: 1 on, 4 off (DPM), 5 off (power button) |

## This PC's monitors

| Monitor | Link | 0xD6 listed | Power off that keeps it in Windows |
|---|---|---|---|
| DELL E1715S x2 | DisplayPort | 01 04 05 | `ddc-power`: 0xD6 = 4 and back to 1. Stays listed. |
| MSI PRO MP275 | HDMI 1 (0x11) | 04 only | `ddc-input`: 0x60 = 0x01 (VGA, nothing connected); it shows "no signal", then sleeps with the backlight off, and keeps its HDMI link up. Back with 0x60 = the input it was on (else `activeInput`). |

On 0xD6 = 4 the MSI firmware drops the HDMI hot-plug signal too and Windows removes the display (user test). Its DDC/CI still answers while VGA is selected, so the switch back works remotely. It reports 0xD6 = 1 throughout, so its power state is read from the input instead.

## Windows display off

`PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)` turns every display off at once; any input wakes them. Wake from the service side is a 1 px mouse move down and back (`mouse_event`).
