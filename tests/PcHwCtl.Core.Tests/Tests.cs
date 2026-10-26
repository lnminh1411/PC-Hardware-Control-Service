using PcHwCtl.Core;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan delta) => UtcNow += delta;
    public void AdvanceSeconds(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
}

public sealed class FakeSensorSource : ISensorSource
{
    public Dictionary<string, float?> Sensors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryRead(string reference, out float value)
    {
        if (Sensors.TryGetValue(reference, out var v) && v.HasValue)
        {
            value = v.Value;
            return true;
        }
        value = 0f;
        return false;
    }
}

public sealed class FakeFanOutput : IFanOutput
{
    public List<(string ControlId, float Percent)> SetCalls { get; } = [];
    public List<string> SetDefaultCalls { get; } = [];

    public void Set(string controlId, float percent) => SetCalls.Add((controlId, percent));
    public void SetDefault(string controlId) => SetDefaultCalls.Add(controlId);
}

public class CoreTests
{
    [Fact]
    public void Test01_GraphInterpolation()
    {
        // 1. Graph interpolation with points (52.469,20.055) (60.209,38.722) (65,80) (75,90) (80,100):
        // 40 -> 20.055, 65 -> 80, 70 -> 85, 90 -> 100.
        var points = new (float Temp, float Pct)[]
        {
            (52.469f, 20.055f),
            (60.209f, 38.722f),
            (65f, 80f),
            (75f, 90f),
            (80f, 100f)
        };

        var curve = new GraphCurve(points, 2f, 2f, 2f, 2f, true);

        Assert.Equal(20.055f, curve.Interpolate(40f), 3);
        Assert.Equal(80f, curve.Interpolate(65f), 3);
        Assert.Equal(85f, curve.Interpolate(70f), 3);
        Assert.Equal(100f, curve.Interpolate(90f), 3);
    }

    [Fact]
    public void Test02_GraphHysteresis()
    {
        // 2. Graph hysteresis: applied 60; T 61 for 5 s -> unchanged; T 62.5 for 1 s -> unchanged; still 62.5 at 2 s -> applied.
        var points = new (float Temp, float Pct)[]
        {
            (50f, 20f),
            (60f, 40f),
            (70f, 80f),
            (80f, 100f)
        };

        var curve = new GraphCurve(points, hysteresisUp: 2f, hysteresisDown: 2f, responseUpS: 2f, responseDownS: 2f, ignoreHysteresisAtLimits: false);
        var clock = new FakeClock();

        // First evaluation applies immediately (applied 60)
        float? out0 = curve.Evaluate(60f, clock.UtcNow);
        Assert.Equal(40f, out0); // 60 -> 40

        // T 61 for 5 s -> unchanged (61 - 60 = 1 < hysteresisUp 2)
        for (int i = 1; i <= 5; i++)
        {
            clock.AdvanceSeconds(1);
            float? out61 = curve.Evaluate(61f, clock.UtcNow);
            Assert.Equal(40f, out61);
        }

        // T 62.5 for 1 s -> unchanged (62.5 - 60 = 2.5 >= 2, but only 1 s < responseUpS 2)
        clock.AdvanceSeconds(1);
        float? out62_1s = curve.Evaluate(62.5f, clock.UtcNow);
        Assert.Equal(40f, out62_1s);

        // Held 1 s since first seen -> still unchanged
        clock.AdvanceSeconds(1);
        Assert.Equal(40f, curve.Evaluate(62.5f, clock.UtcNow));

        // Held 2 s since first seen -> applied
        clock.AdvanceSeconds(1);
        float? out62_2s = curve.Evaluate(62.5f, clock.UtcNow);
        float expectedPct = curve.Interpolate(62.5f);
        Assert.Equal(expectedPct, out62_2s!.Value, 3);
    }

    [Fact]
    public void Test03_GraphLimits()
    {
        // 3. Graph limits: applied 79.5, T 80.6 at once -> 100 immediately.
        var points = new (float Temp, float Pct)[]
        {
            (52.469f, 20.055f),
            (60.209f, 38.722f),
            (65f, 80f),
            (75f, 90f),
            (80f, 100f)
        };

        var curve = new GraphCurve(points, hysteresisUp: 2f, hysteresisDown: 2f, responseUpS: 2f, responseDownS: 2f, ignoreHysteresisAtLimits: true);
        var clock = new FakeClock();

        // Initial evaluation applies immediately
        float? out1 = curve.Evaluate(79.5f, clock.UtcNow);
        Assert.True(out1 < 100f);

        // T 80.6 at once: 80.6 >= 80 (last point) and ignoreHysteresisAtLimits is true
        clock.AdvanceSeconds(0.1);
        float? out2 = curve.Evaluate(80.6f, clock.UtcNow);
        Assert.Equal(100f, out2);
    }

