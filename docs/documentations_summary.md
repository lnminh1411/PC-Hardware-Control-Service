# Documentation summary

Cross-repo system design (ESP32 board, ESP32-Helper, PcHwCtl, LocalAIHost): `E:\System Architecture Documentation\ESP32 PC Control System\ESP32-PC-Control-System.md`, with an agent-oriented copy `ESP32-PC-Control-System.agents.md` in the same folder. Where those and this repo's docs disagree, the code wins.

## Overview

- [README.md](../README.md): What PcHwCtl is (a LocalSystem Windows service replacing FanControl, OpenRGB and Twinkle Tray on one specific PC) and the hardware it is wired to. Lists the features (fans, RGB, monitors, Process Lasso, activity and power, VPN check, presence, screenshots, sensor feed, state file), requirements, publish/install/uninstall commands, the `pchwctl` CLI modes, the top-level config sections with their field names and what hot-reloads, and the pipe verbs by group, pointing to `docs/ipc.md` for detail.

## Install and operation

- [docs/install.md](install.md): Prerequisites (.NET 10, PawnIO, elevated shell, Git LFS for the model), `publish.ps1` output, every `install.ps1` switch and step (leftover agent stop, install and data directory ACLs, source-tree, junction and planted-data checks, service recovery settings, FanControl/OpenRGB disabling) and the `uninstall.ps1` switches and steps. Also covers the config, log and data file locations, which config sections reload live versus after a restart, and diagnostics: `--console --dry-run`, `--list`, `pchwctl ipc` with its exit codes, and `presence-test`.
- [scripts/Scripts.md](../scripts/Scripts.md): One line per script in `scripts/`: `publish.ps1`, `install.ps1`, `uninstall.ps1` and the dot-sourced ACL helpers in `acl.ps1`.

## IPC and service behaviour

- [docs/ipc.md](ipc.md): The authoritative reference for the `\\.\pipe\PcHwCtl` pipe: access (SYSTEM, Administrators, `ipc.allowAccount`, no network logons, 4 clients), framing and limits, and the full command table with reply shapes (fan, rgb, release, display, aff, lasso, eco, power, vpn, presence, screenshot, proc, get, subscribe). Also documents the `get state` JSON, what survives a restart or config reload, the `state.txt` key layout, the sensor feed (config, refresh loop, text block format), Process Lasso building blocks, activity keys and the "PcHwCtl Night" power plan, fan tick priority, `power do` decline reasons and power event keys, the VPN check, presence (camera check, privacy, fields, config), screenshots (flow, decline reasons, delete queue, config), the service-to-agent link, GPU Zero RPM, a C# client sketch and how ESP32-Helper relays board lines.

## Hardware protocols

- [docs/protocols/amd-od8.md](protocols/amd-od8.md): Why the RX 6600 fan bypasses LibreHardwareMonitor, and how its 5-point curve and Zero RPM flag are written straight into the Overdrive8 table through `atiadlxx.dll` (calls, structure layout, entry indexes and ranges, behaviour seen on this driver).
- [docs/protocols/ddc-ci.md](protocols/ddc-ci.md): Monitor control on this PC: why it runs in a user-session display agent (launch, random per-launch pipe, parent watch, log, config reload), the DDC/CI calls and retry rules, VCP codes 0x10/0x60/0xD6, the per-model `ddc-power` and `ddc-input` power methods for the two DELL E1715S and the MSI PRO MP275, and Windows display off and wake.
- [docs/protocols/ene-dram.md](protocols/ene-dram.md): The ENE RGB controller on the DDR5 sticks: 16-bit register access over SMBus, the register map (never write `0xAA` to apply), the read-only identity check, the DIMM-slot address remap from `0x77` to `0x71`/`0x73` after a cold boot, and the direct color update in R, B, G order.
- [docs/protocols/it5711.md](protocols/it5711.md): The Gigabyte IT5711 RGB Fusion 2 USB HID controller: 64-byte feature reports, simple commands (and the save-to-flash ones never sent), device info and byte-order calibration, per-LED ARGB packets, single-color effect packets, this board's LED index map and the init sequence.
- [docs/protocols/pawnio-smbus.md](protocols/pawnio-smbus.md): SMBus access through the PawnIO driver: where the `SmbusI801.bin` module comes from, the `PawnIOLib.dll` exports, the `ioctl_identity` and `ioctl_smbus_xfer` argument layout, and the shared `Global\Access_SMBUS.HTP.Method` mutex.
- [docs/protocols/process-lasso.md](protocols/process-lasso.md): How the service edits `prolasso.ini` (UTF-16, `DefaultAffinitiesEx`, `;` core lists) and applies rules to running processes at once, the Core Ultra 7 265K P-core and E-core cluster layout, measured package power by mode, the board's night-mode sequence built from `aff`, `lasso`, `eco` and `proc top`, and a live check of those commands.
