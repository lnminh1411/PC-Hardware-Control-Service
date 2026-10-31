using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Ipc;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;
using Xunit;

namespace PcHwCtl.Core.Tests;

public sealed class FakeVpn(VpnStatus status) : IVpnStatus
{
    public VpnStatus Status => status;
}

public class VpnTests
{
    [Fact]
    public void Test01_Kind()
    {
        Assert.Equal("warp", VpnClassifier.Kind("Cloudflare WARP Interface Tunnel", "CloudflareWARP", false));
        Assert.Equal("vpn", VpnClassifier.Kind("WireGuard Tunnel", "wg0", false));
        Assert.Equal("vpn", VpnClassifier.Kind("NordLynx Tunnel", "NordLynx", false));
        Assert.Equal("vpn", VpnClassifier.Kind("TAP-Windows Adapter V9", "OpenVPN TAP", false));
        Assert.Equal("vpn", VpnClassifier.Kind("Office", "Office", true));
        Assert.Equal("vpn", VpnClassifier.Kind("VPN Client Adapter - VPN", "VPN - VPN Client", false));   // SoftEther   // Windows' own VPN client: a PPP link
        Assert.Equal("none", VpnClassifier.Kind("Realtek Gaming 2.5GbE Family Controller", "Ethernet", false));
        Assert.Equal("none", VpnClassifier.Kind(null, null, false));
    }

    [Fact]
    public void Test02_ParseTrace()
    {
        const string trace = "fl=123\nh=1.1.1.1\nip=104.28.1.2\nts=1761900000.1\nvisit_scheme=https\nloc=VN\ncolo=SGN\nwarp=on\ngateway=off\n";
        Assert.Equal(new ExitInfo("104.28.1.2", "VN", "on"), VpnClassifier.ParseTrace(trace));
        Assert.Equal(new ExitInfo("1.2.3.4", "DE", "-"), VpnClassifier.ParseTrace("ip=1.2.3.4\r\nloc=DE\r\n"));
        Assert.Null(VpnClassifier.ParseTrace("ip=1.2.3.4\n"));
        Assert.Null(VpnClassifier.ParseTrace(""));
        Assert.Null(VpnClassifier.ParseTrace(null));
    }

    [Fact]
    public void Test03_KeysAndIpc()
    {
        var at = new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);
        var status = new VpnStatus(new VpnCheck("warp", "Cloudflare WARP Interface Tunnel"), new ExitInfo("104.28.1.2", "VN", "on"), at, at);
        var values = new List<KeyValuePair<string, double?>>();
        var texts = new List<KeyValuePair<string, string>>();
        VpnClassifier.AddTo(status, values, texts);
        var t = texts.ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal("warp", t["vpn"]);
        Assert.Equal("Cloudflare WARP Interface Tunnel", t["vpn_by"]);
        Assert.Equal("VN", t["exit_loc"]);
        Assert.Equal("on", t["exit_warp"]);

        texts.Clear();
        VpnClassifier.AddTo(VpnStatus.Unknown, values, texts);
        Assert.Equal("-", texts.Single(k => k.Key == "exit_ip").Value);

        var proc = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock(),
            null, null, null, null, null, null, null, null, null, null, new FakeVpn(status));
        string json = proc.Execute("vpn");
        Assert.Contains("\"kind\":\"warp\"", json);
        Assert.Contains("\"loc\":\"VN\"", json);
        Assert.Contains("Usage: vpn", proc.Execute("vpn now"));
        var off = new CommandProcessor(new IpcFakeFanControl(), new IpcFakeRgbControl(), () => new IpcConfig(), new IpcFakeClock());
        Assert.Contains("VPN check is off", off.Execute("vpn"));
    }

    [Fact]
    public void Test04_SettleValidated()
    {
        var cfg = new Config { Vpn = new VpnConfig { SettleS = 0 } };
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(cfg));
        Assert.Contains("settleS", ex.Message);
    }
}