    [Fact]
    public void Test04_AutoCurve()
    {
        // 4. Auto (45/75, 50-100, step 5, deadband 5, response 1): 45 -> 50, 75 -> 100, 60 -> 75;
        // applied 60, T 63 for 3 s -> 75 unchanged; T 65 held 1 s -> 85.
        var auto = new AutoCurve(idleTemp: 45f, loadTemp: 75f, minPct: 50f, maxPct: 100f, step: 5f, deadband: 5f, responseS: 1f);
        var clock = new FakeClock();

        Assert.Equal(50f, auto.CalculateTarget(45f));
        Assert.Equal(100f, auto.CalculateTarget(75f));
        Assert.Equal(75f, auto.CalculateTarget(60f));

        // Applied 60
        float? initial = auto.Evaluate(60f, clock.UtcNow);
        Assert.Equal(75f, initial);

        // T 63 for 3 s -> 75 unchanged (|63 - 60| = 3 < deadband 5)
        for (int i = 1; i <= 3; i++)
        {
            clock.AdvanceSeconds(1);
            Assert.Equal(75f, auto.Evaluate(63f, clock.UtcNow));
        }

        // T 65 held 1 s -> 85
        // At 0s of holding 65:
        clock.AdvanceSeconds(0.5);
        Assert.Equal(75f, auto.Evaluate(65f, clock.UtcNow));

        // Held 0.5 s since first seen -> unchanged
        clock.AdvanceSeconds(0.5);
        Assert.Equal(75f, auto.Evaluate(65f, clock.UtcNow));

        // Held 1 s since first seen -> applied
        clock.AdvanceSeconds(0.5);
        Assert.Equal(85f, auto.Evaluate(65f, clock.UtcNow));
    }

