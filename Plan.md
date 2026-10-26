# PC Hardware Controller: plan

Goal: one Windows service (`PcHwCtl`) replaces FanControl + OpenRGB on this PC. Same behavior as today, plus IPC so ESP32 board can read and set fans/RGB.

## 1. Current setup (read 2026-10-26)

### Hardware
| Part | Bus | Used for |
|---|---|---|
| ITE IT8696E SuperIO (B860M GAMING X WIFI6E) | LPC, via LHM | 6 PWM outputs, fan tach, temps |
| Gigabyte IT5711 RGB Fusion 2 | USB HID `VID_048D&PID_5711&MI_01&Col02`, fw 0.0.28.3 | 12V RGB (LED5), ARGB_V2_1..3 (24 LED each, GRB), Motherboard zone |
| 2x ENE DRAM (AUDA0-E6K5-0101) | SMBus i801, addr `0x71`, `0x73` | 8 LED each |
| AMD RX 6600 + NVIDIA GPU | ADLX / NvAPI | temps only (GPU fans not controlled) |

### FanControl (`F:\Tweaks\Fan control.json`)
| Control | LHM id | Curve | Notes |
|---|---|---|---|
| CPU Fans | `/lpc/it8696e/control/0` | Auto | max 1923 rpm |
| Rear Fan | `control/1` | Graph | max 1762 rpm |
| Front Fans | `control/2` | Graph | start 32 %, max 1957 rpm |
| Water Pump | `control/4` | Flat 100 % | max 2961 rpm |
| control/3, control/5, GPU fans | | disabled | |

All controls: step 8 %/tick up and down, offset 0, min 0.

Curves:
- **Graph**: temp source `Mix` = mix function 1 over [RX 6600 GPU temp, it8696e temp/4, temp/5, nvidia temp/0, intelcpu temp/1]. Points (52.5, 20) (60.2, 38.7) (65, 80) (75, 90) (80, 100). Hysteresis 2 °C up/down, response 2 s up/down, ignore hysteresis at limits.
- **Auto**: source `/intelcpu/0/temperature/22`, idle 45 °C at 50 %, load 75 °C at 100 %, step 5, deadband 5, response 1 s.
- **Flat**: 100 %.

Confirmed by owner: FanSensor nicknames are correct. `SelectedMixFunction 1` = Max. Control-to-curve assignment ports unchanged, keyed by control id. `ADLX/...` id is a FanControl plugin; LHM name for same sensor is `/gpu-amd/0/temperature/...`, map at import.

### OpenRGB (`%APPDATA%\OpenRGB`)
- Hardware Sync plugin (`plugins\settings\MeasureSettings.json`), both auto-start, 10 fps, 100 ms refresh, source `/lpc/it8696e/0/fan/2` (Front Fans rpm), color gradient between stops:

| Group | Devices | <=1400 rpm | 1500-1650 | >=1750 |
|---|---|---|---|---|
| Main | mobo zones 0-4 | (255,203,128) peach | (230,168,23) amber | (220,55,55) red |
| RAM | ENE 0x71, 0x73 | (255,255,255) white | (230,210,46) yellow | (220,69,110) pink |

  (OpenRGB stores 0x00BBGGRR; decoded above.)
- SDK server autostart on `0.0.0.0:6742`. Profile `Default.orp`: Direct mode, per-LED.
- Hardware Fan Sync plugin: only bridged Mix temp to LEDs through front fan rpm, because OpenRGB cannot read a mixed sensor. Not needed now: gradients read Mix temp directly. Stops translated rpm -> front fan % (calibration) -> Mix temp (Graph curve):

| rpm stop | front % | Mix temp |
|---|---|---|
| 1400 | 57.5 | 62.4 °C |
| 1500 | 64.3 | 63.2 °C |
| 1650 | 75.6 | 64.5 °C |
| 1750 | 83.9 | 68.9 °C |

  Colors packed in 62-69 °C because the Graph curve is steep there. The fan ramp (8 %/tick, 2 s response) used to smooth color changes, so the gradient source gets the same smoothing (EMA, configurable).
- Scheduler plugin disabled.

