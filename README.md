# PC Hardware Control Service

> **This is for my PC only.** Sensor IDs, fan curves, LED layouts and RGB protocols are hard-wired to one machine (Gigabyte B860M GAMING X WIFI6E, IT8696E SuperIO, IT5711 RGB Fusion 2, ENE DRAM). Running it on other hardware can write to the wrong registers. Use it as reference, not as a drop-in tool.

Windows service that replaces FanControl and OpenRGB on my PC. Companion Project: [ESP32-PC-Control-System](https://github.com/lnminh1411/ESP32-PC-Control-System).

## Status

All phases built: fans, IT5711 and ENE DRAM RGB, named-pipe IPC, install scripts. 47 unit tests pass. Verified on the live board in dry run (sensors, curves, IPC, state file).

## Scope

- Fan control on the IT8696E through LibreHardwareMonitor: graph, auto and flat curves, mixed temp sources, hysteresis, step limits, fail-safe back to BIOS control.
- RGB on the IT5711 (motherboard headers, ARGB strips) and ENE DRAM: static, effects, colors driven by sensor values.
- Named-pipe IPC: state, fan overrides with expiry, curve and profile switch, RGB color and mode.

## License

[AGPL-3.0](LICENSE). [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) is GPL-2.0-only, so no OpenRGB code is used here. RGB drivers are written from protocol notes in `docs/protocols/`.
