# PcHwCtl IPC

Named pipe `\\.\pipe\PcHwCtl` (name from config `ipc.pipeName`). Access: SYSTEM, Administrators, and the account in `ipc.allowAccount` (default `NT AUTHORITY\LocalService`; ESP32PcStats runs as LocalSystem, already covered).

## Framing

UTF-8 text, one request per line (`\n`), one JSON response per line. A connection may send many requests. Max line 4 KB; longer lines get an error and the connection closes.

Response: `{"ok":true,"data":...}` or `{"ok":false,"error":"<message>"}`.

## Commands

Names are case-insensitive. `<fan>` = a fan key from config or `all`. `<group>` = an RGB group key or `all`. `[ttl]` = seconds.

| Command | Effect |
|---|---|
| `ping` | `{"pong":true}` |
| `get state` | full state, see below |
| `fan set <fan> <pct> [ttl]` | override output %. ttl default `ipc.overrideTtlS` (60), capped at `ipc.maxTtlS` (3600); 0 not allowed for fans. floorPct still wins |
| `fan curve <fan> <curve>` | switch the fan to another configured curve until `fan release` or config reload |
| `fan release <fan>` | drop the % override and the curve switch |
| `fan zerorpm <fan\|all> <on\|off\|default>` | Zero RPM toggle for fans with a `zeroRpm` config (the GPUs). Saved: survives restarts and reloads. `default` forgets the saved value so the config's `enabled` applies (off in this config). A `fan set` override keeps the fan spinning while it lasts |
| `rgb set <group> <#RRGGBB> [ttl]` | static color override. ttl 0 or missing = until released |
| `rgb mode <group> <off\|static\|breathing\|rainbow\|gradient> [#RRGGBB] [ttl]` | rule override; color used by static/breathing |
| `rgb brightness <0-100>` | global brightness. Saved: survives restarts and reloads |
| `rgb release <group>` | drop the override |
| `display list` | monitors, numbered from the left: id, name, main, bright %, power (1 on, 0 off, -1 unknown), input, powerMethod |
| `display bright <n\|all> <0-100>` | DDC/CI brightness |
| `display power <n\|all> <on\|off>` | per-monitor power through the model's method (config `displays.models`); `all` skips monitors without one |
| `display input <n\|all> <code>` | input source, e.g. `0x11` |
| `display sleep` / `display wake` | Windows display off for every monitor / wake with a 1 px mouse nudge |
| `aff set <exe> <cpus>` | Process Lasso affinity rule, e.g. `aff set brave.exe 10-15` or `2-5,10-17`: written to prolasso.ini (kept for later starts) and applied to running processes now. Replaces the exe's whole-exe rule; per-instance rules (`svchost.exe (netprofm -p)`) stay |
| `aff clear <exe>` | removes the exe's rule; running ones get every CPU |
| `aff list` | the ini's rules: `[{name, cpus}]` |
| `lasso save <profile>` | copies prolasso.ini to `lasso.profilesDir\<profile>.ini`. Names only (letters, digits, `_ . -`), never paths |
| `lasso load <profile>` | the profile replaces prolasso.ini; running processes follow its rules, names whose rule went away get every CPU |
| `lasso profiles` | saved profile names |
| `eco on <exe>` | Windows efficiency mode (EcoQoS: low clocks, E-cores preferred) on every instance, now and for instances started later (swept every `lasso.ecoSweepS`, 10 s). Memory only: gone on service restart |
| `eco off <exe\|all>` | gives each process back the efficiency setting it had before |
| `eco list` | exes held in efficiency mode |
| `power plan` | Windows power plans: `[{name, guid, active}]` |
| `power plan night` / `power plan day` | switches to `power.nightPlan` ("PcHwCtl Night") / `power.dayPlan` ("High performance") |
| `power plan <name\|guid>` | switches to any plan, by exact name (case-insensitive) or GUID |
| `proc top [n]` | the n (default 5, max 50) busiest exes over the last second: `[{exe, cores, instances}]`, `cores` 1.0 = one core busy, summed over instances. Includes system and kept processes |
| `get sensors` | latest sensor feed snapshot as JSON: `{seq, ts, values: {cpu_temp: 41.8, gpu1_fan: null, ...}}` |
| `subscribe sensors` | after the ack line, the server pushes every sensor feed snapshot as text, see [Sensor feed](#sensor-feed) |
| `subscribe` | after the ack line, the server pushes one `get state` style line per tick until the client disconnects |

## State

```json
{
  "fans": {
    "front-fans": { "pct": 32, "target": 32, "temp": 50.0, "rpm": 1121, "missing": false,
                    "curve": "graph", "override": { "pct": 40, "expiresInS": 52 },
                    "zeroRpm": null, "parked": false },
    "gpu-amd": { "pct": 0, "target": 0, "temp": 46.0, "rpm": 0, "missing": false,
                 "curve": "gpu-amd", "override": null, "zeroRpm": true, "parked": true }
  },
  "rgb": {
    "brightness": 100,
    "groups": { "main": { "rule": "gradient", "override": null } },
    "devices": { "it5711": "ok", "ene-0x71": "ok", "ene-0x73": "faulted: <reason>" }
  }
}
```

## What lasts

| Saved in `C:\ProgramData\PcHwCtl\settings.json`, reapplied at start and after every config reload | Memory only, gone on restart |
|---|---|
| `fan zerorpm`, `rgb brightness` | `fan set` (ttl), `fan curve`, `rgb set`, `rgb mode` |

## Displays

Monitor control runs in a user-session agent the service starts itself ([protocols/ddc-ci.md](protocols/ddc-ci.md)). `get state` carries `"displays": { "status": null, "monitors": [...] }`; `status` says why when the agent is not reachable. State file: `hw.mon.count`, `hw.mon.<n>.name|main|bright|power`, the keys of the board's old `mon.*` feed under `hw.`.

## Sensor feed

PcHwCtl is the only LibreHardwareMonitor reader on the PC: ESP32-Helper subscribes instead of reading its own. Config `feed`:

- `intervalMs` 500.
- `every` {Cpu: 1000, Memory: 1000}.
- `resolveS` 30.
- `keys`: key -> `"hardware | sensor type | sensor name [| first|avg|sum|max|min]"`, the helper's stats.ini syntax. The defaults are the handoff's 21 keys: cpu_temp, cpu_power, cpu_fan, sys_fan, vrm_temp, ram_temp, gpu1_*/gpu2_* (temp, fan, clock, load, vram, vramt, power) and igpu_load. After them come the activity keys `proc_top`, `disp_req`, `sys_req`, `exec_req` and the text keys `proc_top_by`, `disp_req_by`, `sys_req_by`, `exec_req_by` (see [Activity](#activity-for-the-boards-night-sleep-and-hibernate-timers)).

Refresh, the same split loop as the helper:

- Every `intervalMs`, only the hardware the feed, fans and RGB read is updated, in parallel.
- Hardware matched by `every` updates in the background at most that often; no tick waits for it. The CPU update costs ~320 ms.
- A full update and sensor lookup runs at start, on hardware change, and every `resolveS` while some key or fan source matches nothing.

Measured 2026-10-27: a tick waits ~94 ms (125 ms max); the background CPU update takes ~323 ms. The fan engine keeps its own 1 s tick and reads the values the loop leaves.

After `subscribe sensors` and its JSON ack, each snapshot is a block of text lines ended by an empty line:

```
seq=33
ts=1793110234999
cpu_temp=41.8
gpu1_fan=790
gpu2_power=13.34
igpu_load=
<empty line>
```

- `seq` counts up by one per snapshot. `ts` is Unix ms.
- Every configured key is always present; `key=` with no value means missing (no sensor, or no reading).
- The first block arrives right after the ack, then one per `intervalMs`. A slow reader gets the newest block, never a backlog.
- No block for 3 s means the feed is stale. The reader shows the keys as missing and reconnects.

## Process Lasso

Building blocks only; policy such as night mode lives on the board ([protocols/process-lasso.md](protocols/process-lasso.md)). `get state` carries `"lasso": { "rules": n, "eco": ["blender.exe"] }`. Replies carry `changed`, the number of running processes whose affinity or efficiency setting moved.

## Activity (for the board's night, sleep and hibernate timers)

PcHwCtl reports; the board owns thresholds, hysteresis and timers.

- **Per-exe CPU:** one `NtQuerySystemInformation` call per second (no process handles, protected processes included). `cores` is CPU time per second of wall time, summed over the exe's instances. A single-threaded job shows as 1.0, not 5 % of 20 threads. Feed keys `proc_top` and `proc_top_by` hold the busiest exe; `proc top [n]` gives more, so the board can skip its keep list.
- **Power requests:** `powercfg /requests` every `feed.requestsPollS` (5 s).
  - DISPLAY: something keeps the screen on (video).
  - SYSTEM: something keeps the PC awake (audio playing, Windows Update, downloads that ask).
  - EXECUTION: an app asks to keep running.
  - Feed keys `disp_req`, `sys_req`, `exec_req` (count, -1 unknown) plus `*_by` (first requester). `get state` has `"power": {display, displayBy, system, systemBy, execution, executionBy}`. State file: `hw.disp_req`, `hw.sys_req`, `hw.exec_req` (+ `_by`).
  - `claude.exe` holds an EXECUTION request all the time, so the board needs its own ignore list for `exec_req`.
- **Power plan:** the active plan is the feed text key `power_plan`, `get state` `power.plan` and state file `hw.plan`. Set with `power plan night|day`.

### The PcHwCtl Night plan

A copy of Power saver, created on this PC (GUID `3ceb8c3b-ac62-40f4-96ea-7268c04b5a42`). Measured 2026-10-27/28, CPU package power: ~29.5 W normal, 15.7-16.1 W with this plan plus Low Power Mode.

| Setting | Value | Why |
|---|---|---|
| Maximum processor state (E-cores, P-cores via class 1, class 2) | 30 % | caps the clock; 5 % (~450 MHz, the chip's floor) was too slow to use |
| Minimum processor state (E-cores, P-cores via class 1) | 5 % | lets idle cores drop to the floor |
| Core parking (every parking and unpark setting, AC and DC) | Balanced's | on AC Balanced keeps min cores at 100 %, so nothing parks; parking saved little next to the cap anyway |
| Heterogeneous policy / class-1 thread thresholds / latency hint min unparked | 0 / 254 / 50 | Balanced's values. Power saver's (4 / 255 / 0) keep every P-core parked, which starves the PC |
| PCIe link state power management | Maximum power savings | needs native ASPM on in the BIOS |
| Sleep / hibernate | never | the board's timers decide |
| Display off | 15 min | |

Lessons:

- Windows applies a processor cap only on a plan switch. Editing the cap into the active High performance plan did nothing, so the caps need a plan of their own.
- The plain parking setting counts all 20 cores, not one core type.
- Max cores 0 % for both core types, with the 5 % cap, made the PC unusable (fixed in Safe Mode).
- With parking at 6 of 20 cores awake and a 5 % cap (~450 MHz), PcHwCtl missed its fan tick and its watchdog restarted it. That led to the dedicated fan thread below.

### Fan tick priority

The service runs at High priority and opts out of Windows efficiency mode. The fan loop has its own thread at Highest priority, off the thread pool, so busy sensor reads and pipe clients can't delay it. RGB frames and the slow background CPU sensor read run below normal. A tick over 2 s logs its steps (config reload, hardware refresh, fan engine, state file). The watchdog's 10 s message names the step it was stuck in.

## Zero RPM (GPU fans)

A fan with a `zeroRpm` block parks below `stopBelowC` and stays parked until its curve temperature reaches `startAtC`. `zeroRpm` in the state is null for fans without the block, else whether it is on; `parked` says the fan is stopped right now.

| `park` | How the fan stops | Used for |
|---|---|---|
| `zero` | writes 0 % | a fan that accepts 0 % |
| `release` | hands the fan back to the driver, whose own auto mode stops it at idle; the curve takes it back at `startAtC` | RTX 3050 (clamps manual 0 % to about 1600 rpm) |

The RX 6600 does not use either: it has `"driver": "amd-od8"`, so its 5-point curve and Zero RPM flag go into the GPU firmware ([protocols/amd-od8.md](protocols/amd-od8.md)). With Zero RPM on, the firmware stops the fan below its fixed 52 °C; `stopBelowC`, `startAtC` and `park` do not apply. `fan set gpu-amd <pct>` writes a flat curve (at least 30 %, the firmware minimum) until it expires; 0 % means Zero RPM with the lowest curve. Everything is rewritten at every start, because Adrenalin cannot keep these settings on this Windows build.

State file keys: `hw.<fan>.zr` = 1/0 (Zero RPM on/off), `hw.<fan>.park` = 1 while parked.

## Client sketch (C#, .NET Framework 4 works)

```csharp
using (var pipe = new NamedPipeClientStream(".", "PcHwCtl", PipeDirection.InOut))
{
    pipe.Connect(1000);
    var w = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
    var r = new StreamReader(pipe);
    w.WriteLine("fan set rear-fan 60 30");
    string json = r.ReadLine();
}
```

ESP32 board path: the board sends `!hw <command>` lines over HID; ESP32PcStats forwards `<command>` to this pipe and returns the response to the board.
