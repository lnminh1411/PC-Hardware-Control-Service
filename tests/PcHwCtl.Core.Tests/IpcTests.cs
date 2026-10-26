using System.Text.Json;
using PcHwCtl.Core;
using PcHwCtl.Core.Control;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class IpcFakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    public void AdvanceSeconds(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
}

public sealed class IpcFakeFanControl : IFanControl
{
    public Dictionary<string, FanState> StatesDict { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, FanState> States => StatesDict;

    public List<string> FansList { get; } = ["cpu-fans", "rear-fan", "front-fans", "water-pump"];
    public IReadOnlyCollection<string> FanNames => FansList;

    public HashSet<string> Curves { get; } = new(StringComparer.OrdinalIgnoreCase) { "graph", "auto", "flat" };

    public List<(string Fan, float Pct, TimeSpan Ttl)> SetOverrideCalls { get; } = [];
    public List<string> ClearOverrideCalls { get; } = [];
    public List<(string Fan, string Curve)> SetCurveOverrideCalls { get; } = [];
    public List<string> ClearCurveOverrideCalls { get; } = [];

    public bool HasCurve(string curve) => Curves.Contains(curve);

    public void SetOverride(string fan, float pct, TimeSpan ttl)
    {
        SetOverrideCalls.Add((fan, pct, ttl));
    }

    public void ClearOverride(string fan)
    {
        ClearOverrideCalls.Add(fan);
    }

    public void SetCurveOverride(string fan, string curve)
    {
        SetCurveOverrideCalls.Add((fan, curve));
    }

    public void ClearCurveOverride(string fan)
    {
        ClearCurveOverrideCalls.Add(fan);
    }

    public HashSet<string> ZeroRpmFans { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Fan, bool? Enabled)> SetZeroRpmCalls { get; } = [];

    public bool SupportsZeroRpm(string fan) => ZeroRpmFans.Contains(fan);

    public void SetZeroRpm(string fan, bool? enabled)
    {
        SetZeroRpmCalls.Add((fan, enabled));
    }
}

public sealed class IpcFakeRgbControl : IRgbControl
{
    public Dictionary<string, RgbGroupState> GroupsDict { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = new RgbGroupState("gradient", null, null),
        ["ram"] = new RgbGroupState("gradient", null, null)
    };
    public IReadOnlyDictionary<string, RgbGroupState> Groups => GroupsDict;

    public Dictionary<string, string> DeviceStatusDict { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it5711"] = "ok",
        ["ene-0x71"] = "ok",
        ["ene-0x73"] = "faulted: timeout"
    };
    public IReadOnlyDictionary<string, string> DeviceStatus => DeviceStatusDict;

    public float Brightness { get; set; } = 100f;

    public List<(string Group, RgbRuleConfig Rule, TimeSpan? Ttl)> SetOverrideCalls { get; } = [];
    public List<string> ClearOverrideCalls { get; } = [];

    public void SetOverride(string group, RgbRuleConfig rule, TimeSpan? ttl)
    {
        SetOverrideCalls.Add((group, rule, ttl));
    }

    public void ClearOverride(string group)
    {
        ClearOverrideCalls.Add(group);
    }
}

public class IpcTests
{
    private static CommandProcessor CreateProcessor(
        IFanControl? fans = null,
        IRgbControl? rgb = null,
        IpcConfig? cfg = null,
        IClock? clock = null)
    {
        var f = fans ?? new IpcFakeFanControl();
        var r = rgb ?? new IpcFakeRgbControl();
        var c = cfg ?? new IpcConfig { OverrideTtlS = 60, MaxTtlS = 3600 };
        var clk = clock ?? new IpcFakeClock();
        return new CommandProcessor(f, r, () => c, clk);
    }

    [Fact]
    public void Test01_PingReturnsExactJson()
    {
        // 1. ping returns exactly {"ok":true,"data":{"pong":true}}.
        var proc = CreateProcessor();
        string res = proc.Execute("ping");
        Assert.Equal("{\"ok\":true,\"data\":{\"pong\":true}}", res);
    }

    [Fact]
    public void Test02_FanSetSpecificTtl()
    {
        // 2. fan set rear-fan 60 30 calls SetOverride("rear-fan", 60, 30 s).
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        string res = proc.Execute("fan set rear-fan 60 30");
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var call = Assert.Single(fans.SetOverrideCalls);
        Assert.Equal("rear-fan", call.Fan);
        Assert.Equal(60f, call.Pct);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Ttl);
    }

