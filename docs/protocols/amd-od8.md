# AMD Overdrive8 fan control (ADL)

Notes for the RX 6600 (Navi 23, DevID 73FF) on this PC. Taken from the public ADL SDK header layout (MIT) and checked live with a read-only probe, then single-entry writes. Driver 32.0.21045.16010, Windows Insider build where Adrenalin cannot save its tuning settings.

## Why not LHM

LibreHardwareMonitor 0.9.6 drives AMD fans with `ADL2_Overdrive5_FanSpeed_Set`. On RDNA2 the driver only emulates that call, and the firmware Zero RPM logic sits on top of it: with Zero RPM on, a manual 70 % stayed at 0 rpm for over a minute at 53-56 °C. Writing the Overdrive8 table directly works.

## Calls (`atiadlxx.dll`, x64)

| Call | Use |
|---|---|
| `ADL2_Main_Control_Create(malloc_cb, 1, &ctx)` | context; the callback allocates with CoTaskMem |
| `ADL2_Overdrive_Caps(ctx, adapter, &supported, &enabled, &version)` | version 8 = Overdrive8 |
| `ADL2_Overdrive8_Init_Setting_Get(ctx, adapter, ADLOD8InitSetting*)` | ranges |
| `ADL2_Overdrive8_Current_Setting_Get(ctx, adapter, ADLOD8CurrentSetting*)` | current values |
| `ADL2_Overdrive8_Setting_Set(ctx, adapter, ADLOD8SetSetting*, ADLOD8CurrentSetting*)` | write |

The `...X2_Get` variants return `-9` (null pointer) on this driver; the fixed-table calls above work. Adapter 0 is the RX 6600 here (the first adapter whose current-setting read succeeds).

## Structures (all `int`, table count 72)

| Structure | Layout |
|---|---|
| `ADLOD8InitSetting` | `count`, `capabilities`, then `count` x { featureID, min, max, default } |
| `ADLOD8CurrentSetting` | `count`, then `count` x value |
| `ADLOD8SetSetting` | `count`, then `count` x { value, requested, reset } |

Set `count` to 72 before each call. In a write, only entries with `requested = 1` change; `reset = 1` puts that entry back to its default.

## Fan entries

| Index | Meaning | Range here | Default |
|---|---|---|---|
| 15 | Zero RPM control (1 = on) | 0..1 | 1 |
| 19, 21, 23, 25, 27 | fan curve temperature, points 1-5 (°C) | 25..100 | |
| 20, 22, 24, 26, 28 | fan curve speed, points 1-5 (%) | 30..100 | |
| 35 | Zero RPM stop temperature (°C) | 52..52 (fixed) | 52 |

Capabilities read `0x33F0D`. The curve speed cannot go below 30 %, so stopping the fan is only possible through Zero RPM, below the fixed 52 °C.

## Behavior seen

- Zero RPM 1 → 0 with the fan stopped at 56 °C: spinning within 5 s.
- The settings live in the driver until reboot or reset. The service writes them at every start because Adrenalin cannot save them on this build.
