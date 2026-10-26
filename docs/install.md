# Installation and maintenance

Guide for installing, configuring, and maintaining the PcHwCtl Windows service and associated tooling.

## Prerequisites

- **.NET 10 runtime**: Windows x64 runtime (.NET 10.0 Windows Desktop / Server runtime).
- **PawnIO driver**: Kernel driver installed on the system (typically already installed by FanControl for direct hardware/SMBus access).
- **Administrator shell**: PowerShell or Command Prompt with elevated privileges (`Run as Administrator`).

## Building and publishing

The service can be built and published as a framework-dependent win-x64 binary using `scripts/publish.ps1`:

```powershell
.\scripts\publish.ps1
```

This publishes `src/PcHwCtl.Service/PcHwCtl.Service.csproj` in Release configuration (`-c Release -r win-x64 --self-contained false`) directly into `publish/` at the repository root.

## Installing the service

Run `scripts/install.ps1` from an elevated PowerShell terminal:

```powershell
.\scripts\install.ps1
```

### Available installation switches

| Switch | Description |
|---|---|
| `-SkipPublish` | Skip compiling and publishing binaries; use existing files in `publish\`. |
| `-ForceConfig` | Overwrite existing `C:\ProgramData\PcHwCtl\pchwctl.json` with repository template, backing up existing file to `pchwctl.json.bak-<yyyyMMdd-HHmmss>`. |
| `-DisableFanControl` | Stop running `FanControl.exe` processes and disable autostart mechanisms (scheduled tasks, registry Run entries, and startup folder shortcuts). Disabled registry entries and shortcuts are renamed with suffix `.disabled-by-pchwctl`. |
| `-DisableOpenRGB` | Stop running `OpenRGB.exe` processes, disable scheduled tasks, rename registry Run entries and startup shortcuts with suffix `.disabled-by-pchwctl`, and stop any Windows service targeting `OpenRGB.exe` while setting its startup type to Disabled. |

### Installation actions

1. Unless `-SkipPublish` is specified, runs `publish.ps1`.
2. Stops the `PcHwCtl` service if already running (waiting up to 15 seconds).
3. Copies published binaries to `C:\Program Files\PcHwCtl\`.
4. Copies configuration template to `C:\ProgramData\PcHwCtl\pchwctl.json` if missing or if `-ForceConfig` is passed.
5. Creates or reconfigures the `PcHwCtl` service with auto-start and failure recovery (restart after 5s, 5s, 30s).
6. Handles `-DisableFanControl` and `-DisableOpenRGB` disabling logic if requested.
7. Starts the service, verifies Running status within 15 seconds, and displays service status alongside the tail of `log.txt`.

## Uninstalling the service

Run `scripts/uninstall.ps1` from an elevated PowerShell terminal:

```powershell
.\scripts\uninstall.ps1
```

### Available uninstallation switches

| Switch | Description |
|---|---|
| `-RestoreFanControl` | Restore FanControl scheduled tasks (re-enabled), startup shortcuts, and registry Run keys previously renamed with `.disabled-by-pchwctl`. |
| `-RestoreOpenRGB` | Restore OpenRGB scheduled tasks, startup shortcuts, registry Run keys, and restore OpenRGB Windows services to Automatic startup and started status. |
| `-RemoveData` | Delete `C:\ProgramData\PcHwCtl\` (including active configuration and log files). Without this switch, configuration and logs are preserved. |

### Uninstallation actions

1. Stops the `PcHwCtl` service (prompting graceful shutdown which relinquishes fans back to BIOS control).
2. Deletes the `PcHwCtl` Windows service.
3. Removes `C:\Program Files\PcHwCtl\`.
4. Retains `C:\ProgramData\PcHwCtl\` unless `-RemoveData` was specified.
5. Re-enables disabled items if `-RestoreFanControl` or `-RestoreOpenRGB` was specified.

## Configuration and logs

- **Configuration path**: `C:\ProgramData\PcHwCtl\pchwctl.json`
- **Log path**: `C:\ProgramData\PcHwCtl\log.txt`

The service monitors `pchwctl.json` and hot-reloads valid changes automatically. If a newly edited configuration is invalid, the service logs the error and continues running using the last good configuration.

## Testing and diagnostics

### Console dry run

To inspect sensor detection and verify fan curve calculations without issuing write commands to hardware controllers:

```powershell
"C:\Program Files\PcHwCtl\pchwctl.exe" --console --dry-run
```

You can also list all detected sensors and their current values:

```powershell
"C:\Program Files\PcHwCtl\pchwctl.exe" --list
```

### Querying the running service over IPC

When `PcHwCtl` is running as a Windows service, connect to its named pipe (`\\.\pipe\PcHwCtl`) or issue CLI IPC commands:

```powershell
"C:\Program Files\PcHwCtl\pchwctl.exe" ipc "get state"
```

Common IPC commands include:
- `ping`: Check service IPC responsiveness (`{"pong":true}`).
- `get state`: Return full JSON state of fan speeds, temperatures, curves, and RGB device states.
- `fan set <fan> <pct> [ttl]`: Apply manual speed percentage override with optional time-to-live in seconds.
- `fan release <fan>`: Release manual fan override back to configured curve.
- `rgb set <group> <#RRGGBB> [ttl]`: Apply static RGB override color.