    [Fact]
    public void Test05_MixMaxAndMissing()
    {
        // 5. Mix max of [40, 55] -> 55; one source missing with allowMissing false -> fan goes 100 immediately, no step limit.
        var cfg = new Config
        {
            Sensors =
            {
                ["mix"] = new SensorConfig
                {
                    Type = "mix",
                    Fn = "max",
                    Sources = ["/temp/1", "/temp/2"],
                    AllowMissing = false
                }
            },
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "graph",
                    Source = "mix",
                    Points = [[0f, 0f], [100f, 100f]],
                    ResponseUpS = 0,
                    ResponseDownS = 0
                }
            },
            Fans =
            {
                ["fan1"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    Enabled = true,
                    StepUp = 8f,
                    StepDown = 8f
                }
            }
        };

        var src = new FakeSensorSource();
        src.Sensors["/temp/1"] = 40f;
        src.Sensors["/temp/2"] = 55f;

        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        engine.Tick();
        Assert.Equal(55f, engine.States["fan1"].CurrentPct);

        // One source missing with allowMissing false: last good value holds for staleSensorS, then failsafe
        src.Sensors["/temp/2"] = null;
        engine.Tick();
        Assert.False(engine.States["fan1"].SourceMissing);

        clock.AdvanceSeconds(cfg.StaleSensorS + 1);
        engine.Tick();

        Assert.True(engine.States["fan1"].SourceMissing);
        Assert.Equal(100f, engine.States["fan1"].CurrentPct);
    }

    [Fact]
    public void Test06_StaleSensor()
    {
        // 6. Stale: source returns same success then fails for 6 s with staleSensorS 5 -> fan at 100.
        var cfg = new Config
        {
            StaleSensorS = 5,
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "graph",
                    Source = "/temp/1",
                    Points = [[0f, 0f], [100f, 100f]],
                    ResponseUpS = 0,
                    ResponseDownS = 0
                }
            },
            Fans =
            {
                ["fan1"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    Enabled = true,
                    StepUp = 8f,
                    StepDown = 8f
                }
            }
        };

        var src = new FakeSensorSource();
        src.Sensors["/temp/1"] = 40f;

        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        // Initial tick: success at t=0
        engine.Tick();
        Assert.Equal(40f, engine.States["fan1"].CurrentPct);

        // Fails: TryRead returns false
        src.Sensors["/temp/1"] = null;

        // Fails for 1..5 s (within staleSensorS 5) -> fan still holds last good
        for (int i = 1; i <= 5; i++)
        {
            clock.AdvanceSeconds(1);
            engine.Tick();
            Assert.False(engine.States["fan1"].SourceMissing);
            Assert.Equal(40f, engine.States["fan1"].CurrentPct);
        }

        // Fails for 6 s (> staleSensorS 5) -> fan at 100
        clock.AdvanceSeconds(1);
        engine.Tick();
        Assert.True(engine.States["fan1"].SourceMissing);
        Assert.Equal(100f, engine.States["fan1"].CurrentPct);
    }

    [Fact]
    public void Test07_StepLimit()
    {
        // 7. Step limit 8: previous 20, target 80 -> 28, 36, 44; down from 80 to 20 -> 72.
        var cfg = new Config
        {
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "graph",
                    Source = "/temp/1",
                    Points = [[0f, 0f], [100f, 100f]],
                    ResponseUpS = 0,
                    ResponseDownS = 0
                }
            },
            Fans =
            {
                ["fan1"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    Enabled = true,
                    StepUp = 8f,
                    StepDown = 8f
                }
            }
        };

        var src = new FakeSensorSource();
        src.Sensors["/temp/1"] = 20f;

        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        // Set previous to 20
        engine.Tick();
        Assert.Equal(20f, engine.States["fan1"].CurrentPct);

        // Target 80 -> stepUp 8
        src.Sensors["/temp/1"] = 80f;

        engine.Tick();
        Assert.Equal(28f, engine.States["fan1"].CurrentPct);

        engine.Tick();
        Assert.Equal(36f, engine.States["fan1"].CurrentPct);

        engine.Tick();
        Assert.Equal(44f, engine.States["fan1"].CurrentPct);

        // Down from 80 to 20 -> 72
        // Jump fan to 80 first via override or multiple ticks
        while (engine.States["fan1"].CurrentPct < 80f)
        {
            engine.Tick();
        }
        Assert.Equal(80f, engine.States["fan1"].CurrentPct);

        // Now target is 20 -> down step 8 -> 72
        src.Sensors["/temp/1"] = 20f;
        engine.Tick();
        Assert.Equal(72f, engine.States["fan1"].CurrentPct);
    }

    [Fact]
    public void Test08_StartPercentage()
    {
        // 8. Start 32: previous 0, target 20 -> 32.
        var cfg = new Config
        {
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "graph",
                    Source = "/temp/1",
                    Points = [[0f, 0f], [100f, 100f]],
                    ResponseUpS = 0,
                    ResponseDownS = 0
                }
            },
            Fans =
            {
                ["fan1"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    Enabled = true,
                    StartPct = 32f,
                    StepUp = 8f,
                    StepDown = 8f
                }
            }
        };

        var src = new FakeSensorSource();
        src.Sensors["/temp/1"] = 0f;

        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        // Fan previous is 0
        engine.Tick();
        Assert.Equal(0f, engine.States["fan1"].CurrentPct);

        // Target 20: 0 < target < startPct 32 -> target becomes 32
        src.Sensors["/temp/1"] = 20f;
        engine.Tick();
        Assert.Equal(32f, engine.States["fan1"].TargetPct);
    }

    [Fact]
    public void Test09_FloorAndOverride()
    {
        // 9. Floor 100 with override 30 -> 100; override 40 on a floor-0 fan, ttl 10 s -> 40, after 11 s back to curve.
        var cfg = new Config
        {
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "graph",
                    Source = "/temp/1",
                    Points = [[0f, 0f], [100f, 100f]],
                    ResponseUpS = 0,
                    ResponseDownS = 0
                }
            },
            Fans =
            {
                ["fan-floor-100"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    FloorPct = 100f,
                    Enabled = true
                },
                ["fan-floor-0"] = new FanConfig
                {
                    Control = "/ctrl/2",
                    Curve = "c",
                    FloorPct = 0f,
                    Enabled = true
                }
            }
        };

        var src = new FakeSensorSource();
        src.Sensors["/temp/1"] = 70f;

        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        // Floor 100 with override 30 -> 100
        engine.SetOverride("fan-floor-100", 30f, TimeSpan.FromSeconds(60));
        engine.Tick();
        Assert.Equal(100f, engine.States["fan-floor-100"].CurrentPct);

        // Override 40 on a floor-0 fan, ttl 10 s -> 40, after 11 s back to curve
        // Step limit (8 %/tick) also applies to overrides, so give it ticks to settle
        engine.SetOverride("fan-floor-0", 40f, TimeSpan.FromSeconds(10));
        for (int i = 0; i < 5; i++) engine.Tick();
        Assert.Equal(40f, engine.States["fan-floor-0"].CurrentPct);

        // After 11 s the override expires and the fan ramps back to the curve
        clock.AdvanceSeconds(11);
        for (int i = 0; i < 5; i++) engine.Tick();
        Assert.Equal(70f, engine.States["fan-floor-0"].CurrentPct);
    }

    [Fact]
    public void Test10_ShutdownIdempotent()
    {
        // 10. Shutdown calls SetDefault once per written control; second Shutdown no extra calls.
        var cfg = new Config
        {
            Curves =
            {
                ["c"] = new CurveConfig
                {
                    Type = "flat",
                    Pct = 50f
                }
            },
            Fans =
            {
                ["fan1"] = new FanConfig
                {
                    Control = "/ctrl/1",
                    Curve = "c",
                    Enabled = true
                },
                ["fan2"] = new FanConfig
                {
                    Control = "/ctrl/2",
                    Curve = "c",
                    Enabled = true
                }
            }
        };

        var src = new FakeSensorSource();
        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        var engine = new FanEngine(cfg, src, fanOut, clock, _ => { }, dryRun: false);

        engine.Tick();
        Assert.Equal(2, fanOut.SetCalls.Count);
        Assert.Empty(fanOut.SetDefaultCalls);

        engine.Shutdown();
        Assert.Equal(2, fanOut.SetDefaultCalls.Count);
        Assert.Contains("/ctrl/1", fanOut.SetDefaultCalls);
        Assert.Contains("/ctrl/2", fanOut.SetDefaultCalls);

        // Second Shutdown no extra calls
        engine.Shutdown();
        Assert.Equal(2, fanOut.SetDefaultCalls.Count);
    }

    [Fact]
    public void Test11_ImporterOnFanControlJson()
    {
        // 11. Importer on fancontrol.json: fans cpu-fans, rear-fan, front-fans, water-pump;
        // front-fans startPct 32; cpu-fans curve auto, water-pump curve flat pct 100;
        // mix fn max with 5 sources including sel:GpuAmd|Temperature|GPU Core;
        // graph has 5 points and source mix; disabled controls absent.
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "fancontrol.json");
        if (!File.Exists(fixturePath))
        {
            fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "fancontrol.json");
        }
        if (!File.Exists(fixturePath))
        {
            // Look up to repo root
            fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/fancontrol.json"));
        }

        string json = File.ReadAllText(fixturePath);
        Config config = FanControlImporter.Import(json);

        // Fans: cpu-fans, rear-fan, front-fans, water-pump
        Assert.Equal(4, config.Fans.Count);
        Assert.True(config.Fans.ContainsKey("cpu-fans"));
        Assert.True(config.Fans.ContainsKey("rear-fan"));
        Assert.True(config.Fans.ContainsKey("front-fans"));
        Assert.True(config.Fans.ContainsKey("water-pump"));

        // front-fans startPct 32
        Assert.Equal(32f, config.Fans["front-fans"].StartPct);

        // cpu-fans curve auto
        Assert.Equal("auto", config.Fans["cpu-fans"].Curve);

        // water-pump curve flat pct 100
        Assert.Equal("flat", config.Fans["water-pump"].Curve);
        Assert.Equal(100f, config.Curves["flat"].Pct);

        // mix fn max with 5 sources including sel:GpuAmd|Temperature|GPU Core
        Assert.True(config.Sensors.ContainsKey("mix"));
        var mix = config.Sensors["mix"];
        Assert.Equal("max", mix.Fn);
        Assert.Equal(5, mix.Sources.Count);
        Assert.Contains("sel:GpuAmd|Temperature|GPU Core", mix.Sources);

        // graph has 5 points and source mix
        Assert.True(config.Curves.ContainsKey("graph"));
        var graph = config.Curves["graph"];
        Assert.Equal("mix", graph.Source);
        Assert.NotNull(graph.Points);
        Assert.Equal(5, graph.Points.Count);

        // disabled controls absent
        Assert.False(config.Fans.ContainsKey("fan-4"));
        Assert.False(config.Fans.ContainsKey("fan-6"));
        Assert.False(config.Fans.ContainsKey("amd-radeon-rx-6600"));
        Assert.False(config.Fans.ContainsKey("gpu-fan"));
    }

    [Fact]
    public void Test12_IdentifierMatcher()
    {
        // 12. IdentifierMatcher: /lpc/it8696e/control/0 matches /lpc/it8696e/0/control/0; two candidates normalizing the same -> null.
        string wanted = "/lpc/it8696e/control/0";
        var singleCandidate = new[] { "/lpc/it8696e/0/control/0" };
        var match1 = IdentifierMatcher.Match(wanted, singleCandidate);
        Assert.Equal("/lpc/it8696e/0/control/0", match1);

        var twoCandidates = new[] { "/lpc/it8696e/0/control/0", "/lpc/it8696e/1/control/0" };
        var match2 = IdentifierMatcher.Match(wanted, twoCandidates);
        Assert.Null(match2);
    }

    [Fact]
    public void Test13_ConfigLoaderValidation()
    {
        // 13. ConfigLoader rejects unsorted points and an unknown curve key, message names both.
        string badConfigJson = """
        {
          "curves": {
            "bad-graph": {
              "type": "graph",
              "source": "/cpu/temp",
              "points": [[80, 100], [50, 20]]
            }
          },
          "fans": {
            "fan1": {
              "control": "/ctrl/0",
              "curve": "unknown-curve-key"
            }
          }
        }
        """;

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadText(badConfigJson));
        Assert.Contains("points not sorted by temp", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown-curve-key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test14_ReloadRebuildsEditedCurveAndReleasesOldControl()
    {
        Config Make(float pct, string control) => new()
        {
            Curves = { ["c"] = new CurveConfig { Type = "flat", Pct = pct } },
            Fans = { ["fan1"] = new FanConfig { Control = control, Curve = "c", StepUp = 100, StepDown = 100 } }
        };

        var fanOut = new FakeFanOutput();
        var engine = new FanEngine(Make(50f, "/ctrl/1"), new FakeSensorSource(), fanOut, new FakeClock(), _ => { }, dryRun: false);
        engine.Tick();
        Assert.Equal(50f, engine.States["fan1"].CurrentPct);

        // Same curve name, new value
        engine.ReplaceConfig(Make(70f, "/ctrl/1"));
        engine.Tick();
        Assert.Equal(70f, engine.States["fan1"].CurrentPct);

        // Control moved: old one goes back to BIOS
        engine.ReplaceConfig(Make(70f, "/ctrl/2"));
        engine.Tick();
        engine.Shutdown();
        Assert.Contains("/ctrl/1", fanOut.SetDefaultCalls);
        Assert.Contains("/ctrl/2", fanOut.SetDefaultCalls);
    }

    private static (FanEngine Engine, FakeSensorSource Src, FakeFanOutput Out, FakeClock Clock) ZeroRpmRig(string park)
    {
        var cfg = new Config
        {
            Curves = { ["c"] = new CurveConfig { Type = "graph", Source = "/t", Points = [[40f, 30f], [70f, 100f]] } },
            Fans =
            {
                ["gpu"] = new FanConfig
                {
                    Control = "/ctrl/gpu",
                    Curve = "c",
                    StepUp = 100,
                    StepDown = 100,
                    ZeroRpm = new ZeroRpmConfig { StopBelowC = 50, StartAtC = 60, Park = park }
                }
            }
        };
        var src = new FakeSensorSource();
        var fanOut = new FakeFanOutput();
        var clock = new FakeClock();
        return (new FanEngine(cfg, src, fanOut, clock, _ => { }, false), src, fanOut, clock);
    }

    private static void TickAt(FanEngine engine, FakeSensorSource src, FakeClock clock, float temp)
    {
        src.Sensors["/t"] = temp;
        engine.Tick();
        clock.AdvanceSeconds(1);
    }

    [Fact]
    public void Test15_ZeroRpmParkZero_Hysteresis()
    {
        // 15. park "zero": below stopBelowC writes 0 %, stays parked until startAtC, then follows the curve.
        var (engine, src, fanOut, clock) = ZeroRpmRig("zero");

        TickAt(engine, src, clock, 45);
        Assert.Equal(("/ctrl/gpu", 0f), fanOut.SetCalls[^1]);
        Assert.True(engine.States["gpu"].Parked);
        Assert.True(engine.States["gpu"].ZeroRpm);

        TickAt(engine, src, clock, 55); // between stop and start: still parked
        Assert.True(engine.States["gpu"].Parked);
        Assert.Single(fanOut.SetCalls);

        TickAt(engine, src, clock, 61); // start: back on the curve
        Assert.False(engine.States["gpu"].Parked);
        Assert.Equal(79f, fanOut.SetCalls[^1].Percent, 1);

        TickAt(engine, src, clock, 55); // above stop: keeps spinning
        Assert.False(engine.States["gpu"].Parked);

        TickAt(engine, src, clock, 49);
        Assert.Equal(("/ctrl/gpu", 0f), fanOut.SetCalls[^1]);
        Assert.Empty(fanOut.SetDefaultCalls);

        // Starting inside the band (between stop and start) parks, as when Zero RPM is switched on.
        var (fresh, freshSrc, _, freshClock) = ZeroRpmRig("zero");
        TickAt(fresh, freshSrc, freshClock, 55);
        Assert.True(fresh.States["gpu"].Parked);
    }

    [Fact]
    public void Test16_ZeroRpmParkRelease_OverrideAndToggle()
    {
        // 16. park "release": parking hands the fan to the driver once (SetDefault), unparking writes the curve again.
        //     A % override and the IPC toggle both keep the fan spinning.
        var (engine, src, fanOut, clock) = ZeroRpmRig("release");

        TickAt(engine, src, clock, 45); // never written yet: nothing to release
        Assert.True(engine.States["gpu"].Parked);
        Assert.Empty(fanOut.SetCalls);
        Assert.Empty(fanOut.SetDefaultCalls);

        TickAt(engine, src, clock, 61);
        Assert.Equal(79f, fanOut.SetCalls[^1].Percent, 1);

        TickAt(engine, src, clock, 45);
        TickAt(engine, src, clock, 45);
        Assert.Equal(["/ctrl/gpu"], fanOut.SetDefaultCalls);

        TickAt(engine, src, clock, 61); // taken back from the driver
        Assert.Equal(79f, fanOut.SetCalls[^1].Percent, 1);
        Assert.Equal(2, fanOut.SetCalls.Count);

        engine.SetOverride("gpu", 70, TimeSpan.FromMinutes(1));
        TickAt(engine, src, clock, 45);
        Assert.Equal(70f, fanOut.SetCalls[^1].Percent);
        Assert.False(engine.States["gpu"].Parked);
        engine.ClearOverride("gpu");

        engine.SetZeroRpm("gpu", false);
        TickAt(engine, src, clock, 45);
        Assert.Equal(41.7f, fanOut.SetCalls[^1].Percent, 1);
        Assert.False(engine.States["gpu"].ZeroRpm);

        engine.SetZeroRpm("gpu", null);
        TickAt(engine, src, clock, 45);
        Assert.True(engine.States["gpu"].Parked);
        Assert.Equal(2, fanOut.SetDefaultCalls.Count);

        Assert.Throws<ArgumentException>(() => engine.SetZeroRpm("nope", true));
    }

    [Fact]
    public void Test17_ZeroRpmValidation()
    {
        // 17. startAtC must exceed stopBelowC, park must be zero|release, and a floor cannot coexist with parking.
        var cfg = new Config
        {
            Curves = { ["c"] = new CurveConfig { Type = "flat", Pct = 50 } },
            Fans =
            {
                ["gpu"] = new FanConfig
                {
                    Control = "/ctrl/gpu",
                    Curve = "c",
                    FloorPct = 20,
                    ZeroRpm = new ZeroRpmConfig { StopBelowC = 60, StartAtC = 50, Park = "spin" }
                }
            }
        };

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains(ex.Errors, e => e.Contains("startAtC"));
        Assert.Contains(ex.Errors, e => e.Contains("'spin'"));
        Assert.Contains(ex.Errors, e => e.Contains("floorPct"));
    }
}
