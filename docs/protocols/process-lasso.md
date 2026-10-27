# Process Lasso

The service runs as SYSTEM, so it can rewrite Process Lasso's config and set the affinity of other users' processes. It offers building blocks (`aff`, `lasso` in [../ipc.md](../ipc.md)); the board decides when to use them.

## prolasso.ini

`C:\ProgramData\ProcessLasso\config\prolasso.ini`, UTF-16 LE with a BOM. `DefaultAffinitiesEx=name,flag,cpus,name,flag,cpus,...` holds the affinity rules. `cpus` is a Process Lasso core list with `;` between ranges (`0-1;6-9;18-19`), because `,` separates the fields. `aff set` accepts `,` and writes `;`. A name with ` (` (`svchost.exe (netprofm -p)`) targets one service instance; `aff` only touches whole-exe rules.

## Applying now

Process Lasso applies a rule when a process starts. So every change also sets the affinity of matching running processes:

- `aff set`: processes of that exe go to the rule's CPUs.
- `aff clear`: they get every CPU.
- `lasso load`: each exe with a rule in the new ini goes to it; each exe that had a rule only in the old ini gets every CPU.

## Night mode on the board

What ESP32-Helper's Night.cs did, as commands:

1. `lasso save day` once, while the normal config is in place.
2. Build a night ini (E-cores 12-17 for non-critical processes), place it in the profiles dir as `night.ini`, or send `aff set <exe> 12-17` per process.
3. Night: `lasso load night`. Morning: `lasso load day`.
4. Hold off while `displayRequest` is 1 (video). Any no-word failsafe is the board's.

## Live check (2026-10-27)

On a running `ping.exe`:

- `aff set ping.exe 12-17` gave affinity 0x3F000, with the rule in the ini.
- `aff clear` gave 0xFFFFF.
- `lasso load` of a profile saved beforehand left prolasso.ini hash-identical to the original and put ping back to 0xFFFFF.
- Bad CPU lists and path-like profile names were refused.
