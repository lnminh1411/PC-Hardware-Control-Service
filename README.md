# PC Hardware Control Service

> **This is for my PC only.** Sensor IDs, fan curves, LED layouts and RGB protocols are hard-wired to one machine (Gigabyte B860M GAMING X WIFI6E, IT8696E SuperIO, IT5711 RGB Fusion 2, ENE DRAM). Running it on other hardware can write to the wrong registers. Use it as reference, not as a drop-in tool.

Windows service that replaces FanControl and OpenRGB on my PC, with a local IPC API so my [ESP32 carrier board](https://github.com/lnminh1411/ESP32-PC-Control-System) can read and set fans and RGB.

## Status

Planning. See [Plan.md](Plan.md) for current setup, architecture, phases and open questions.

## Scope

- Fan control on the IT8696E through LibreHardwareMonitor: graph, auto and flat curves, mixed temp sources, hysteresis, step limits, fail-safe back to BIOS control.
- RGB on the IT5711 (motherboard headers, ARGB strips) and ENE DRAM: static, effects, colors driven by sensor values.
- Named-pipe IPC: state, fan overrides with expiry, curve and profile switch, RGB color and mode.

## License

[AGPL-3.0](LICENSE). RGB protocol code is ported from [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0-or-later, used here under GPL-3.0, which AGPL-3.0 section 13 allows combining with).
