using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakeActivity : IProcessActivity, IPowerRequests
{
    public IReadOnlyList<ProcessLoad> Loads { get; set; } = [new("blender.exe", 7.5, 3), new("chrome.exe", 0.42, 30)];
    public IReadOnlyList<ProcessLoad> Top(int n) => Loads.Take(n).ToList();
    public PowerRequestState State { get; set; } = new(PowerRequestParser.Parse(ActivityTests.Powercfg));
}

public sealed class FakePlans : IPowerPlans
{
    private readonly List<PowerPlan> _plans =
    [
        new(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "High performance", true),
        new(Guid.Parse("3ceb8c3b-ac62-40f4-96ea-7268c04b5a42"), "PcHwCtl Night", false)
    ];

    public IReadOnlyList<PowerPlan> List() => _plans;
    public PowerPlan? Active => _plans.FirstOrDefault(p => p.Active);

    public PowerPlan Set(string nameOrGuid)
    {
        var plan = PowerPlanMatch.Find(_plans, nameOrGuid) ?? throw new ArgumentException($"No power plan '{nameOrGuid}'.");
        for (int i = 0; i < _plans.Count; i++) _plans[i] = _plans[i] with { Active = _plans[i].Guid == plan.Guid };
        return plan with { Active = true };
    }
}

public class ActivityTests
{
    public const string Powercfg =
        "DISPLAY:\r\n" +
        "[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe\r\n" +
        "Video Wake Lock\r\n\r\n" +
        "SYSTEM:\r\n" +
        "[DRIVER] Realtek High Definition Audio (HDAUDIO\\FUNC_01&VEN_10EC&DEV_0897\\4&1)\r\n" +
        "An audio stream is currently in use.\r\n" +
        "[SERVICE] \\Device\\HarddiskVolume3\\Windows\\System32\\svchost.exe (wuauserv)\r\n" +
        "Windows Update\r\n\r\n" +
        "AWAYMODE:\r\nNone.\r\n\r\n" +
        "EXECUTION:\r\nNone.\r\n\r\n" +
        "PERFBOOST:\r\nNone.\r\n";

    [Fact]
    public void Test01_PowerRequests_Parse()
    {
        var s = new PowerRequestState(PowerRequestParser.Parse(Powercfg));
        Assert.Equal(1, s.Count("DISPLAY"));
        Assert.Equal("brave.exe", s.First("DISPLAY"));
        Assert.Equal(2, s.Count("SYSTEM"));
        Assert.StartsWith("Realtek High Definition Audio (HDAUDIO", s.First("SYSTEM")); // drivers keep their text
        Assert.Equal("svchost.exe (wuauserv)", s.Sections!["SYSTEM"][1]);
        Assert.Equal(0, s.Count("EXECUTION"));
        Assert.Equal("", s.First("EXECUTION"));
        Assert.Equal(-1, PowerRequestState.Unknown.Count("SYSTEM"));
    }

    [Fact]
    public void Test02_ProcessCpu_PerExeCores()
    {
        // 100 ns units: 1e7 = one second of CPU.
        ProcessSample[] before =
        [
            new(0, 0, "idle", 0),
            new(10, 1, "blender.exe", 0),
            new(11, 1, "blender.exe", 0),
            new(20, 1, "game.exe", 50_000_000),
            new(30, 1, "old.exe", 0)
        ];
        ProcessSample[] now =
        [
            new(0, 0, "idle", 900_000_000),          // never reported
            new(10, 1, "blender.exe", 40_000_000),   // 2 s CPU in 2 s: 2 cores
            new(11, 1, "blender.exe", 20_000_000),   // + 1 core
            new(20, 1, "game.exe", 70_000_000),      // 1 core
            new(30, 2, "new.exe", 6_000_000),        // pid 30 reused: started in between, all its 0.6 s count
            new(40, 5, "Late.EXE", 2_000_000)
        ];
        var loads = ProcessCpu.Loads(before, now, 2.0);
        Assert.Equal("blender.exe", loads[0].Exe);
        Assert.Equal(3.0, loads[0].Cores, 3);
        Assert.Equal(2, loads[0].Instances);
        Assert.Equal(1.0, loads.Single(l => l.Exe == "game.exe").Cores, 3);
        Assert.Equal(0.3, loads.Single(l => l.Exe == "new.exe").Cores, 3);
        Assert.DoesNotContain(loads, l => l.Exe == "idle");
        Assert.Empty(ProcessCpu.Loads(before, now, 0));
    }

    [Fact]
    public void Test03_Ipc_ProcTopAndPowerState()
    {
        var act = new FakeActivity();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(),
            null, null, null, null, act, act);

        string top = proc.Execute("proc top 1");
        Assert.Contains("\"exe\":\"blender.exe\",\"cores\":7.5,\"instances\":3", top);
        Assert.DoesNotContain("chrome", top);
        Assert.Contains("chrome.exe", proc.Execute("PROC TOP"));
        Assert.Contains("Usage: proc top", proc.Execute("proc top 99"));

        string state = proc.StateJson();
        Assert.Contains("\"power\":{\"plan\":null,\"display\":1,\"displayBy\":\"brave.exe\",\"system\":2", state);
        Assert.Contains("\"execution\":0,\"executionBy\":\"\"", state);
    }

    [Fact]
    public void Test04_Snapshot_TextKeys()
    {
        var s = new SensorSnapshot(1, 2, [new("proc_top", 7.5)], [new("proc_top_by", "blender.exe"), new("sys_req_by", "a\nb")]);
        Assert.Equal("seq=1\nts=2\nproc_top=7.5\nproc_top_by=blender.exe\nsys_req_by=a b\n\n", s.ToText());

        var hub = new SensorFeedHub();
        hub.Publish(s);
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(), null, null, null, hub);
        Assert.Contains("\"proc_top_by\":\"blender.exe\"", proc.Execute("get sensors"));
    }

    [Fact]
    public void Test05_Ipc_PowerPlan()
    {
        var plans = new FakePlans();
        var act = new FakeActivity();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(),
            null, null, null, null, act, act, plans, () => new PowerConfig());

        Assert.Contains("\"name\":\"PcHwCtl Night\"", proc.Execute("power plan"));
        Assert.Contains("\"plan\":\"PcHwCtl Night\"", proc.Execute("power plan night"));
        Assert.Contains("\"plan\":\"PcHwCtl Night\",\"display\"", proc.StateJson());
        Assert.Contains("\"plan\":\"High performance\"", proc.Execute("POWER PLAN DAY"));
        Assert.Contains("\"plan\":\"PcHwCtl Night\"", proc.Execute("power plan 3ceb8c3b-ac62-40f4-96ea-7268c04b5a42"));
        Assert.Contains("\"plan\":\"High performance\"", proc.Execute("power plan high performance"));
        Assert.Contains("No power plan", proc.Execute("power plan Ultimate"));
        Assert.Contains("Usage: power plan", proc.Execute("power"));

        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("Power plan control is off", off.Execute("power plan night"));
    }
}
