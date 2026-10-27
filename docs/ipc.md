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

## Process Lasso

Building blocks only; policy such as night mode lives on the board ([protocols/process-lasso.md](protocols/process-lasso.md)). `get state` carries `"lasso": { "rules": n, "displayRequest": 1|0|-1, "displayRequestBy": "brave.exe" }`; `displayRequest` is 1 while a process holds the display awake (video). State file: `hw.disp_req`, `hw.disp_req_by`. Replies carry `changed`, the number of running processes whose affinity moved.

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
