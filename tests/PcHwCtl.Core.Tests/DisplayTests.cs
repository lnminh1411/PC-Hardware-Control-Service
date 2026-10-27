using PcHwCtl.Core;
using PcHwCtl.Core.Display;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakeDisplay(string name, int left, bool main = false) : IDisplayHandle
{
    public string Name { get; } = name;
    public int Left { get; } = left;
    public bool Main { get; } = main;
    public Dictionary<byte, uint> Vcp { get; } = new() { [0x10] = 50, [0x60] = 0x11, [0xD6] = 1 };
    public Dictionary<byte, uint> Max { get; } = new() { [0x10] = 100, [0x60] = 0xFF, [0xD6] = 5 };
    public List<(int Code, uint Value)> Writes { get; } = [];
    public int DropNextReads { get; set; }
    public bool Dead { get; set; }

    // Monitor answers reads with the old value until told otherwise (as real ones do for seconds).
    public bool Lagging { get; set; }

    public bool TryRead(byte code, out uint current, out uint maximum)
    {
        current = Vcp.GetValueOrDefault(code);
        maximum = Max.GetValueOrDefault(code);
        if (Dead) return false;
        if (DropNextReads > 0)
        {
            DropNextReads--;
            return false;
        }
        return Vcp.ContainsKey(code);
    }

    public bool TryWrite(byte code, uint value)
    {
        if (Dead) return false;
        Writes.Add((code, value));
        if (!Lagging) Vcp[code] = value;
        return true;
    }
}

public sealed class FakeDisplayBus : IDisplayBus
{
    public List<FakeDisplay> Monitors { get; } = [];
    public int DisplayOffPosts { get; private set; }
    public int Nudges { get; private set; }

    public DisplayScan Scan() => new(Monitors.Cast<IDisplayHandle>().ToList());
    public bool PostDisplayOff() { DisplayOffPosts++; return true; }
    public void NudgeMouse() => Nudges++;
}

public class DisplayTests
{
    private static (DisplayController Ctl, FakeDisplayBus Bus, FakeClock Clock, FakeDisplay Msi, FakeDisplay DellA, FakeDisplay DellB) Rig()
    {
        var bus = new FakeDisplayBus();
        // Scan order is not screen order: numbering must follow Left.
        var dellB = new FakeDisplay("DELL E1715S", 1920);
        var msi = new FakeDisplay("MSI PRO MP275", 0, main: true);
        var dellA = new FakeDisplay("DELL E1715S", -1280);
        bus.Monitors.AddRange([dellB, msi, dellA]);
        var cfg = new DisplaysConfig
        {
            Models =
            {
                ["DELL E1715S"] = new DisplayModelConfig { Power = "ddc-power" },
                ["MSI PRO MP275"] = new DisplayModelConfig { Power = "ddc-input", StandbyInput = "0x01", ActiveInput = "0x11" }
            }
        };
        var clock = new FakeClock();
        return (new DisplayController(bus, () => cfg, clock, _ => { }), bus, clock, msi, dellA, dellB);
    }

    [Fact]
    public void Test01_Refresh_NumbersFromLeft_ReadsWithOneRetry()
    {
        var (ctl, _, _, msi, dellA, dellB) = Rig();
        dellA.Vcp[0x10] = 30;
        dellA.Max[0x10] = 60; // 30 of 60 = 50 %
        msi.DropNextReads = 1; // first brightness read lost, retry answers
        dellB.Dead = true;

        ctl.Refresh();

        var d = ctl.Displays;
        Assert.Equal(["DELL E1715S", "MSI PRO MP275", "DELL E1715S"], d.Select(x => x.Name));
        Assert.Equal([1, 2, 3], d.Select(x => x.Id));
        Assert.Equal(50, d[0].Bright);
        Assert.Equal(50, d[1].Bright);
        Assert.True(d[1].Main);
        Assert.Equal(-1, d[2].Bright);
        Assert.Equal(-1, d[2].Power);
        Assert.Equal("ddc-input", d[1].PowerMethod);
    }

    [Fact]
    public void Test02_Brightness_ScalesToMax_HeldWhileMonitorLags()
    {
        var (ctl, _, clock, msi, dellA, _) = Rig();
        dellA.Max[0x10] = 200;
        msi.Lagging = true;

        Assert.True(ctl.Brightness("all", 40).Ok);
        Assert.Contains((0x10, 80u), dellA.Writes);
        Assert.Equal(40, ctl.Displays[1].Bright); // MSI still reads 50, the set value is reported

        clock.AdvanceSeconds(16);
        ctl.Refresh();
        Assert.Equal(50, ctl.Displays[1].Bright); // hold expired: report what the monitor says

        Assert.False(ctl.Brightness("all", 101).Ok);
        Assert.Contains("no monitor 4", ctl.Brightness("4", 10).Error);
    }

