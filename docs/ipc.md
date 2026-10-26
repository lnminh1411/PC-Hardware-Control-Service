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

## Zero RPM (GPU fans)

A fan with a `zeroRpm` block parks below `stopBelowC` and stays parked until its curve temperature reaches `startAtC`. `zeroRpm` in the state is null for fans without the block, else whether it is on; `parked` says the fan is stopped right now.

| `park` | How the fan stops | Used for |
|---|---|---|
| `zero` | writes 0 % | RX 6600 (accepts 0 %) |
| `release` | hands the fan back to the driver, whose own auto mode stops it at idle; the curve takes it back at `startAtC` | RTX 3050 (clamps manual 0 % to about 1600 rpm) |

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
