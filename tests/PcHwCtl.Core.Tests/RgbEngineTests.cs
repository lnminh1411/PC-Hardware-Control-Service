using PcHwCtl.Core;
using PcHwCtl.Core.Rgb;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class SliceAFakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan delta) => UtcNow += delta;
    public void AdvanceSeconds(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
}

public sealed class SliceAFakeLedDevice : ILedDevice
{
    public string Id { get; }
    public IReadOnlyList<LedZone> Zones { get; set; }
    public bool Initialized { get; private set; }
    public bool Disposed { get; private set; }
    public bool InvalidateCalled { get; private set; }

    public bool FailInitialize { get; set; }
    public bool ThrowOnInitialize { get; set; }
    public bool ThrowOnFlush { get; set; }
    public bool ThrowOnSetZone { get; set; }

    public int FlushCount { get; private set; }
    public List<(string Zone, RgbColor[] Colors)> SetZoneCalls { get; } = [];

    public SliceAFakeLedDevice(string id, params LedZone[] zones)
    {
        Id = id;
        Zones = zones;
    }

    public int InitializeCount { get; private set; }

    public bool Initialize()
    {
        InitializeCount++;
        if (ThrowOnInitialize) throw new InvalidOperationException("Device error on Initialize");
        if (FailInitialize) return false;
        Initialized = true;
        return true;
    }

    public void SetZone(string zone, ReadOnlySpan<RgbColor> colors)
    {
        if (ThrowOnSetZone) throw new InvalidOperationException("Device error on SetZone");
        SetZoneCalls.Add((zone, colors.ToArray()));
    }

    public void Flush()
    {
        if (ThrowOnFlush) throw new InvalidOperationException("Device error on Flush");
        FlushCount++;
    }

    public void Invalidate()
    {
        InvalidateCalled = true;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    public void ResetCallRecords()
    {
        FlushCount = 0;
        SetZoneCalls.Clear();
        InvalidateCalled = false;
    }
}

public class RgbEngineTests
{
    [Fact]
    public void Test01_StaticGroup_WritesColorToBothZones_OneFlush()
    {
        // 1. Static group on dev/* across 2 zones writes the color to both zones, one Flush.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("zone1", 3), new LedZone("zone2", 5));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/*"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#FF55AA" }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });
        engine.Tick();

        Assert.Equal(2, dev.SetZoneCalls.Count);
        Assert.Equal("zone1", dev.SetZoneCalls[0].Zone);
        Assert.Equal(3, dev.SetZoneCalls[0].Colors.Length);
        Assert.All(dev.SetZoneCalls[0].Colors, c => Assert.Equal(new RgbColor(255, 85, 170), c));

        Assert.Equal("zone2", dev.SetZoneCalls[1].Zone);
        Assert.Equal(5, dev.SetZoneCalls[1].Colors.Length);
        Assert.All(dev.SetZoneCalls[1].Colors, c => Assert.Equal(new RgbColor(255, 85, 170), c));

        Assert.Equal(1, dev.FlushCount);
    }

    [Fact]
    public void Test02_SameColorsNextFrame_NoSetZone_NoFlush()
    {
        // 2. Same colors next frame: no SetZone, no Flush.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("zone1", 2), new LedZone("zone2", 2));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/*"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#00FF00" }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });
        engine.Tick();
        Assert.Equal(2, dev.SetZoneCalls.Count);
        Assert.Equal(1, dev.FlushCount);

        dev.ResetCallRecords();
        clock.AdvanceSeconds(0.1);
        engine.Tick();

        Assert.Empty(dev.SetZoneCalls);
        Assert.Equal(0, dev.FlushCount);
    }

