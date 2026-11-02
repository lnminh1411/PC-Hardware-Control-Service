using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakePresence(PresenceResult result) : IPresence
{
    public int Checks;

    public PresenceResult Check()
    {
        Checks++;
        return result;
    }
}

public sealed class FakeShooter : IScreenshot
{
    public (string Nonce, string Webhook)? Last;

    public ShotResult Take(string nonce, string webhook)
    {
        Last = (nonce, webhook);
        return ScreenshotLogic.IsWebhook(webhook) ? ShotResult.Ok : ShotResult.Declined("webhook");
    }
}

public class PresenceTests
{
    private const string Hook = "https://discord.com/api/webhooks/123/abc";

    [Fact]
    public void Test01_Decide()
    {
        var cfg = new PresenceConfig();
        Assert.Equal(("yes", "-"), PresenceLogic.Decide(0.9, 120, cfg));
        Assert.Equal(("yes", "-"), PresenceLogic.Decide(0.55, 1.6, cfg));     // YOLO26x's lowest seated score, near-black room
        Assert.Equal(("no", "-"), PresenceLogic.Decide(0.39, 80, cfg));      // below the 0.4 margin
        Assert.Equal(("no", "-"), PresenceLogic.Decide(0.001, 80, cfg));
        Assert.Equal(("no", "-"), PresenceLogic.Decide(0.001, 0.9, cfg));    // near-black room: YOLO26x still sees
        Assert.Equal(("unknown", "dark"), PresenceLogic.Decide(0.01, 0.3, cfg));   // black frame: can't tell
    }

    [Fact]
    public void Test02_BestPerson()
    {
        const int A = PresenceLogic.Anchors;
        var output = new float[PresenceLogic.Rows * A];
        output[3 * A + 7] = 0.99f;   // a box value, not a score
        output[5 * A + 7] = 0.98f;   // class 1 (bicycle)
        output[4 * A + 8399] = 0.42f;
        output[8399] = 320; output[A + 8399] = 240; output[2 * A + 8399] = 100; output[3 * A + 8399] = 200;
        var p = PresenceLogic.BestPerson(output);
        Assert.Equal(0.42, p.Conf, 3);
        Assert.Equal((320.0, 240.0, 100.0, 200.0), (p.Cx, p.Cy, p.W, p.H));
        Assert.Throws<ArgumentException>(() => PresenceLogic.BestPerson(new float[10]));
    }

    [Fact]
    public void Test12_DepthZoneAndHead()
    {
        var cfg = new PresenceConfig();
        // npu-bench readings (C270 under the monitor): seated 0.55 m, behind a chair 1.1 m, 1.5 m, 1.6 m, 1.75 m
        Assert.Equal("desk", PresenceLogic.Zone(0.455, 100, cfg));
        Assert.Equal("near", PresenceLogic.Zone(0.585, 100, cfg));
        Assert.Equal("near", PresenceLogic.Zone(0.747, 100, cfg));
        Assert.Equal("near", PresenceLogic.Zone(0.797, 100, cfg));
        Assert.Equal("room", PresenceLogic.Zone(0.847, 100, cfg));
        Assert.Equal("-", PresenceLogic.Zone(1.005, 6.4, cfg));   // too dark: that run scattered 0.81-2.69

        // 640 x 480 frame: letterboxed with 80 rows of padding top and bottom; the depth map stretches it to 768 x 768.
        // A box over the whole frame puts the head region at x 37.5-62.5%, y 5-25% of the map.
        var whole = new PersonBox(0.9, 320, 320, 640, 480);
        Assert.Equal((288, 38, 481, 193), PresenceLogic.HeadRegion(whole, 640, 480));
        var off = new PersonBox(0.9, -500, -500, 10, 10);   // outside the frame: clamped, never empty
        var r = PresenceLogic.HeadRegion(off, 640, 480);
        Assert.True(r.X1 > r.X0 && r.Y1 > r.Y0);

        var depth = new float[PresenceLogic.DepthSize * PresenceLogic.DepthSize];
        depth.AsSpan().Fill(2f);
        for (int y = 38; y < 193; y++) depth.AsSpan(y * 768 + 288, 481 - 288).Fill(0.5f);
        Assert.Equal(0.5, PresenceLogic.RegionMedian(depth, (288, 38, 481, 193)), 3);
        Assert.Equal(2.5, PresenceLogic.Median(new float[] { 4, 1, 2, 3 }), 3);
        Assert.Throws<ArgumentException>(() => PresenceLogic.RegionMedian(new float[10], (0, 0, 1, 1)));

        var stretched = new byte[768 * 3 * 768];
        stretched[0] = 255;   // pixel 0 pure blue
        var input = new float[3 * 768 * 768];
        PresenceLogic.FillDepthInput(stretched, 768 * 3, input);
        Assert.Equal((0f, 0f, 1f), (input[0], input[768 * 768], input[2 * 768 * 768]));
        Assert.Equal(0f, input[1]);   // no grey padding
    }