    [Fact]
    public void Test03_Power_DellUsesD6_MsiSwitchesToEmptyInputAndBack()
    {
        var (ctl, _, _, msi, dellA, dellB) = Rig();

        Assert.True(ctl.Power("all", false).Ok);
        Assert.Contains((0xD6, 4u), dellA.Writes);
        Assert.Contains((0xD6, 4u), dellB.Writes);
        Assert.Equal([(0x60, 1u)], msi.Writes); // never 0xD6 on the MSI: it would drop out of Windows
        Assert.All(ctl.Displays, d => Assert.Equal(0, d.Power));

        msi.Dead = true; // asleep on "no signal": no DDC answers, the held state is reported
        ctl.Refresh();
        Assert.Equal(0, ctl.Displays[1].Power);
        Assert.Equal(0x01, ctl.Displays[1].Input);
        Assert.Equal(50, ctl.Displays[1].Bright); // last answer
        msi.Dead = false;

        msi.Vcp[0x60] = 0x01;
        Assert.True(ctl.Power("2", true).Ok);
        Assert.Equal((0x60, 0x11u), msi.Writes[^1]);
        Assert.Equal(1, ctl.Displays[1].Power);
        Assert.Equal(0x11, ctl.Displays[1].Input);
    }

    [Fact]
    public void Test04_Power_RestoresInputItWasOn_FallsBackToActiveInput_RejectsUnconfigured()
    {
        var (ctl, bus, _, msi, _, _) = Rig();
        msi.Vcp[0x60] = 0x12; // on HDMI 2 before standby
        Assert.True(ctl.Power("2", false).Ok);
        Assert.True(ctl.Power("2", true).Ok);
        Assert.Equal((0x60, 0x12u), msi.Writes[^1]);

        msi.Vcp[0x60] = 0x01; // already in standby when the service started: input before is unknown
        Assert.True(ctl.Power("2", true).Ok);
        Assert.Equal((0x60, 0x11u), msi.Writes[^1]);

        bus.Monitors.Add(new FakeDisplay("ACME 1", 5000));
        var r = ctl.Power("4", false);
        Assert.False(r.Ok);
        Assert.Contains("no DDC power method", r.Error);
    }

    [Fact]
    public void Test05_SleepWakeAndInput()
    {
        var (ctl, bus, _, msi, _, _) = Rig();
        Assert.True(ctl.Sleep().Ok);
        Assert.True(ctl.Wake().Ok);
        Assert.Equal(1, bus.DisplayOffPosts);
        Assert.Equal(1, bus.Nudges);

        Assert.True(ctl.Input("2", 0x0F).Ok);
        Assert.Equal((0x60, 0x0Fu), msi.Writes[^1]);
        Assert.False(ctl.Input("2", 0).Ok);
    }

    [Fact]
    public void Test06_Commands_ParseAndReplyJson_ThroughCommandProcessor()
    {
        var (ctl, bus, _, msi, _, _) = Rig();
        ctl.Refresh();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(), null, ctl);

        string list = proc.Execute("display list");
        Assert.Contains("\"ok\":true", list);
        Assert.Contains("\"name\":\"MSI PRO MP275\"", list);
        Assert.Contains("\"powerMethod\":\"ddc-input\"", list);

        Assert.Contains("\"ok\":true", proc.Execute("display power 2 off"));
        Assert.Equal((0x60, 1u), msi.Writes[^1]);
        Assert.Contains("\"ok\":true", proc.Execute("display input 2 0x11"));
        Assert.Contains("\"ok\":true", proc.Execute("display bright all 70"));
        Assert.Contains("\"ok\":true", proc.Execute("display sleep"));
        Assert.Contains("\"ok\":true", proc.Execute("DISPLAY WAKE"));
        Assert.Equal(1, bus.Nudges);

        Assert.Contains("power is on or off", proc.Execute("display power 2 maybe"));
        Assert.Contains("\"ok\":false", proc.Execute("display bright 2"));
        Assert.Contains("\"displays\":{", proc.StateJson());

        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("Display control is off", off.Execute("display list"));
    }

    [Fact]
    public void Test07_ConfigValidation()
    {
        var cfg = new Config();
        cfg.Displays.Models["X"] = new DisplayModelConfig { Power = "ddc-input" };
        cfg.Displays.Models["Y"] = new DisplayModelConfig { Power = "ddc-power", PowerOff = 1 };
        cfg.Displays.Models["Z"] = new DisplayModelConfig { Power = "laser" };
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains(ex.Errors, e => e.Contains("'X'") && e.Contains("standbyInput"));
        Assert.Contains(ex.Errors, e => e.Contains("'Y'") && e.Contains("powerOff"));
        Assert.Contains(ex.Errors, e => e.Contains("'Z'") && e.Contains("laser"));
    }
}