    [Fact]
    public void Test03_GradientStopsAndInterpolation()
    {
        // 3. Gradient with stops (62.4 #FFCB80) (63.2 #E6A817) (64.5 #E6A817) (68.9 #DC3737), SmoothingS 0:
        // 50 -> #FFCB80, 62.8 -> midpoint of first two (per channel, rounded), 66.7 -> midway E6A817..DC3737, 80 -> #DC3737; missing source -> #DC3737.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z", 1));
        var clock = new SliceAFakeClock();
        float? sensorVal = 50f;
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/z"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "gradient",
                        Source = "/temp/cpu",
                        SmoothingS = 0,
                        Stops =
                        [
                            new GradientStop { At = 62.4f, Color = "#FFCB80" },
                            new GradientStop { At = 63.2f, Color = "#E6A817" },
                            new GradientStop { At = 64.5f, Color = "#E6A817" },
                            new GradientStop { At = 68.9f, Color = "#DC3737" }
                        ]
                    }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => sensorVal, clock, _ => { });

        // 50 -> #FFCB80 (255, 203, 128)
        sensorVal = 50f;
        engine.Tick();
        Assert.Equal(new RgbColor(255, 203, 128), dev.SetZoneCalls.Last().Colors[0]);

        // 62.8 -> midpoint of first two (per channel, rounded) -> (243, 186, 76)
        sensorVal = 62.8f;
        clock.AdvanceSeconds(0.1);
        engine.Tick();
        Assert.Equal(new RgbColor(243, 186, 76), dev.SetZoneCalls.Last().Colors[0]);

        // 66.7 -> midway E6A817..DC3737 -> (225, 112, 39)
        sensorVal = 66.7f;
        clock.AdvanceSeconds(0.1);
        engine.Tick();
        Assert.Equal(new RgbColor(225, 112, 39), dev.SetZoneCalls.Last().Colors[0]);

        // 80 -> #DC3737 -> (220, 55, 55)
        sensorVal = 80f;
        clock.AdvanceSeconds(0.1);
        engine.Tick();
        Assert.Equal(new RgbColor(220, 55, 55), dev.SetZoneCalls.Last().Colors[0]);