    [Fact]
    public void Test03_FanSetTtlCappingAndRejection()
    {
        // 3. fan set rear-fan 60 uses ttl 60; fan set rear-fan 60 99999 is capped at 3600; fan set rear-fan 60 0 is rejected.
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        // ttl omitted -> default 60
        string resDefault = proc.Execute("fan set rear-fan 60");
        using (var doc = JsonDocument.Parse(resDefault))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Equal(TimeSpan.FromSeconds(60), fans.SetOverrideCalls[0].Ttl);

        // ttl 99999 -> capped at 3600
        string resCapped = proc.Execute("fan set rear-fan 60 99999");
        using (var doc = JsonDocument.Parse(resCapped))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Equal(TimeSpan.FromSeconds(3600), fans.SetOverrideCalls[1].Ttl);

        // ttl 0 -> rejected
        string resZero = proc.Execute("fan set rear-fan 60 0");
        using (var doc = JsonDocument.Parse(resZero))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
        }
        Assert.Equal(2, fans.SetOverrideCalls.Count);
    }

    [Fact]
    public void Test04_FanSetValidation()
    {
        // 4. fan set nope 50 is ok:false and names the fan; fan set rear-fan 101 is ok:false.
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        // Unknown fan names the fan in error
        string resNope = proc.Execute("fan set nope 50");
        using (var docNope = JsonDocument.Parse(resNope))
        {
            Assert.False(docNope.RootElement.GetProperty("ok").GetBoolean());
            string error = docNope.RootElement.GetProperty("error").GetString()!;
            Assert.Contains("nope", error, StringComparison.OrdinalIgnoreCase);
        }

        // Pct > 100 rejected
        string res101 = proc.Execute("fan set rear-fan 101");
        using (var doc101 = JsonDocument.Parse(res101))
        {
            Assert.False(doc101.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public void Test05_FanSetAll()
    {
        // 5. fan set all 70 10 hits every fan.
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        string res = proc.Execute("fan set all 70 10");
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        Assert.Equal(fans.FansList.Count, fans.SetOverrideCalls.Count);
        foreach (string fanName in fans.FansList)
        {
            Assert.Contains(fans.SetOverrideCalls, c => c.Fan == fanName && c.Pct == 70f && c.Ttl == TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Test06_FanCurveSwitchAndUnknownCurve()
    {
        // 6. fan curve front-fans flat calls SetCurveOverride; an unknown curve is rejected.
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        string resValid = proc.Execute("fan curve front-fans flat");
        using (var docValid = JsonDocument.Parse(resValid))
        {
            Assert.True(docValid.RootElement.GetProperty("ok").GetBoolean());
        }
        var call = Assert.Single(fans.SetCurveOverrideCalls);
        Assert.Equal("front-fans", call.Fan);
        Assert.Equal("flat", call.Curve);

        string resUnknown = proc.Execute("fan curve front-fans nonexistent");
        using (var docUnknown = JsonDocument.Parse(resUnknown))
        {
            Assert.False(docUnknown.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(docUnknown.RootElement.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public void Test07_FanReleaseAll()
    {
        // 7. fan release all clears both override kinds on every fan.
        var fans = new IpcFakeFanControl();
        var proc = CreateProcessor(fans: fans);

        string res = proc.Execute("fan release all");
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        Assert.Equal(fans.FansList.Count, fans.ClearOverrideCalls.Count);
        Assert.Equal(fans.FansList.Count, fans.ClearCurveOverrideCalls.Count);
        foreach (string fanName in fans.FansList)
        {
            Assert.Contains(fanName, fans.ClearOverrideCalls);
            Assert.Contains(fanName, fans.ClearCurveOverrideCalls);
        }
    }

    [Fact]
    public void Test08_RgbSetWithStaticRuleAndTtl()
    {
        // 8. rgb set main #FF0000 calls SetOverride with a static rule and ttl null; with ttl 5 it is 5 s.
        var rgb = new IpcFakeRgbControl();
        var proc = CreateProcessor(rgb: rgb);

        // ttl omitted -> null
        string resNull = proc.Execute("rgb set main #FF0000");
        using (var docNull = JsonDocument.Parse(resNull))
        {
            Assert.True(docNull.RootElement.GetProperty("ok").GetBoolean());
        }
        var call1 = Assert.Single(rgb.SetOverrideCalls);
        Assert.Equal("main", call1.Group);
        Assert.Equal("static", call1.Rule.Type);
        Assert.Equal("#FF0000", call1.Rule.Color);
        Assert.Null(call1.Ttl);

        // ttl 5 -> 5 s
        rgb.SetOverrideCalls.Clear();
        string res5 = proc.Execute("rgb set main #FF0000 5");
        using (var doc5 = JsonDocument.Parse(res5))
        {
            Assert.True(doc5.RootElement.GetProperty("ok").GetBoolean());
        }
        var call2 = Assert.Single(rgb.SetOverrideCalls);
        Assert.Equal("main", call2.Group);
        Assert.Equal("static", call2.Rule.Type);
        Assert.Equal("#FF0000", call2.Rule.Color);
        Assert.Equal(TimeSpan.FromSeconds(5), call2.Ttl);
    }

    [Fact]
    public void Test09_RgbModeBrightnessReleaseAndBadColor()
    {
        // 9. rgb mode all rainbow, rgb brightness 40, rgb release main work; a bad color is rejected.
        var rgb = new IpcFakeRgbControl();
        var proc = CreateProcessor(rgb: rgb);

        // rgb mode all rainbow
        string resMode = proc.Execute("rgb mode all rainbow");
        using (var docMode = JsonDocument.Parse(resMode))
        {
            Assert.True(docMode.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Equal(rgb.GroupsDict.Count, rgb.SetOverrideCalls.Count);
        Assert.All(rgb.SetOverrideCalls, c => Assert.Equal("rainbow", c.Rule.Type));

        // rgb brightness 40
        string resBright = proc.Execute("rgb brightness 40");
        using (var docBright = JsonDocument.Parse(resBright))
        {
            Assert.True(docBright.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Equal(40f, rgb.Brightness);

        // rgb release main
        string resRel = proc.Execute("rgb release main");
        using (var docRel = JsonDocument.Parse(resRel))
        {
            Assert.True(docRel.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Contains("main", rgb.ClearOverrideCalls);

        // bad color is rejected
        string resBadColor = proc.Execute("rgb set main #GG0000");
        using (var docBadColor = JsonDocument.Parse(resBadColor))
        {
            Assert.False(docBadColor.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(docBadColor.RootElement.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public void Test10_GetStateShapeAndRounding()
    {
        // 10. get state JSON has fans.front-fans.rpm and rgb.devices; numbers rounded as specified.
        var clock = new IpcFakeClock { UtcNow = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc) };
        var fans = new IpcFakeFanControl();
        var rgb = new IpcFakeRgbControl();

        fans.StatesDict["front-fans"] = new FanState(
            CurrentPct: 32.44f,
            TargetPct: 32.46f,
            CurveTemp: 50.04f,
            SourceMissing: false,
            Override: new OverrideInfo(40.16f, clock.UtcNow.AddSeconds(52.2)),
            Rpm: 1121.4f,
            Curve: "graph");

        var proc = CreateProcessor(fans: fans, rgb: rgb, clock: clock);
        string res = proc.Execute("get state");
        using var doc = JsonDocument.Parse(res);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());

        var data = root.GetProperty("data");
        var fansObj = data.GetProperty("fans");
        var front = fansObj.GetProperty("front-fans");

        Assert.Equal(32.4, front.GetProperty("pct").GetDouble());
        Assert.Equal(32.5, front.GetProperty("target").GetDouble());
        Assert.Equal(50.0, front.GetProperty("temp").GetDouble());
        Assert.Equal(1121, front.GetProperty("rpm").GetInt32());
        Assert.False(front.GetProperty("missing").GetBoolean());
        Assert.Equal("graph", front.GetProperty("curve").GetString());

        var ovr = front.GetProperty("override");
        Assert.Equal(40.2, ovr.GetProperty("pct").GetDouble());
        Assert.Equal(52, ovr.GetProperty("expiresInS").GetInt32());

        var rgbObj = data.GetProperty("rgb");
        Assert.Equal(100.0, rgbObj.GetProperty("brightness").GetDouble());
        var devices = rgbObj.GetProperty("devices");
        Assert.Equal("ok", devices.GetProperty("it5711").GetString());
        Assert.Equal("faulted: timeout", devices.GetProperty("ene-0x73").GetString());
    }

    [Fact]
    public void Test11_GarbageLineNeverThrows()
    {
        // 11. A garbage line gives ok:false and never throws.
        var proc = CreateProcessor();

        string[] garbageInputs =
        [
            "some completely random text 123",
            "",
            "   \t  ",
            "fan",
            "fan set",
            "rgb",
            "rgb set",
            "unknown_verb target 123",
            "fan set rear-fan not_a_number",
            "rgb brightness -5",
            "rgb brightness 150"
        ];

        foreach (string input in garbageInputs)
        {
            string res = proc.Execute(input);
            using var doc = JsonDocument.Parse(res);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public void Test12_SubscribeAck()
    {
        var proc = CreateProcessor();
        string res = proc.Execute("subscribe");
        Assert.Equal("{\"ok\":true,\"data\":{\"subscribed\":true}}", res);
    }

    [Fact]
    public void Test20_FanZeroRpmToggle()
    {
        // 20. fan zerorpm: on/off/default per fan, "all" only touches fans with a zeroRpm config,
        //     fans without one and bad modes are rejected.
        var fans = new IpcFakeFanControl();
        fans.FansList.Add("gpu-amd");
        fans.FansList.Add("gpu-nvidia");
        fans.ZeroRpmFans.Add("gpu-amd");
        fans.ZeroRpmFans.Add("gpu-nvidia");
        var proc = CreateProcessor(fans);

        Assert.Contains("\"ok\":true", proc.Execute("fan zerorpm gpu-amd off"));
        Assert.Contains("\"ok\":true", proc.Execute("fan zerorpm GPU-NVIDIA on"));
        Assert.Contains("\"ok\":true", proc.Execute("fan zerorpm gpu-amd default"));
        Assert.Equal([("gpu-amd", (bool?)false), ("gpu-nvidia", true), ("gpu-amd", null)], fans.SetZeroRpmCalls);

        fans.SetZeroRpmCalls.Clear();
        Assert.Contains("\"ok\":true", proc.Execute("fan zerorpm all off"));
        Assert.Equal([("gpu-amd", (bool?)false), ("gpu-nvidia", false)], fans.SetZeroRpmCalls);

        Assert.Contains("no zeroRpm config", proc.Execute("fan zerorpm cpu-fans on"));
        Assert.Contains("\"ok\":false", proc.Execute("fan zerorpm gpu-amd maybe"));
        Assert.Contains("\"ok\":false", proc.Execute("fan zerorpm nope on"));
        Assert.Equal(2, fans.SetZeroRpmCalls.Count); // rejected commands made no calls
    }

    [Fact]
    public void Test21_PersistedSettings_SavedByCommands_ReloadedAndApplied()
    {
        // 21. zerorpm and brightness commands save to disk; a new instance reads them back and Apply pushes them
        //     into the engines (skipping fans without zeroRpm). "default" forgets the fan. save:false never writes.
        string path = Path.Combine(AppContext.BaseDirectory, $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var fans = new IpcFakeFanControl();
            fans.FansList.Add("gpu-amd");
            fans.FansList.Add("gpu-nvidia");
            fans.ZeroRpmFans.Add("gpu-amd");
            fans.ZeroRpmFans.Add("gpu-nvidia");
            var rgb = new IpcFakeRgbControl();
            var saved = new PersistedSettings(path, _ => { });
            var proc = new CommandProcessor(fans, rgb, () => new IpcConfig(), new IpcFakeClock(), saved);

            proc.Execute("fan zerorpm gpu-amd on");
            proc.Execute("fan zerorpm gpu-nvidia off");
            proc.Execute("fan zerorpm gpu-nvidia default");
            proc.Execute("rgb brightness 40");

            var reloaded = new PersistedSettings(path, _ => { });
            Assert.Equal(new Dictionary<string, bool> { ["gpu-amd"] = true }, reloaded.ZeroRpm);
            Assert.Equal(40f, reloaded.RgbBrightness);

            var fresh = new IpcFakeFanControl();
            fresh.ZeroRpmFans.Add("gpu-amd");
            var freshRgb = new IpcFakeRgbControl();
            reloaded.Apply(fresh, freshRgb);
            Assert.Equal([("gpu-amd", (bool?)true)], fresh.SetZeroRpmCalls);
            Assert.Equal(40f, freshRgb.Brightness);

            var readOnly = new PersistedSettings(path, _ => { }, save: false);
            readOnly.SetRgbBrightness(90);
            Assert.Equal(40f, new PersistedSettings(path, _ => { }).RgbBrightness);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
