# PC Hardware Control Service

> **This is for my PC only.** Sensor IDs, fan curves, LED layouts and RGB protocols are hard-wired to one machine (Gigabyte B860M GAMING X WIFI6E, IT8696E SuperIO, IT5711 RGB Fusion 2, ENE DRAM, Intel Core Ultra 7 265K, AMD Radeon RX 6600, NVIDIA GeForce RTX 3050, DELL E1715S, MSI PRO MP275). Running it on other hardware can write to the wrong registers. Use it as reference, not as a drop-in tool.

Windows service (LocalSystem) replacing FanControl and OpenRGB on my PC. Exposes a JSON-line named pipe `PcHwCtl` used by the ESP32 board helper. Companion project: [ESP32-PC-Control-System](https://github.com/lnminh1411/ESP32-PC-Control-System).

## Features

- **Fans**: `graph` (hysteresis and response delays), `auto` (deadband) and `flat` curves; virtual `mix` sensors (`max`, `min`, `avg`); step limits (`stepUp`, `stepDown`); a stale sensor drives its fan to 100 %, service stop or crash hands fans back to BIOS control; AMD OD8 GPU fan curves written directly to firmware via `atiadlxx.dll`; GPU Zero RPM parking (`zero` and `release` modes).
- **RGB**: Gigabyte IT5711 USB HID (3 ARGB headers, single-color zones) and ENE DRAM SMBus (2x DDR5); `off`, `static`, `breathing`, `rainbow`, and sensor-driven `gradient` modes with smoothing; dedicated background device flush threads with FPS caps; global master brightness.
- **Monitors**: DDC/CI brightness (VCP 0x10), power (VCP 0xD6 `ddc-power` or VCP 0x60 input switch `ddc-input`), and active input selection (VCP 0x60); Windows display sleep (`SC_MONITORPOWER`) and mouse-nudge wake; user-session `display-agent` helper process to bypass Session 0 isolation.
- **Process Lasso**: Direct `prolasso.ini` parser and editor; live CPU affinity pinning (`aff`); profile saving and loading (`lasso`); Windows efficiency mode (EcoQoS) enforcement and background sweep (`eco`).
- **Activity and power**: Per-process CPU usage tracking (`proc top`) via `NtQuerySystemInformation`; Windows wake locks inspection via `powercfg /requests`; Windows power plan switching including "PcHwCtl Night" (30 % max / 5 % min processor state, Balanced's core parking, display off after 5 min); SYSTEM power transitions (`sleep`, `hibernate`, `restart`, `shutdown`, `lock`); Event Log shutdown, wake, and update reboot tracking.
- **Sensor feed**: Central LibreHardwareMonitor reader with split-loop polling (500 ms fast, 1000 ms background CPU/memory); JSON queries and snapshot text streaming over named pipe (`get sensors`, `subscribe sensors`).
- **State file and persisted settings**: Atomic state export to `C:\ProgramData\PcHwCtl\state.txt` for ESP32PcStats; persisted Zero RPM toggles and RGB brightness in `C:\ProgramData\PcHwCtl\settings.json`.
- **FanControl importer**: CLI converter from legacy FanControl JSON configs into `pchwctl.json`.

## Hardware

Built for this specific machine:
- **Motherboard**: Gigabyte B860M GAMING X WIFI6E
- **SuperIO**: ITE IT8696E (LPC bus; fan controls, tachometers, temperatures)
- **RGB controller**: Gigabyte IT5711 RGB Fusion 2 (USB HID `048D:5711`, 3 ARGB headers, 12V strip, accent)
- **RAM**: 2x ENE DRAM DDR5 sticks (`AUDA0-E6K5-0101`, 8 LEDs each, Intel i801 SMBus remapped to `0x71` and `0x73`)
- **CPU**: Intel Core Ultra 7 265K (8 P-cores, 12 E-cores)
- **GPUs**:
  - Primary: AMD Radeon RX 6600 (Navi 23, DevID `73FF`, ADL Overdrive8 native curve)
  - Secondary: NVIDIA GeForce RTX 3050 (LHM fan control, Zero RPM `release` parking)
- **Monitors** (ordered left to right):
  1. DELL E1715S (DisplayPort, `ddc-power`)
  2. MSI PRO MP275 (HDMI 1, `ddc-input`)
  3. DELL E1715S (DisplayPort, `ddc-power`)

## Requirements

- Windows x64
- .NET 10 runtime (`net10.0-windows`)
- PawnIO driver and `C:\Program Files\PawnIO\PawnIOLib.dll`
- LibreHardwareMonitorLib 0.9.6 (bundled via NuGet)
- Administrator rights to install and to run `pchwctl` (PawnIO); `import-fancontrol` needs none. The service runs as LocalSystem

## Build and install

Publish:
```powershell
.\scripts\publish.ps1
```

Install (elevated):
```powershell
.\scripts\install.ps1 [-SkipPublish] [-ForceConfig] [-DisableFanControl] [-DisableOpenRGB]
```

Uninstall (elevated):
```powershell
.\scripts\uninstall.ps1 [-RestoreFanControl] [-RestoreOpenRGB] [-RemoveData]
```

See [docs/install.md](docs/install.md) for details.

## CLI

- `pchwctl ipc <command>`: Send a command line to the running service named pipe.
- `pchwctl display-agent [--config <path>] [--parent <pid>]`: Run user-session helper agent for DDC/CI monitor control.
- `pchwctl import-fancontrol <in.json> <out.json>`: Convert a FanControl JSON configuration into PcHwCtl format.
- `pchwctl --list`: Enumerate all detected hardware sensors and values to stdout.
- `pchwctl --dry-run`: Evaluate fan curves and RGB frames without writing to hardware registers.
- `pchwctl --console`: Run service loop interactively in the current console instead of as a service.
- `pchwctl --config <path>`: Specify an alternate configuration JSON path.

## Configuration

Installed and read from `C:\ProgramData\PcHwCtl\pchwctl.json` (template at `config/pchwctl.json`). Monitored and hot-reloaded automatically. Top-level sections:
- `tickMs`: Evaluation loop interval in milliseconds.
- `staleSensorS`: Maximum sensor reading age before triggering 100% failsafe.
- `sensors`: Virtual composite sensor definitions (`mix` aggregation via `max`, `min`, `avg`).
- `curves`: Fan curve algorithm definitions (`graph`, `auto`, `flat`).
- `fans`: Controlled fan bindings, step limits, floor, and Zero RPM settings.
- `rgb`: RGB lighting engine configuration, frame rates, and lighting groups.
- `devices`: Hardware controller connection parameters for `it5711` and `ene`.
- `ipc`: Named pipe identifier, override TTL bounds, and ACL permissions.
- `displays`: DDC/CI monitor polling interval and per-model power methods.
- `feed`: Sensor feed poll interval, per-hardware intervals (`every`), and power request polling.
- `power`: Active power scheme mappings for `nightPlan` and `dayPlan`.
- `lasso`: Process Lasso configuration path, profile directory, and eco sweep interval.

## IPC

Pipe name: `\\.\pipe\PcHwCtl`. Framing: UTF-8 text, one request per line (`\n`), one JSON response per line (`{"ok":true,"data":...}` or `{"ok":false,"error":"..."}`).

Command groups by verb:
- `ping`: Check service responsiveness.
- `get`: Return full state (`get state`) or latest sensor snapshot (`get sensors`).
- `subscribe`: Stream full JSON state every tick (`subscribe`) or text sensor snapshot blocks (`subscribe sensors`).
- `fan`: Set manual speed override (`fan set`), switch curve (`fan curve`), clear overrides (`fan release`), or toggle Zero RPM (`fan zerorpm`).
- `rgb`: Set color override (`rgb set`), set rule override (`rgb mode`), set global brightness (`rgb brightness`), or clear override (`rgb release`).
- `release`: Drop fan and RGB overrides for a given target (`release <target|all>`).
- `display`: List monitors (`display list`), set brightness (`display bright`), toggle power (`display power`), switch input (`display input`), sleep displays (`display sleep`), or wake displays (`display wake`).
- `aff`: Manage CPU affinity rules in `prolasso.ini` (`aff set`, `aff clear`, `aff list`).
- `lasso`: Manage Process Lasso configuration profiles (`lasso save`, `lasso load`, `lasso profiles`).
- `eco`: Manage Windows efficiency mode / EcoQoS (`eco on`, `eco off`, `eco list`).
- `proc`: Query top CPU consuming processes (`proc top [n]`).
- `power`: List plans (`power plan`), switch plan (`power plan night|day|<name|guid>`), query capabilities (`power caps`), initiate power transition (`power do`), or inspect power event history (`power status`).

State file is written atomically every tick to `C:\ProgramData\PcHwCtl\state.txt` for ESP32PcStats. Dynamic settings (`fan zerorpm`, `rgb brightness`) persist in `C:\ProgramData\PcHwCtl\settings.json`.

See [docs/ipc.md](docs/ipc.md) for the authoritative reference.

## Docs

- [docs/install.md](docs/install.md): Installation, maintenance, service configuration, and diagnostics.
- [docs/ipc.md](docs/ipc.md): Authoritative IPC protocol framing, command syntax, state schemas, and sensor keys.
- [docs/protocols/amd-od8.md](docs/protocols/amd-od8.md): Native AMD Overdrive8 fan curve table writes via `atiadlxx.dll`.
- [docs/protocols/ddc-ci.md](docs/protocols/ddc-ci.md): DDC/CI monitor brightness, power, input switching, and user-session agent.
- [docs/protocols/ene-dram.md](docs/protocols/ene-dram.md): ENE DRAM SMBus protocol, DIMM slot remapping, and register mapping.
- [docs/protocols/it5711.md](docs/protocols/it5711.md): Gigabyte IT5711 USB HID feature reports and ARGB header layouts.
- [docs/protocols/pawnio-smbus.md](docs/protocols/pawnio-smbus.md): SMBus transactions via PawnIO kernel driver and `PawnIOLib.dll`.
- [docs/protocols/process-lasso.md](docs/protocols/process-lasso.md): Process Lasso `prolasso.ini` management and Arrow Lake core topology.

## License

[AGPL-3.0](LICENSE).