### ESP32 link (already exists)
`E:\ESP32 PC Controller\host\pc_stats` = service `ESP32PcStats` (C#, .NET Fx 4, `csc.exe`). Reads sensors with LHM (`lhwm-wrapper.dll`), streams to board over vendor HID (64 B reports, CDC fallback). Board already sends commands back (`!power <action> <nonce>`, ack via `power.ack`). Fan/RGB commands go through same path.

## 2. Architecture

```
ESP32 board --USB HID--> ESP32PcStats --named pipe--> PcHwCtl service
                                                        |-- FanEngine  (LHM: IT8696E PWM + temps)
                                                        |-- RgbEngine  (IT5711 HID, ENE SMBus)
                                                        |-- Rules      (fan rpm -> color, temp -> curve)
                                                        |-- Config     (JSON, hot reload)
                                                        `-- IPC server (pipe; optional LAN TCP)
```

Decisions:
- **Separate service, pipe link to ESP32PcStats.** ESP32PcStats keeps owning HID. It forwards `!fan` / `!rgb` lines to the pipe and streams fan state back as `@s` keys. Keeps one HID owner, no firmware transport change.
- **LHM directly** (NuGet `LibreHardwareMonitorLib`, PawnIO driver version). Same sensor ids as FanControl, so config imports 1:1. LHM takes global ISA/SMBus mutexes, so it coexists with ESP32PcStats reads.
- **RGB native, no OpenRGB at all.** OpenRGB source is `GPL-2.0-only` (checked at running commit `0fca93e4`), which cannot be combined with this AGPL-3.0 repo. So no code is copied or translated. Protocol facts (register addresses, packet layouts) go into `docs/protocols/*.md` in our own words; code is written from those docs only. USB/SMBus captures from the running OpenRGB are a second, independent source.
- **SMBus path**: the PC already runs the PawnIO kernel driver; OpenRGB 1.0rc2 drives i801 SMBus through PawnIO module `SmbusI801.bin`. Same driver + module, loaded via `PawnIOLib.dll` (PawnIO project, not OpenRGB).
- **Language**: C# .NET 10 (latest GA; move to .NET 11 at its GA, Nov 2026), `Microsoft.Extensions.Hosting.WindowsServices`, LHM `LibreHardwareMonitorLib` 0.9.6 (PawnIO). Runs as LocalSystem (driver + HID access).

## 3. Phases

### Phase 0: groundwork
- Snapshot today's behavior: FanControl + OpenRGB running, log 10 min of temps/PWM/rpm (LHM console tool) as baseline.
- Confirm Mix function, `/intelcpu/0/temperature/22` sensor name, AMD GPU LHM id.
- Project skeleton: `src/PcHwCtl` (service), `src/PcHwCtl.Cli` (console + debug), `config/pchwctl.json`.

### Phase 1: fans (replaces FanControl)
- Sensor layer: LHM `Computer` (CPU, GPU, Motherboard on; storage off, like FanControl), poll 1 s.
- Curve types: `graph` (linear interp + hysteresis + response time), `auto` (FanControl Auto algorithm), `flat`, `mix` sensor (max/min/avg), `manual`.
- Output: step limit 8 %/tick, start %, min %, per-control enable.
- Importer: `pchwctl import-fancontrol "F:\Tweaks\Fan control.json"` writes native config.
- **Fail-safe** (must have before first run):
  - service stop / crash / unhandled exception: `control.SetDefault()` (BIOS auto) on all controls.
  - sensor missing or stale > 5 s: that curve goes 100 %.
  - pump control locked to >= its configured floor; IPC cannot set below.
  - watchdog thread: main loop stalls > 10 s, set defaults.
- Test: run side by side in read-only mode vs FanControl, compare computed % per tick. Then stop FanControl, switch to write.

### Phase 2: RGB, motherboard (replaces OpenRGB part 1)
- IT5711 HID driver: init, LED count per header (24 on D_LED1..3), direct per-LED mode, 12V strip, mobo zones. Byte order from calibration (D_LED GRB, Mainboard BGR).
- Rule engine: `gradient(source, stops)` same as Hardware Sync, 10 fps, plus static color, off, effects (breathing, rainbow) as software frames.
- Test vs OpenRGB screenshots for each zone, then remove OpenRGB autostart.

### Phase 3: RGB, DRAM
- ENE over SMBus i801 through PawnIO SMBus module (same driver LHM uses).
- Risk: wrong SMBus write can hit SPD hub. Hard allow-list: writes only to `0x71`, `0x73`, only after ENE id check (`AUDA0-E6K5-0101`). No address scan.
- No OpenRGB fallback (owner decision). If blocked, DRAM stays unlit until fixed.

### Phase 4: IPC
Named pipe `\\.\pipe\PcHwCtl`, JSON lines, ACL: SYSTEM + Administrators + ESP32PcStats account.

| Command | Effect |
|---|---|
| `get state` | temps, rpm, % per control, active profile, RGB state |
| `fan set <control> <pct> [ttl s]` | manual override, auto-expires (default 60 s) |
| `fan curve <control> <curve>` | switch curve |
| `fan profile <name>` | quiet / normal / max (named configs) |
| `rgb set <zone\|all> <hex> [ttl]` | static color override |
| `rgb mode <zone\|all> <rule>` | gradient / static / off / effect |
| `rgb brightness <0-100>` | global dim |
| `release <all\|target>` | drop overrides |
| `subscribe` | push state on change |

Board side (in ESP32 repo, separate task): ESP32PcStats forwards `!fan ...` / `!rgb ...` with nonce + ack like `!power`. Streams `fan.<name>.rpm`, `fan.<name>.pct`, `rgb.profile` as `@s`/`@t` keys. Firmware adds a face/buttons for profile switch.

Optional later: LAN TCP/WebSocket with token so board talks over Wi-Fi when USB stack is down.

### Phase 5: service + tooling
- `install.ps1` / `uninstall.ps1` (same style as pc_stats): service `PcHwCtl`, auto start, restart on failure x3.
- Log `C:\ProgramData\PcHwCtl\log.txt`, rotate 4 MB.
- Config hot reload with validation; bad config keeps last good one.
- CLI: `--console`, `--list` (sensors/controls/LED zones), `--dry-run` (compute, no writes).
- Suspend/resume: re-init IT5711 and re-apply PWM on resume (Gigabyte boards lose RGB state).
- Remove FanControl + OpenRGB from startup after each phase passes.

## 4. Config shape (draft)

```json
{
  "sensors": { "mix": { "fn": "max", "of": ["/gpu-amd/0/temperature/0", "/lpc/it8696e/temperature/4", "..."] } },
  "curves": {
    "graph": { "type": "graph", "source": "mix", "points": [[52.5,20],[60.2,38.7],[65,80],[75,90],[80,100]],
               "hysteresis": 2, "responseS": 2 },
    "auto":  { "type": "auto", "source": "/intelcpu/0/temperature/22", "idle": [45,50], "load": [75,100] },
    "flat":  { "type": "flat", "pct": 100 }
  },
  "fans": {
    "cpu":   { "control": "/lpc/it8696e/control/0", "tach": "/lpc/it8696e/fan/0", "curve": "auto", "step": 8 },
    "rear":  { "control": "/lpc/it8696e/control/1", "tach": "/lpc/it8696e/fan/1", "curve": "graph", "step": 8 },
    "front": { "control": "/lpc/it8696e/control/2", "tach": "/lpc/it8696e/fan/2", "curve": "graph", "start": 32, "step": 8 },
    "pump":  { "control": "/lpc/it8696e/control/4", "tach": "/lpc/it8696e/fan/4", "curve": "flat", "floor": 100 }
  },
  "rgb": {
    "main": { "zones": ["mobo:*"], "rule": { "type": "gradient", "source": "fan:front.rpm",
              "stops": [[1400,"#FFCB80"],[1500,"#E6A817"],[1650,"#E6A817"],[1750,"#DC3737"]] } },
    "ram":  { "zones": ["ene:0x71","ene:0x73"], "rule": { "type": "gradient", "source": "fan:front.rpm",
              "stops": [[1400,"#FFFFFF"],[1500,"#E6D22E"],[1650,"#E6D22E"],[1750,"#DC456E"]] } }
  },
  "ipc": { "pipe": "PcHwCtl", "overrideTtlS": 60 }
}
```

## 5. Risks
| Risk | Mitigation |
|---|---|
| Fans stuck at low % if service dies | SetDefault on exit, watchdog, BIOS fan curve as last line |
| SMBus write to wrong device | allow-list + id check, DRAM last phase |
| Two LHM instances racing (pc_stats + PcHwCtl) | LHM global mutexes; later option: pc_stats reads sensors from PcHwCtl pipe instead |
| IT5711 protocol differs on fw 0.0.28.3 | capture OpenRGB HID traffic (USBPcap) as reference before writing |
| PawnIO driver not installed | installer checks, FanControl already installed it |

## 6. Open questions
1. Separate service + pipe (plan above) or merge into ESP32PcStats?
2. DRAM RGB native (Phase 3 risk) or keep OpenRGB headless for DRAM?
3. Need OpenRGB SDK server on 6742 for anything?
4. Hardware Fan Sync plugin: used for anything?
5. .NET 8 OK, or stay .NET Fx 4 + `csc.exe` like pc_stats?