        // missing source -> last stop #DC3737 -> (220, 55, 55)
        engine.InvalidateAll();
        sensorVal = null;
        clock.AdvanceSeconds(0.1);
        engine.Tick();
        Assert.Equal(new RgbColor(220, 55, 55), dev.SetZoneCalls.Last().Colors[0]);
    }

    [Fact]
    public void Test04_GradientSmoothing_JumpFiftyToSeventy_GradualAndFormulaMatch()
    {
        // 4. Gradient smoothing 2 s: source jumps 50 -> 70 with 1 s frames; color moves gradually
        // (smoothed value after 1 s equals 50 + 20*(1-exp(-0.5)) within 1e-3, checked through a gradient whose stops make color a linear function of value).
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z", 1));
        var clock = new SliceAFakeClock();
        float? sensorVal = 50f;

        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/z"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "gradient",
                        Source = "/temp/cpu",
                        SmoothingS = 2f,
                        Stops =
                        [
                            new GradientStop { At = 0f, Color = "#000000" },
                            new GradientStop { At = 100f, Color = "#640000" } // R channel equals At
                        ]
                    }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => sensorVal, clock, _ => { });

        // Frame 0 at t = 0: first sample = raw 50
        sensorVal = 50f;
        engine.Tick();
        Assert.Equal(50, dev.SetZoneCalls.Last().Colors[0].R);
        Assert.Equal(50f, engine.GetGroupSmoothedValue("main"));

        // Frame 1 at t = 1s: source jumps to 70
        sensorVal = 70f;
        clock.AdvanceSeconds(1.0);
        engine.Tick();

        float expectedSmoothed = 50f + 20f * (1f - MathF.Exp(-0.5f));
        float actualSmoothed = engine.GetGroupSmoothedValue("main")!.Value;
        Assert.InRange(actualSmoothed, expectedSmoothed - 1e-3f, expectedSmoothed + 1e-3f);

        byte rFrame1 = dev.SetZoneCalls.Last().Colors[0].R;
        Assert.Equal((byte)MathF.Round(expectedSmoothed, MidpointRounding.AwayFromZero), rFrame1);
        Assert.True(rFrame1 > 50 && rFrame1 < 70);

        // Frame 2 at t = 2s: source stays at 70, smoothed value continues moving toward 70
        clock.AdvanceSeconds(1.0);
        engine.Tick();
        byte rFrame2 = dev.SetZoneCalls.Last().Colors[0].R;
        Assert.True(rFrame2 > rFrame1 && rFrame2 <= 70);
    }

    [Fact]
    public void Test05_Brightness50_HalvesChannels()
    {
        // 5. Brightness 50 halves channels.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z", 1));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 50,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/z"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#C86432" } // (200, 100, 50)
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });
        engine.Tick();

        var writtenColor = dev.SetZoneCalls[0].Colors[0];
        Assert.Equal(new RgbColor(100, 50, 25), writtenColor);
    }

    [Fact]
    public void Test06_Breathing_AtZeroIsBlack_AtHalfPeriodIsFullColor()
    {
        // 6. Breathing at t = 0 is black, at t = PeriodS/2 is full color.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z", 1));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/z"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "breathing",
                        Color = "#00FF80", // (0, 255, 128)
                        PeriodS = 4f
                    }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });

        // at t = 0 -> black
        engine.Tick();
        Assert.Equal(RgbColor.Black, dev.SetZoneCalls[0].Colors[0]);

        // at t = PeriodS / 2 = 2s -> full color (0, 255, 128)
        clock.AdvanceSeconds(2.0);
        engine.Tick();
        Assert.Equal(new RgbColor(0, 255, 128), dev.SetZoneCalls.Last().Colors[0]);
    }

    [Fact]
    public void Test07_Rainbow_ThreeLeds_RedGreenBlue()
    {
        // 7. Rainbow: 3 LEDs at t=0 give hues 0, 1/3, 2/3 (red, green, blue).
        var dev = new SliceAFakeLedDevice("dev", new LedZone("strip", 3));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/strip"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "rainbow",
                        PeriodS = 6f
                    }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });
        engine.Tick();

        var colors = dev.SetZoneCalls[0].Colors;
        Assert.Equal(3, colors.Length);
        // hue 0 -> Red (255, 0, 0)
        Assert.Equal(new RgbColor(255, 0, 0), colors[0]);
        // hue 1/3 -> Green (0, 255, 0)
        Assert.Equal(new RgbColor(0, 255, 0), colors[1]);
        // hue 2/3 -> Blue (0, 0, 255)
        Assert.Equal(new RgbColor(0, 0, 255), colors[2]);
    }

    [Fact]
    public void Test08_OverrideWithTtl_ReplacesRule_ExpiresBackToConfigured_AllOverridesEveryGroup()
    {
        // 8. Override with ttl 10 s replaces the rule; after 11 s the configured rule is back. all overrides every group.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z1", 1), new LedZone("z2", 1));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["g1"] = new RgbGroupConfig
                {
                    Zones = ["dev/z1"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#101010" }
                },
                ["g2"] = new RgbGroupConfig
                {
                    Zones = ["dev/z2"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#101010" }
                }
            }
        };

        var engine = new RgbEngine(config, [dev], _ => null, clock, _ => { });

        // Initial tick: both output #101010 (16, 16, 16)
        engine.Tick();
        Assert.Equal(new RgbColor(16, 16, 16), dev.SetZoneCalls.First(c => c.Zone == "z1").Colors[0]);
        Assert.Equal(new RgbColor(16, 16, 16), dev.SetZoneCalls.First(c => c.Zone == "z2").Colors[0]);

        // Apply 'all' override with 10s TTL
        var overrideRule = new RgbRuleConfig { Type = "static", Color = "#202020" };
        engine.SetOverride("all", overrideRule, TimeSpan.FromSeconds(10));

        Assert.True(engine.Groups["g1"].OverrideRule != null);
        Assert.True(engine.Groups["g2"].OverrideRule != null);
        Assert.Equal("static", engine.Groups["g1"].OverrideRule);

        dev.ResetCallRecords();
        engine.Tick();
        Assert.Equal(new RgbColor(32, 32, 32), dev.SetZoneCalls.First(c => c.Zone == "z1").Colors[0]);
        Assert.Equal(new RgbColor(32, 32, 32), dev.SetZoneCalls.First(c => c.Zone == "z2").Colors[0]);

        // Advance by 11 seconds (exceeding 10s TTL)
        clock.AdvanceSeconds(11);
        dev.ResetCallRecords();
        engine.Tick();

        Assert.False(engine.Groups["g1"].OverrideRule != null);
        Assert.False(engine.Groups["g2"].OverrideRule != null);
        Assert.Null(engine.Groups["g1"].OverrideRule);

        // Configured rule #101010 is back
        Assert.Equal(new RgbColor(16, 16, 16), dev.SetZoneCalls.First(c => c.Zone == "z1").Colors[0]);
        Assert.Equal(new RgbColor(16, 16, 16), dev.SetZoneCalls.First(c => c.Zone == "z2").Colors[0]);
    }

    [Fact]
    public void Test09_FlushThrowing_MarksFaulted_RetriedAfter10sAndRecovers()
    {
        // 9. Flush throwing marks device faulted, no exception escapes Tick; Initialize retried after 10 s and recovers.
        var dev = new SliceAFakeLedDevice("dev", new LedZone("z", 1));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["dev/z"],
                    Rule = new RgbRuleConfig { Type = "static", Color = "#FF0000" }
                }
            }
        };

        var logs = new List<string>();
        var engine = new RgbEngine(config, [dev], _ => null, clock, logs.Add);

        Assert.Equal("ok", engine.DeviceStatus["dev"]);

        // Cause Flush to throw
        dev.ThrowOnFlush = true;
        var ex = Record.Exception(() => engine.Tick());
        Assert.Null(ex); // No exception escapes Tick

        Assert.StartsWith("faulted:", engine.DeviceStatus["dev"]);

        // Advance 5 seconds - retry not yet due
        clock.AdvanceSeconds(5);
        dev.ThrowOnFlush = false;
        engine.Tick();
        Assert.StartsWith("faulted:", engine.DeviceStatus["dev"]);

        // Advance another 6 seconds (total 11s >= 10s) - retry occurs and recovers
        clock.AdvanceSeconds(6);
        engine.Tick();

        Assert.Equal("ok", engine.DeviceStatus["dev"]);
        Assert.True(dev.FlushCount > 0);
    }

    [Fact]
    public void Test10_Validator_ReportsUnsortedGradientAndUnknownFanSource()
    {
        // 10. Validator reports an unsorted gradient and an unknown fan source.
        var root = new Config
        {
            Fans = new Dictionary<string, FanConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu-fan"] = new FanConfig { Enabled = true }
            },
            Sensors = new Dictionary<string, SensorConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["cpu_temp"] = new SensorConfig { Type = "mix" }
            }
        };

        var rgb = new RgbConfig
        {
            Fps = 10,
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["g1"] = new RgbGroupConfig
                {
                    Zones = ["dev/z1"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "gradient",
                        Source = "cpu_temp",
                        Stops =
                        [
                            new GradientStop { At = 70f, Color = "#FF0000" },
                            new GradientStop { At = 50f, Color = "#00FF00" }
                        ]
                    }
                },
                ["g2"] = new RgbGroupConfig
                {
                    Zones = ["dev/z2"],
                    Rule = new RgbRuleConfig
                    {
                        Type = "gradient",
                        Source = "fan:nonexistent.rpm",
                        Stops =
                        [
                            new GradientStop { At = 20f, Color = "#0000FF" },
                            new GradientStop { At = 80f, Color = "#FFFF00" }
                        ]
                    }
                }
            }
        };

        var problems = RgbConfigValidator.Validate(rgb, root);

        Assert.Contains(problems, p => p.Contains("not sorted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, p => p.Contains("invalid source", StringComparison.OrdinalIgnoreCase) && p.Contains("fan:nonexistent.rpm"));
    }

    [Fact]
    public void Test11_DeviceFpsCap_ThrottlesFlushesPerDevice_ReportsTiming()
    {
        // 11. Rainbow at 60 fps: an uncapped device flushes every frame, "ene-*" capped at 20 flushes every third.
        var fast = new SliceAFakeLedDevice("it5711", new LedZone("strip", 3));
        var slow = new SliceAFakeLedDevice("ene-0x71", new LedZone("dimm", 3));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Fps = 60,
            Brightness = 100,
            DeviceFps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["ene-*"] = 20 },
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig
                {
                    Zones = ["it5711/strip", "ene-0x71/dimm"],
                    Rule = new RgbRuleConfig { Type = "rainbow", PeriodS = 1f }
                }
            }
        };

        var engine = new RgbEngine(config, [fast, slow], _ => null, clock, _ => { });
        for (int i = 0; i < 60; i++)
        {
            engine.Tick();
            clock.Advance(TimeSpan.FromTicks(166_667));
        }

        Assert.Equal(60, fast.FlushCount);
        Assert.Equal(20, slow.FlushCount);
        Assert.Equal(20, slow.SetZoneCalls.Count); // skipped frames are merged, only the newest colors go out

        string report = engine.TakeTimingReport();
        Assert.Contains("60 fps", report);
        Assert.Contains("ene-0x71 20/s", report);
        Assert.Contains("it5711 60/s", report);
        Assert.Equal("0 fps", engine.TakeTimingReport());

        var validator = RgbConfigValidator.Validate(
            new RgbConfig { DeviceFps = new(StringComparer.OrdinalIgnoreCase) { ["ene-*"] = 0 } }, new Config());
        Assert.Contains(validator, p => p.Contains("deviceFps", StringComparison.Ordinal));
    }

    [Fact]
    public void Test12_ReinitializeAll_InitializesAgain_ResendsColors_FaultsFailures()
    {
        // After a resume every device runs Initialize again (ENE leaves direct mode in hibernate) and resends colors.
        var good = new SliceAFakeLedDevice("good", new LedZone("z", 2));
        var bad = new SliceAFakeLedDevice("bad", new LedZone("z", 2));
        var clock = new SliceAFakeClock();
        var config = new RgbConfig
        {
            Brightness = 100,
            Groups = new Dictionary<string, RgbGroupConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["main"] = new RgbGroupConfig { Zones = ["good/*", "bad/*"], Rule = new RgbRuleConfig { Type = "static", Color = "#102030" } }
            }
        };
        var logs = new List<string>();
        var engine = new RgbEngine(config, [good, bad], _ => null, clock, logs.Add);
        engine.Tick();
        Assert.Equal(1, good.InitializeCount);
        good.ResetCallRecords();

        bad.FailInitialize = true;
        engine.ReinitializeAll();

        Assert.Equal(2, good.InitializeCount);
        Assert.True(good.InvalidateCalled);
        Assert.Equal("ok", engine.DeviceStatus["good"]);
        Assert.StartsWith("faulted", engine.DeviceStatus["bad"]);
        Assert.Contains(logs, l => l.Contains("re-initialized after resume: good"));

        clock.AdvanceSeconds(1);
        engine.Tick();
        Assert.NotEmpty(good.SetZoneCalls);   // colors resent although they did not change

        bad.FailInitialize = false;
        clock.AdvanceSeconds(11);
        engine.Tick();
        Assert.Equal("ok", engine.DeviceStatus["bad"]);
    }
}