    [Fact]
    public void Test03_FillInputLetterboxes()
    {
        Assert.Equal(1.0, PresenceLogic.Scale(640, 480));
        Assert.Equal(0.5, PresenceLogic.Scale(1280, 720));

        // 2 x 1 image, stride 8 (padded rows): pixel 0 pure blue, pixel 1 pure red
        byte[] bgr = [255, 0, 0, 0, 0, 255, 9, 9];
        var input = new float[3 * 640 * 640];
        PresenceLogic.FillInput(bgr, 2, 1, 8, input);
        int plane = 640 * 640, o = 319 * 640 + 319;
        Assert.Equal(114f / 255f, input[0]);                  // padding
        Assert.Equal(0f, input[o]);                           // R of blue
        Assert.Equal(1f, input[2 * plane + o]);               // B of blue
        Assert.Equal(1f, input[o + 1]);                       // R of red
        Assert.Equal(0f, input[plane + o + 1]);               // G of red
        Assert.Throws<ArgumentException>(() => PresenceLogic.FillInput(bgr, 700, 1, 2100, input));
    }

    [Fact]
    public void Test04_Webhook()
    {
        Assert.True(ScreenshotLogic.IsWebhook(Hook));
        Assert.True(ScreenshotLogic.IsWebhook("https://discordapp.com/api/webhooks/1/x?thread_id=5"));
        Assert.False(ScreenshotLogic.IsWebhook("http://discord.com/api/webhooks/1/x"));
        Assert.False(ScreenshotLogic.IsWebhook("https://evil.example/api/webhooks/1/x"));
        Assert.False(ScreenshotLogic.IsWebhook("https://discord.com.evil.example/api/webhooks/1/x"));
        Assert.False(ScreenshotLogic.IsWebhook("https://discord.com/channels/1"));
        Assert.False(ScreenshotLogic.IsWebhook(null));
        Assert.Equal(Hook + "?wait=true", ScreenshotLogic.PostUrl(Hook));
        Assert.Equal("https://discord.com/api/webhooks/1/x?thread_id=5&wait=true", ScreenshotLogic.PostUrl("https://discord.com/api/webhooks/1/x?thread_id=5"));
        Assert.Equal(Hook + "/messages/77", ScreenshotLogic.MessageUrl(Hook, "77"));
        Assert.Equal(Hook + "/messages/77?thread_id=5", ScreenshotLogic.MessageUrl(Hook + "?thread_id=5", "77"));   // a thread post needs it
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/x#frag")]      // the fragment would swallow ?wait=true
    [InlineData("https://discord.com/api/webhooks/1/x#")]
    [InlineData("https://discord.com/api/webhooks/1/x?wait=false")]
    [InlineData("https://discord.com/api/webhooks/1/x?thread_id=5&wait=false")]
    [InlineData("https://user:pw@discord.com/api/webhooks/1/x")]
    [InlineData("https://discord.com:8443/api/webhooks/1/x")]
    [InlineData("https://discord.com/api/webhooks/1/x/slack")]
    [InlineData("https://discord.com/api/webhooks/..%2f..%2fusers")]
    [InlineData("https://discord.com/api/webhooks/x/y")]
    [InlineData("https://discord.com/api/webhooks/1")]
    public void Test07_WebhookIsPlain(string url) => Assert.False(ScreenshotLogic.IsWebhook(url));

    [Fact]
    public void Test08_OnlyListedWebhooks()
    {
        var cfg = new ScreenshotConfig { WebhookIds = ["123"] };
        Assert.True(ScreenshotLogic.IsAllowed(Hook, cfg));
        Assert.True(ScreenshotLogic.IsAllowed(Hook + "?thread_id=5", cfg));
        Assert.False(ScreenshotLogic.IsAllowed("https://discord.com/api/webhooks/999/abc", cfg));   // someone else's
        Assert.False(ScreenshotLogic.IsAllowed(Hook, new ScreenshotConfig()));
        Assert.False(ScreenshotLogic.IsAllowed(null, cfg));
    }

    [Fact]
    public void Test09_OffByDefault()
    {
        var config = new Config();
        Assert.False(config.Presence.Enabled);
        Assert.False(config.Screenshot.Enabled);
        Assert.Empty(config.Screenshot.WebhookIds);
        Assert.Equal("", config.Ipc.AllowAccount);
    }

    [Fact]
    public void Test10_RetryDelete()
    {
        Assert.True(ScreenshotLogic.RetryDelete(null));   // no answer
        Assert.True(ScreenshotLogic.RetryDelete(408));
        Assert.True(ScreenshotLogic.RetryDelete(429));
        Assert.True(ScreenshotLogic.RetryDelete(503));
        Assert.False(ScreenshotLogic.RetryDelete(401));
        Assert.False(ScreenshotLogic.RetryDelete(403));
    }

    [Fact]
    public void Test11_PendingDelete()
    {
        var due = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        var job = PendingDelete.New(due, "dpapi:AAAA", "77");
        Assert.True(PendingDelete.TryParse(job.Format(), out var back));
        Assert.Equal(job, back);

        // A shorter delay first, then doubling, and never past the give-up time.
        long now = due.Ticks;
        var first = job.Retry(now);
        Assert.Equal(now + TimeSpan.TicksPerMinute, first.DueTicks);
        long hour = now + TimeSpan.TicksPerHour;
        Assert.Equal(hour + TimeSpan.TicksPerHour, job.Retry(hour).DueTicks);
        Assert.Equal(job.GiveUpTicks, job.Retry(job.GiveUpTicks - TimeSpan.TicksPerMinute).DueTicks);
        Assert.False(job.Expired(job.GiveUpTicks - 1));
        Assert.True(job.Expired(job.GiveUpTicks));

        Assert.True(PendingDelete.TryParse($"{due.Ticks}\t{Hook}\t77", out var old));   // the older format
        Assert.Equal((Hook, "77", job.GiveUpTicks), (old.Webhook, old.MessageId, old.GiveUpTicks));

        Assert.False(PendingDelete.TryParse("x\ty\tz", out _));
        Assert.False(PendingDelete.TryParse($"{due.Ticks}\t{Hook}", out _));
        Assert.False(PendingDelete.TryParse($"{due.Ticks}\t{Hook}\t../../x", out _));   // the id goes into a URL
    }

    [Fact]
    public void Test05_Ipc()
    {
        var presence = new FakePresence(new PresenceResult("yes", 0.91234, 96.27, "-", "NPU", 1430, "near", 0.74712));
        var shooter = new FakeShooter();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(),
            presence: presence, screenshot: shooter);

        string json = proc.Execute("presence");
        Assert.Contains("\"present\":\"yes\"", json);
        Assert.Contains("\"conf\":0.912", json);
        Assert.Contains("\"device\":\"NPU\"", json);
        Assert.Contains("\"zone\":\"near\"", json);
        Assert.Contains("\"depth\":0.747", json);
        Assert.Equal(1, presence.Checks);
        Assert.Contains("Usage: presence", proc.Execute("presence now"));

        Assert.Contains("\"accepted\":true", proc.Execute($"screenshot n1 {Hook}"));
        Assert.Equal(("n1", Hook), shooter.Last);
        Assert.Contains("\"why\":\"webhook\"", proc.Execute("screenshot n2 https://evil.example/x"));
        Assert.Contains("Usage: screenshot", proc.Execute("screenshot n3"));

        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("Presence check is off", off.Execute("presence"));
        Assert.Contains("Screenshots are off", off.Execute($"screenshot n1 {Hook}"));
    }

