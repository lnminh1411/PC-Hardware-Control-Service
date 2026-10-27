# Process Lasso

The service runs as SYSTEM, so it can rewrite Process Lasso's config and set the affinity of other users' processes. It offers building blocks (`aff`, `lasso`, `eco`, `proc top` in [../ipc.md](../ipc.md)); the board decides when to use them.

## prolasso.ini

`C:\ProgramData\ProcessLasso\config\prolasso.ini`, UTF-16 LE with a BOM. `DefaultAffinitiesEx=name,flag,cpus,name,flag,cpus,...` holds the affinity rules. `cpus` is a Process Lasso core list with `;` between ranges (`0-1;6-9;18-19`), because `,` separates the fields. `aff set` accepts `,` and writes `;`. A name with ` (` (`svchost.exe (netprofm -p)`) targets one service instance; `aff` only touches whole-exe rules.

## Applying now

Process Lasso applies a rule when a process starts. So every change also sets the affinity of matching running processes:

- `aff set`: processes of that exe go to the rule's CPUs.
- `aff clear`: they get every CPU.
- `lasso load`: each exe with a rule in the new ini goes to it; each exe that had a rule only in the old ini gets every CPU.

## CPU layout (Core Ultra 7 265K)

From `GetLogicalProcessorInformationEx`:

- P-cores: 0, 1, 6, 7, 8, 9, 18, 19.
- E-cores: three clusters of 4, each sharing a 4 MB L2: **2-5**, **10-13** and **14-17**.

A cluster-aligned night target keeps the other clusters asleep. `14-17` is one cluster; `12-17` spans two.

Measured 2026-10-27, 1 min each, light use, `cpu_power` from the feed:

| Mode | Average CPU package power |
|---|---|
| Normal | 36.4 W, then 33.8 W |
| Pinned to 12-17 | 30.6 W |
| Efficiency mode | 31.2 W |

Pinned plus efficiency mode, and 14-17 itself, are not measured yet.

## Night mode on the board

The board's logic (2026-10-27), with these building blocks:

1. `lasso save day` once, while the normal config is in place.
2. Engage: each included exe (the keep list stays as it was) gets `aff set <exe> 14-17` and `eco on <exe>`. A saved `night.ini` plus `lasso load night` does the affinity part in one step.
3. Heavy work is left alone. Use `proc top` (cores per exe), with the board's own threshold and hysteresis. A heavy exe gets `aff clear` and `eco off` until it calms down; this check keeps running all night, since jobs started at night inherit the night rules.
4. Morning, or a resume during the day: `lasso load day`, `eco off all`.
5. Sleep and hibernate timers live on the board. They hold off on `disp_req`, `sys_req` and heavy `proc_top`, and the board needs its own ignore list for `exec_req`. The board wakes the PC from sleep to hibernate it.

## Live check (2026-10-27)

On a running `ping.exe`:

- `aff set ping.exe 12-17` gave affinity 0x3F000, with the rule in the ini.
- `aff clear` gave 0xFFFFF.
- `lasso load` of a profile saved beforehand left prolasso.ini hash-identical to the original and put ping back to 0xFFFFF.
- Bad CPU lists and path-like profile names were refused.
- `eco on ping.exe` set EcoQoS 1/1 on the running ping. A ping started afterwards got it within one sweep. `eco off` put both back to 0/0.
