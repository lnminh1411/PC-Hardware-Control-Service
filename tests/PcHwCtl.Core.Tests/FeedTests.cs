using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;
using Xunit;

namespace PcHwCtl.Core.Tests;

public class FeedTests
{
    [Fact]
    public void Test01_Wildcard()
    {
        Assert.True(Wildcard.Match("ITE*", "ITE IT8696E"));
        Assert.True(Wildcard.Match("DIMM #*", "dimm #3"));
        Assert.True(Wildcard.Match("D3D*", "D3D 3D"));
        Assert.True(Wildcard.Match("*", ""));
        Assert.False(Wildcard.Match("GPU Core", "GPU Core Max"));
        Assert.False(Wildcard.Match("Fan #1", "Fan #10"));
    }

    [Fact]
    public void Test02_ParseAndMatch()
    {
        var rule = SensorRule.Parse("ram_temp", " Memory | Temperature | DIMM #* | MAX ");
        Assert.Equal("max", rule.Reduce);
        Assert.True(rule.Matches([("Memory", "G Skill (#1)")], "Temperature", "DIMM #1"));
        Assert.False(rule.Matches([("Memory", "G Skill (#1)")], "Temperature", "Thermal Sensor High Limit"));

        // Sub-hardware: the SuperIO chip under the motherboard matches by its name or its parent's type.
        var fan = SensorRule.Parse("cpu_fan", "ITE* | Fan | Fan #1");
        Assert.True(fan.Matches([("SuperIO", "ITE IT8696E"), ("Motherboard", "Gigabyte B860M")], "Fan", "Fan #1"));
        var viaParent = SensorRule.Parse("x", "Motherboard | Fan | Fan #1");
        Assert.True(viaParent.Matches([("SuperIO", "ITE IT8696E"), ("Motherboard", "Gigabyte B860M")], "Fan", "Fan #1"));

        var net = SensorRule.Parse("net_down", "Wi-Fi,Ethernet | Throughput | Download Speed | sum");
        Assert.Equal(["Wi-Fi", "Ethernet"], net.Hardware);
        Assert.True(net.Matches([("Network", "Ethernet")], "Throughput", "Download Speed"));

        Assert.Throws<FormatException>(() => SensorRule.Parse("a", "Cpu | Temperature"));
        Assert.Throws<FormatException>(() => SensorRule.Parse("a", "Cpu | Temperature | x | median"));
    }

    [Fact]
    public void Test03_Reduce()
    {
        double[] v = [44.25, double.NaN, 43];
        Assert.Equal(44.25, SensorRule.Parse("a", "M | T | N").Apply(v));
        Assert.Equal(44.25, SensorRule.Parse("a", "M | T | N | max").Apply(v));
        Assert.Equal(43, SensorRule.Parse("a", "M | T | N | min").Apply(v));
        Assert.Equal(87.25, SensorRule.Parse("a", "M | T | N | sum").Apply(v));
        Assert.Equal(43.625, SensorRule.Parse("a", "M | T | N | avg").Apply(v));
        Assert.Null(SensorRule.Parse("a", "M | T | N").Apply([]));
    }

    [Fact]
    public void Test04_SnapshotText()
    {
        var s = new SensorSnapshot(7, 1793000000123, [new("cpu_temp", 45.456), new("gpu1_fan", null), new("gpu1_vram", 2048)]);
        Assert.Equal("seq=7\nts=1793000000123\ncpu_temp=45.46\ngpu1_fan=\ngpu1_vram=2048\n\n", s.ToText());
    }

    [Fact]
    public async Task Test05_Hub_LatestThenWaitsForNext()
    {
        var hub = new SensorFeedHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = hub.NextAsync(0, cts.Token);
        Assert.False(first.IsCompleted);
        hub.Publish(new SensorSnapshot(1, 0, []));
        Assert.Equal(1, (await first).Seq);

        hub.Publish(new SensorSnapshot(2, 0, []));
        hub.Publish(new SensorSnapshot(3, 0, []));
        Assert.Equal(3, (await hub.NextAsync(1, cts.Token)).Seq); // a slow reader gets the newest, never a backlog

        var next = hub.NextAsync(3, cts.Token);
        Assert.False(next.IsCompleted);
        hub.Publish(new SensorSnapshot(4, 0, []));
        Assert.Equal(4, (await next).Seq);
    }

    [Fact]
    public void Test06_Ipc_GetAndSubscribeSensors()
    {
        var hub = new SensorFeedHub();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(), null, null, null, hub);

        Assert.Contains("No sensor snapshot yet", proc.Execute("get sensors"));
        hub.Publish(new SensorSnapshot(5, 99, [new("cpu_temp", 45.456), new("gpu1_fan", null)]));
        string get = proc.Execute("get sensors");
        Assert.Contains("\"seq\":5", get);
        Assert.Contains("\"cpu_temp\":45.46", get);
        Assert.Contains("\"gpu1_fan\":null", get);

        Assert.Contains("\"subscribed\":\"sensors\"", proc.Execute("subscribe sensors"));
        Assert.Contains("\"subscribed\":true", proc.Execute("subscribe"));
        Assert.Contains("Usage: subscribe", proc.Execute("subscribe fans"));

        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("Sensor feed is off", off.Execute("subscribe sensors"));
    }

    [Fact]
    public void Test07_ConfigValidation()
    {
        var cfg = new Config();
        ConfigLoader.Validate(cfg); // defaults are valid
        cfg.Feed.IntervalMs = 50;
        cfg.Feed.Keys["CPU-Temp"] = "Cpu | Temperature | Core Average";
        cfg.Feed.Keys["bad"] = "Cpu | Temperature";
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains(ex.Errors, e => e.Contains("intervalMs 50"));
        Assert.Contains(ex.Errors, e => e.Contains("'CPU-Temp'"));
        Assert.Contains(ex.Errors, e => e.Contains("Sensor key 'bad'"));
    }
}
