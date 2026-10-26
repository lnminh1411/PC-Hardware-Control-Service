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
| `rgb set <group> <#RRGGBB> [ttl]` | static color override. ttl 0 or missing = until released |
| `rgb mode <group> <off\|static\|breathing\|rainbow\|gradient> [#RRGGBB] [ttl]` | rule override; color used by static/breathing |
| `rgb brightness <0-100>` | global brightness, kept until service restart |
| `rgb release <group>` | drop the override |
| `subscribe` | after the ack line, the server pushes one `get state` style line per tick until the client disconnects |

## State

```json
{
  "fans": {
    "front-fans": { "pct": 32, "target": 32, "temp": 50.0, "rpm": 1121, "missing": false,
                    "curve": "graph", "override": { "pct": 40, "expiresInS": 52 } }
  },
  "rgb": {
    "brightness": 100,
    "groups": { "main": { "rule": "gradient", "override": null } },
    "devices": { "it5711": "ok", "ene-0x71": "ok", "ene-0x73": "faulted: <reason>" }
  }
}
```

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
