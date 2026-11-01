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
    public void Test02_MaxPersonConf()
    {
        var output = new float[PresenceLogic.Rows * PresenceLogic.Anchors];
        output[3 * PresenceLogic.Anchors + 7] = 0.99f;   // a box value, not a score
        output[5 * PresenceLogic.Anchors + 7] = 0.98f;   // class 1 (bicycle)
        output[4 * PresenceLogic.Anchors + 8399] = 0.42f;
        Assert.Equal(0.42, PresenceLogic.MaxPersonConf(output), 3);

        var half = output.Select(v => (Half)v).ToArray();
        Assert.Equal(0.42, PresenceLogic.MaxPersonConf(half), 2);
        Assert.Throws<ArgumentException>(() => PresenceLogic.MaxPersonConf(new float[10]));
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
        Assert.Equal(Hook + "/messages/77", ScreenshotLogic.MessageUrl(Hook + "?thread_id=5", "77"));
    }

    [Fact]
    public void Test05_Ipc()
    {
        var presence = new FakePresence(new PresenceResult("yes", 0.91234, 96.27, "-", "NPU", 1430));
        var shooter = new FakeShooter();
        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(),
            presence: presence, screenshot: shooter);

        string json = proc.Execute("presence");
        Assert.Contains("\"present\":\"yes\"", json);
        Assert.Contains("\"conf\":0.912", json);
        Assert.Contains("\"device\":\"NPU\"", json);
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
    }
}