    [Fact]
    public void Test06_Validated()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Presence = new PresenceConfig { MinConf = 0 } }));
        Assert.Contains("minConf", ex.Message);
        ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Screenshot = new ScreenshotConfig { BorderS = 99 } }));
        Assert.Contains("borderS", ex.Message);
        ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Screenshot = new ScreenshotConfig { BorderS = 0 } }));   // no border, no secret screenshots
        Assert.Contains("borderS", ex.Message);
        ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Presence = new PresenceConfig { MinIntervalS = -1 } }));
        Assert.Contains("minIntervalS", ex.Message);
        ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Presence = new PresenceConfig { DeskBelow = 0.9 } }));   // above roomFrom
        Assert.Contains("deskBelow", ex.Message);
        ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Presence = new PresenceConfig { DepthFrames = 0 } }));
        Assert.Contains("depthFrames", ex.Message);
    }

    [Fact]
    public void Test07_EneAddressesStayOffSpd()
    {
        ConfigLoader.Validate(new Config { Devices = new DevicesConfig { Ene = new EneConfig { Addresses = ["0x71", "0x73", "119"] } } });   // 119 = 0x77
        foreach (string bad in new[] { "0x30", "0x37", "0x50", "0x6F", "0x78", "112x", "48" })
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new Config { Devices = new DevicesConfig { Ene = new EneConfig { Addresses = [bad] } } }));
            Assert.Contains("0x70-0x77", ex.Message);
        }
    }
}
