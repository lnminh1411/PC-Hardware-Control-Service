# PcHwCtl IPC

Named pipe `\\.\pipe\PcHwCtl` (name from config `ipc.pipeName`; no pipe when `ipc.enabled` is false). Access: SYSTEM, Administrators, and the account in `ipc.allowAccount` (default none; ESP32-Helper (ESP32PcStats) runs as LocalSystem, already covered). Network logons are denied, admins included. That account can run every command, screenshots and camera checks included, so don't name a shared one such as `NT AUTHORITY\LocalService`. Up to 4 clients at once.

Pipe names are machine-wide and first come, first served, so a client should connect with `TokenImpersonationLevel.Anonymous` and check who holds the pipe (`GetNamedPipeServerProcessId`) before it sends a webhook.

## Framing

UTF-8 text, one request per line (`\n`; a trailing `\r` and a leading BOM are dropped), one JSON response per line. Tokens are split on whitespace. A connection may send many requests. Max line 4096 bytes; a longer line gets `Line exceeds 4096 bytes.` and the connection closes.

Response: `{"ok":true,"data":...}` or `{"ok":false,"error":"<message>"}`. Commands that only act reply `{"ok":true,"data":null}`. A feature switched off in the config replies `ok:false`, e.g. `VPN check is off (config vpn.enabled).`

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
| `rgb set <group> <#RRGGBB> [ttl]` | static color override. ttl 0 or missing = until released; capped at `ipc.maxTtlS` |
| `rgb mode <group> <off\|static\|breathing\|rainbow\|gradient> [#RRGGBB] [ttl]` | rule override; color used by static/breathing. ttl as for `rgb set` |
| `rgb brightness <0-100>` | global brightness. Saved: survives restarts and reloads |
| `rgb release <group>` | drop the override |
| `release <fan\|group\|all>` | `all` drops every fan override and curve switch and every RGB override; a name drops the fan and/or RGB group with that name |
| `display list` | `{status, monitors:[{id, name, main, bright, power, input, powerMethod}]}`, numbered from the left: bright % (-1 no answer), power (1 on, 0 off, -1 unknown), input (VCP 0x60, -1 no answer) |
| `display bright <n\|all> <0-100>` | DDC/CI brightness |
| `display power <n\|all> <on\|off>` | per-monitor power through the model's method (config `displays.models`); `all` skips monitors without one |
| `display input <n\|all> <code>` | input source, hex or decimal, e.g. `0x11` or `17` |
| `display sleep` / `display wake` | Windows display off for every monitor / wake with a 1 px mouse nudge |
| `aff set <exe> <cpus>` | Process Lasso affinity rule, e.g. `aff set brave.exe 10-15` or `2-5,10-17`: written to prolasso.ini (kept for later starts) and applied to running processes now. Replaces the exe's whole-exe rule; per-instance rules (`svchost.exe (netprofm -p)`) stay |
| `aff clear <exe>` | removes the exe's rule; running ones get every CPU |
| `aff list` | the ini's rules: `{rules:[{name, cpus}]}` |
| `lasso save <profile>` | copies prolasso.ini to `lasso.profilesDir\<profile>.ini`. Names only (1-64 letters, digits, `_ . -`), never paths |
| `lasso load <profile>` | the profile replaces prolasso.ini; running processes follow its rules, names whose rule went away get every CPU |
| `lasso profiles` | saved profile names: `{profiles:[...]}` |
| `eco on <exe>` | Windows efficiency mode (EcoQoS: low clocks, E-cores preferred) on every instance, now and for instances started later (swept every `lasso.ecoSweepS`, 10 s). Memory only: gone on service restart |
| `eco off <exe\|all>` | gives each process back the efficiency setting it had before |
| `eco list` | exes held in efficiency mode: `{eco:[...]}` |
| `power plan` | Windows power plans: `{plans:[{name, guid, active}]}` |
| `power plan night` / `power plan day` | switches to `power.nightPlan` ("PcHwCtl Night") / `power.dayPlan` ("High performance"); replies `{plan, guid}` |
| `power plan <name\|guid>` | switches to any plan, by exact name (case-insensitive, may contain spaces) or GUID; replies `{plan, guid}` |
| `power caps` | `{sleep, hibernate}`: whether the PC can sleep, and whether hibernation is on (hiberfil.sys present) |
| `power do <sleep\|hibernate\|restart\|shutdown\|lock> [delayMs]` | accepted: `{action, accepted:true}`, the action runs as SYSTEM after `delayMs` (0-10000). Declined: `ok:false` and the reason in `error` (see [Power actions and events](#power-actions-and-events)) |
| `power status` | `{going:{kind, by, why, code, user, id, at}, lastDown:{kind, at, by, why}, wake:{from, at, sleptAt, by}\|null, update:{pending, pendingBy, running}, lastDo:{action, outcome, why, at}\|null}`: why the PC is going down now, how it last went down, the last wake, Windows Update state, and the last `power do` |
| `vpn` | `{kind, by, stack, exit:{ip, loc, warp}, checkedAt, tracedAt}`, see [VPN check](#vpn-check) |
| `presence` | one webcam check now (1-3 s): `{present, conf, luma, why, device, ms}`, see [Presence](#presence) |
| `screenshot <nonce> <webhook>` | `{nonce, accepted, why}`: captures every monitor, then a red border and a Discord post deleted later, see [Screenshot](#screenshot) |
| `proc top [n]` | the n (default 5, 1-50) busiest exes over the last second: `{processes:[{exe, cores, instances}]}`, `cores` 1.0 = one core busy, summed over instances. Includes system and kept processes |
| `get sensors` | latest sensor feed snapshot as JSON: `{seq, ts, values: {cpu_temp: 41.8, gpu1_fan: null, ..., power_plan: "PcHwCtl Night", ...}}`. Numbers are rounded to 2 decimals, text keys are strings. `No sensor snapshot yet.` before the first |
| `subscribe sensors` | ack `{"subscribed":"sensors"}`, then the server pushes every sensor feed snapshot as text, see [Sensor feed](#sensor-feed) |
| `subscribe` | ack `{"subscribed":true}`, then the server pushes one `{"ok":true,"data":<state>}` line every `tickMs` until the client disconnects |

After a successful `subscribe` the connection only receives pushes; it takes no more requests.

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
  },
  "displays": { "status": null, "monitors": [] },
  "lasso": { "rules": 12, "eco": [] },
  "power": { "plan": "High performance", "display": 0, "displayBy": "", "system": 0, "systemBy": "", "execution": 1, "executionBy": "claude.exe" }
}
```

An RGB group's `override` is `{rule, expiresInS}` (`expiresInS` null = until released). `displays`, `lasso` and `power` are null when that part is off; see [Displays](#displays), [Process Lasso](#process-lasso) and [Activity](#activity-for-the-boards-night-sleep-and-hibernate-timers).

## What lasts

| Saved in `C:\ProgramData\PcHwCtl\settings.json`, reapplied at start and after every config reload | Memory only, gone on restart |
|---|---|
| `fan zerorpm`, `rgb brightness` | `fan set` (ttl), `fan curve`, `rgb set`, `rgb mode` |

A config reload drops `fan curve` switches and unsaved Zero RPM toggles of the fans it keeps; `fan set` overrides stay, and RGB overrides stay for groups that still exist.

## State file

`C:\ProgramData\PcHwCtl\state.txt`, rewritten atomically every fan tick (`tickMs`) for ESP32-Helper (`@source hw file:///C:/ProgramData/PcHwCtl/state.txt 1000`). One `key=value` per line; keys longer than 23 characters are shortened before their last segment, and line breaks in values become spaces.

- `hw.<fan>.pct`, `.rpm`, `.temp` (curve temperature), `.ovr` (1 while a `fan set` override runs), `.zr`, `.park`. The fan name is lower-cased with anything outside `a-z 0-9 _` turned into `_` (`hw.front_fans.rpm`).
- `hw.mix`: the `mix` sensor, when the config has one.
- `hw.rgb.bright`, `hw.rgb.<group>` (the active rule, override first).
- `hw.plan`, the power and VPN keys, `hw.disp_req` etc. and `hw.mon.*`, described in the sections below.

## Displays

Monitor control runs in a user-session agent the service starts itself ([protocols/ddc-ci.md](protocols/ddc-ci.md)). `get state` carries `"displays": { "status": null, "monitors": [...] }`; `status` says why when the agent is not reachable. State file: `hw.mon.count`, `hw.mon.<n>.name|main|bright|power`, the keys of the board's old `mon.*` feed under `hw.`.

## Sensor feed

PcHwCtl is the only LibreHardwareMonitor reader on the PC: ESP32-Helper subscribes instead of reading its own. Config `feed`:

- `enabled` true. Off: no feed (`get sensors` and `subscribe sensors` reply that it is off) and the fan loop refreshes the hardware itself.
- `intervalMs` 500 (100-10000).
- `requestsPollS` 5, see [Activity](#activity-for-the-boards-night-sleep-and-hibernate-timers).
- `every` {Cpu: 1000, Memory: 1000}.
- `resolveS` 30.
- `keys`: key -> `"hardware | sensor type | sensor name [| first|avg|sum|max|min]"`, the helper's stats.ini syntax. The defaults are the handoff's 21 keys: cpu_temp, cpu_power, cpu_fan, sys_fan, vrm_temp, ram_temp, gpu1_*/gpu2_* (temp, fan, clock, load, vram, vramt, power) and igpu_load. Keys are 1-23 of `a-z 0-9 _ .` (the board's rule). Setting `keys` or `every` replaces the whole default list, it doesn't merge. After them come the activity keys `proc_top`, `disp_req`, `sys_req`, `exec_req` and the text keys `proc_top_by`, `disp_req_by`, `sys_req_by`, `exec_req_by` (see [Activity](#activity-for-the-boards-night-sleep-and-hibernate-timers)), then `power_plan`, the power keys (see [Power actions and events](#power-actions-and-events)) and, with `vpn.enabled`, the VPN keys (see [VPN check](#vpn-check)).

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

Building blocks only; policy such as night mode lives on the board ([protocols/process-lasso.md](protocols/process-lasso.md)). `get state` carries `"lasso": { "rules": n, "eco": ["blender.exe"] }` (`rules` -1 = the ini can't be read). `aff set|clear`, `lasso save|load` and `eco on|off` reply `{result, changed}`: a message, and the number of running processes whose affinity or efficiency setting moved.

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
| Core parking (every parking and unpark setting, AC and DC) | Balanced's | Windows parks idle cores itself (Process Lasso showed 7 of 20 parked under Balanced) even with min cores at 100 %. High performance doesn't park |
| Heterogeneous policy / class-1 thread thresholds / latency hint min unparked | 0 / 254 / 50 | Balanced's values. Power saver's (4 / 255 / 0) keep every P-core parked, which starves the PC |
| PCIe link state power management | Maximum power savings | needs native ASPM on in the BIOS |
| Sleep / hibernate | never | the board's timers decide |
| Display off | 5 min | the same as night mode's idle engage on the ESP32 board |

Lessons:

- Windows applies a processor cap only on a plan switch. Editing the cap into the active High performance plan did nothing, so the caps need a plan of their own.
- The plain parking setting counts all 20 cores, not one core type.
- Max cores 0 % for both core types, with the 5 % cap, made the PC unusable (fixed in Safe Mode).
- With parking at 6 of 20 cores awake and a 5 % cap (~450 MHz), PcHwCtl missed its fan tick and its watchdog restarted it. That led to the dedicated fan thread below.

### Fan tick priority

The service runs at High priority and opts out of Windows efficiency mode. The fan loop has its own thread at Highest priority, off the thread pool, so busy sensor reads and pipe clients can't delay it. RGB frames and the slow background CPU sensor read run below normal. A tick over 2 s logs its steps (config reload, hardware refresh, fan engine, state file). The watchdog's 10 s message names the step it was stuck in.

## Power actions and events

PcHwCtl is the only program that sleeps, hibernates, restarts, shuts down or locks the PC for the board. The helper keeps the board's `!power` nonce and ack and calls `power do`.

**`power do`:** PcHwCtl accepts or declines at once, then runs the action after `delayMs`, so a client can ack the board first (the helper passes 1500). Sleep and hibernate start at least 0.3 s after the reply; restart and shutdown force other sessions' apps closed, with the comment "Requested by the ESP32 board (PcHwCtl power do)" in event 1074; lock locks the console user's desktop. Declined, with the reason in `error`:

| Reason | When |
|---|---|
| `A shutdown is already under way (by <exe>).` (or restart, update restart, ...) | a shutdown or restart was announced in the last 120 s |
| `Already going to sleep.` / `hibernate` | sleep or hibernate while one is under way |
| `Windows is installing updates.` | sleep or hibernate while `update_running` |
| `This PC can't sleep.` / `Hibernation is off.` | from `power caps` |
| `No user signed in at the console (...)` | lock with nobody signed in |
| `InitiateShutdown: <Win32 message> (<code>)` | Windows refused, e.g. 1115 a shutdown is in progress, 1190 one is scheduled |

With no delay, restart, shutdown and lock run before the reply, so a Win32 error declines them. What Windows refuses after the reply (a delayed action, or a driver vetoing sleep) shows as `power_do` = `<action> failed <why>`, e.g. `sleep failed SetSuspendState: ...`.

**Keys.** The feed and the state file (with `hw.` in front) carry:

| Key | Value |
|---|---|
| `going` | `none`, `shutdown`, `restart`, `update_shutdown`, `update_restart`, `sleep`, `hibernate`. Clears after 120 s, or on wake |
| `going_by` | the exe that asked (event 1074), `board` for `power do` sleep/hibernate, else `-` |
| `going_why` | Windows' reason title and the comment (`Other (Planned): Requested by the ESP32 board ...`), Kernel-Power's sleep reason (`Application API`, `Button or Lid`, ...) or kernel shutdown reason |
| `going_code` | reason code decoded: `0x80020010 os/service_pack planned` |
| `going_user` | the account it ran for, `NT AUTHORITY\SYSTEM` for services |
| `going_id` (value) | the event's record id; changes for every new announcement |
| `last_down` | how the PC went down before this boot: `shutdown`, `restart`, `update_shutdown`, `update_restart`, `unexpected`, `bluescreen`, `power_button`, `unknown` |
| `last_down_at`, `last_down_by`, `last_down_why` | local `ddd HH:mm`; exe; reason with code, or `bugcheck 0x9f`, `power button held`, `lost power while sleep`, `lost power or stopped responding` |
| `wake_from`, `wake_at`, `wake_by` | last wake since boot: `sleep`/`hibernate`, time, Windows' wake source (`Power Button`, `Device -USB Input Device`, `Unknown`) |
| `update_pending` (value), `update_pending_by` | 1 when Windows wants a restart for updates; `wu` (Windows Update), `cbs` (servicing stack), `wu+cbs` |
| `update_running` (value) | 1 while Windows installs updates with nobody signed in, after an update restart |
| `power_do`, `power_do_at` | the last `power do`: `<action> accepted`, `<action> declined <why>`, `<action> failed <why>` |

Sources: System log 1074 (who/why/code/comment/user), Kernel-Power 42 (entering sleep) and 109 (kernel shutdown, e.g. the power button, used when no 1074 came first), Kernel-Power 41 / EventLog 6008 at boot (unclean: bugcheck, power button, sleep in progress), Power-Troubleshooter 1 (wake source). `update_pending` reads the RebootRequired and RebootPending registry keys every 30 s; `update_running` looks for TiWorker, poqexec or SetupHost with no explorer every 5 s.

## VPN check

Is the PC's internet traffic going through a tunnel, and where does it come out? PcHwCtl asks Windows which adapter carries traffic to 8.8.8.8 (`GetBestInterface`, so VPNs that add `0.0.0.0/1` + `128.0.0.0/1` routes count) and classifies it:

- `warp`: Cloudflare WARP. It tunnels through Cloudflare but does not hide where the PC is; websites still see roughly the right country. WARP's DNS-only mode (1.1.1.1) has no tunnel, so it reads `none`.
- `vpn`: a tunnel driver or VPN client (WireGuard, Wintun, TAP/OpenVPN, SoftEther, NordLynx, Proton, Mullvad, Tailscale exit nodes, any adapter with VPN in its name, ...) or a Windows PPP VPN.
- `none`: anything else, or no route.

**When:** only on Windows' adapter events (address or availability changes), never on a timer. A VPN connecting fires several events in a row, so the check waits until they have been quiet for `vpn.settleS` (default 5 s) and runs once. It also runs once at start.

**Exit:** when the check's result changes (or the last trace failed), PcHwCtl fetches `https://www.cloudflare.com/cdn-cgi/trace` (5 s timeout; by name, because VPN clients such as SoftEther route 1.1.1.1 outside the tunnel): the public IP, its country, and Cloudflare's own `warp=on|plus|off`. This is PcHwCtl's only internet request; `vpn.trace: false` turns it off. `exit_loc` is how to tell WARP from a real VPN: with WARP it stays your own country.

**VPN + WARP:** `vpn` is the outer tunnel, the one holding the route. `vpn_stack` lists every tunnel whose routes cover at least half the IPv4 space (0.0.0.0/0, the 0/1 + 128/1 halves, or WARP's ~117 routes around private ranges), route holder first: `warp+vpn` = WARP runs through the VPN (the trace shows `warp=on` with the VPN server's country, since Cloudflare places you where WARP connects from), `vpn+warp` = the VPN runs through WARP (`warp=off`, the VPN server's IP). Tailscale without an exit node only routes 100.64.0.0/10 and stays out.

Feed and state file keys (`hw.` in the state file): `vpn` (none, warp, vpn), `vpn_by` (adapter description), `vpn_stack` (e.g. `vpn`, `warp+vpn`, `vpn+warp`, `none`), `exit_ip`, `exit_loc` (country code), `exit_warp` (on, plus, off). `-` when unknown.

Config: `"vpn": { "enabled": true, "trace": true, "settleS": 5 }` (settleS 1-60). Applies after a service restart.

## Presence

`presence` answers "is someone at the PC right now?" with one check, run when the command arrives. It is off unless `presence.enabled` is true. There is no timer: the client decides how often to ask (the ESP32 helper asks at the interval the board sets with `!pres <seconds>`, or once on `!pres now`) and what to do with the answer (intrusion alert). A good answer stands for `minIntervalS` (default 10), so a looping client can't keep the camera on.

As an intrusion check it has limits: only a nearly black frame reads `unknown`, so a lens cover that lets light through can read `no`, and one frame can't tell a photo from a person. A client should alert on `unknown` too.

**How:** the display agent (the user's session; session 0 has no camera access) opens camera `presence.camera` through DirectShow at 640x480, drops `settleFrames` (default 26, about 1 s) while exposure settles, keeps one frame and closes the camera, so the LED is on for about 1-2 s. YOLO26x (COCO, shipped in `models\yolo26x-fp16-ov`, about 65 ms on the NPU) runs on `presence.device` (default NPU through OpenVINO; CPU when the NPU fails). The answer is the best person score.

**Privacy:** the frame lives only in the agent's memory. The agent never writes it to a file or sends it: the camera frame, its scaled copy and the model's input tensor are zeroed right after inference, before the reply. Buffers inside OpenCV's DirectShow backend, its resize, the NPU plugin and the CPU fallback are not wiped, and Windows can page any of this memory out. Only the fields below leave the agent, and the agent doesn't log them.

| Field | Meaning |
|---|---|
| `present` | `yes` (person score >= `minConf`), `no`, or `unknown` |
| `conf` | best person score, 0-1. npu-bench, YOLO26x on a Logi C270 in a near-black room (luma 1-2): away at most 0.001, seated 0.55-0.93 |
| `luma` | mean frame brightness 0-255 |
| `why` | `-`, or why `present` is `unknown`: `dark` (nobody seen and luma < `darkLuma`, default 0.5: a covered lens or black frame), `camera` (in use, unplugged, or Windows camera access for desktop apps off), `model` (OpenVINO failed), `agent` (no display agent, e.g. nobody logged on), `off` |
| `device` | where the model ran: `NPU`, `CPU`, `-` |
| `ms` | how long the check took |

The first check after the agent starts also compiles the model (cached in `%LOCALAPPDATA%\PcHwCtl\ovcache`, about 5 s the first time).

Config: `"presence": { "enabled": true, "device": "NPU", "model": "", "camera": 0, "minConf": 0.4, "darkLuma": 0.5, "settleFrames": 26, "minIntervalS": 10 }` (`enabled` defaults to false; `model` empty = the shipped one). The agent rereads it when the file changes; the service reads `enabled` only at start. `pchwctl presence-test` runs two checks in a console with the installed config, for diagnosis.

## Screenshot

`screenshot <nonce> <webhook>` (from the ESP32 helper, on the board's `!shot <nonce>`; the webhook is the helper's own). It is off unless `screenshot.enabled` is true, and only goes to webhooks listed in `screenshot.webhookIds`. The display agent captures and shows the border; the service posts, so the webhook never enters the user's session:

1. The service checks the webhook, then asks the agent for a capture (`shot <nonce>`, no webhook).
2. The agent refuses when nobody could see the border: the displays are off (Windows' console display state, or every monitor powered off over DDC), or the desktop is locked or behind a UAC prompt.
3. It captures every monitor into one PNG, in memory only, then shows a red border and "Screenshot Captured" on every monitor for `borderS` seconds (1-60). The border comes after the capture, so it is never in the image. If it can't be shown, the PNG is discarded.
4. The PNG goes to the service as raw bytes after the reply line, and the agent zeroes its copy.
5. The service waits until the border has been up for `borderS`, posts the PNG to the Discord webhook (`?wait=true`, for the message id; redirects are not followed), then zeroes it.
6. It deletes the post `deleteMin` minutes later. Due deletes live in `C:\ProgramData\PcHwCtl\shot-deletes.txt` (admin-only), so a restart doesn't lose them. No answer, a timeout, HTTP 408, 429 or 5xx are retried with growing gaps for 7 days. Any other refusal, or running out of time, logs that the post is still on Discord. A service stop waits for a post in flight, so its delete is recorded.

The reply comes after the capture (about a second); the post follows `borderS` later. `accepted:false` and `why`:
- `busy`: the last screenshot is still posting, or came less than `minIntervalS` ago (default 10).
- `webhook`: not a plain `https://discord.com/api/webhooks/<id>/<token>` URL (only `?thread_id=` may follow), or `<id>` isn't in `webhookIds`. Any program can create its own Discord webhook, so this list is what stops a pipe client sending the desktop to itself.
- `display`: the displays are off, so the border would go unseen.
- `locked`: the desktop is locked, or a secure prompt is up.
- `capture`: the capture or the border failed.
- `agent`: no display agent, or it didn't answer.
- `off`: screenshots are off.

Every request and its outcome is in the service log (`log.txt`), without the webhook. On disk the webhook is in `shot-deletes.txt`, sealed with DPAPI for SYSTEM. Discord may keep a deleted attachment for a while.

Config: `"screenshot": { "enabled": true, "webhookIds": ["1234567890"], "deleteMin": 5, "borderS": 5, "minIntervalS": 10 }` (`enabled` defaults to false, `webhookIds` to none; `deleteMin` 0 = keep the post, at most 10080; `minIntervalS` 0-3600). The id is the number in the webhook URL, not its token. The service reads this section at start (a change needs a restart); the agent rereads it for the border.

### The agent link

The service starts the display agent in the logged-on user's session with a new random pipe name each time (`PcHwCtl-display-<32 hex>`, passed as `--pipe`), and only talks to the process it started: the pipe's server pid must be that agent's. Any account can run the installed exe, so neither the pipe name nor the exe path would prove anything. Replies are limited to 64 KiB a line, and monitor lists are checked before the service uses them.

The agent's words are the `display` ones without `display` (`list`, `bright`, `power`, `input`, `sleep`, `wake`), `presence`, and `shot <nonce>`, which replies `{"why", "bytes"}` followed by `bytes` raw PNG bytes. The service gives up after 15 s for display commands, 60 s for `presence` and 30 s for `shot`; a presence reply that isn't `yes`, `no` or `unknown` with finite numbers reads as `unknown` / `agent`.

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
using (var pipe = new NamedPipeClientStream(".", "PcHwCtl", PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Anonymous))
{
    pipe.Connect(1000);
    var w = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
    var r = new StreamReader(pipe);
    w.WriteLine("fan set rear-fan 60 30");
    string json = r.ReadLine();
}
```

ESP32 board path: the board sends `!hw <id> <command>` lines over USB; ESP32-Helper forwards `<command>` to this pipe, one at a time, only when it is `ping`, `rgb ...` or `fan set|curve|release|zerorpm ...`, and returns `<id> ok` or `<id> err <why>` to the board. Other board lines map to their own commands (`!shot` to `screenshot`, `!pres` to `presence`, `!power` to `power do`, `!mon` to `display ...`).
