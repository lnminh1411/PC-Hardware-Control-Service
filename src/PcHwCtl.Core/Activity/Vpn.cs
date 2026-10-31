namespace PcHwCtl.Core.Activity;

/// <summary>
/// VPN check: which adapter carries traffic to the internet, and whether it is a tunnel. Cloudflare WARP is its own
/// kind: it tunnels through Cloudflare but does not hide where the PC is. The Cloudflare trace (once per change of the
/// check, never on a timer) says where traffic exits: ip, country, and Cloudflare's own warp=on|plus|off.
/// </summary>
public sealed class VpnConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Ask https://www.cloudflare.com/cdn-cgi/trace after the check changes; false = no internet requests.</summary>
    public bool Trace { get; set; } = true;

    /// <summary>Wait this long after the last adapter event before checking, so a VPN still connecting is not caught halfway.</summary>
    public int SettleS { get; set; } = 5;
}

/// <param name="Kind">none, warp or vpn: the adapter carrying the internet route (the outer tunnel).</param>
/// <param name="By">Adapter description carrying the internet route, "-" when unknown.</param>
/// <param name="Stack">Every tunnel with internet routes, route holder first: "warp+vpn" = WARP's tunnel runs through
/// the VPN, "vpn+warp" = the VPN's runs through WARP. "none" when the route holder is no tunnel.</param>
public sealed record VpnCheck(string Kind, string By, string Stack)
{
    public VpnCheck(string kind, string by) : this(kind, by, kind) { }
}

/// <param name="Warp">Cloudflare's warp field: on, plus, off.</param>
public sealed record ExitInfo(string Ip, string Loc, string Warp);

public sealed record VpnStatus(VpnCheck Check, ExitInfo? Exit, DateTime CheckedUtc, DateTime? TracedUtc)
{
    public static readonly VpnStatus Unknown = new(new VpnCheck("none", "-"), null, DateTime.MinValue, null);
}

public interface IVpnStatus
{
    VpnStatus Status { get; }
}

public static class VpnClassifier
{
    // Adapter descriptions and names of tunnel drivers and VPN clients. WARP is matched first.
    private static readonly string[] VpnHints =
    [
        "WireGuard", "Wintun", "TAP-Windows", "TAP-NordVPN", "OpenVPN", "NordLynx", "NordVPN", "ProtonVPN", "Mullvad",
        "ExpressVPN", "Surfshark", "PANGP", "GlobalProtect", "AnyConnect", "Fortinet", "FortiClient", "SonicWall",
        "Tailscale", "ZeroTier", "Windscribe", "Private Internet Access", "hide.me", "CyberGhost", "IPVanish", "WAN Miniport",
        "SoftEther", "VPN"   // SoftEther's adapter is "VPN Client Adapter - <name>"; any adapter calling itself VPN counts
    ];

    /// <summary>Kind of the adapter that carries the internet route.</summary>
    /// <param name="ppp">The adapter is a PPP link (Windows' built-in VPN client).</param>
    public static string Kind(string? description, string? name, bool ppp)
    {
        string text = $"{description} {name}";
        if (text.Contains("Cloudflare WARP", StringComparison.OrdinalIgnoreCase)) return "warp";
        if (ppp || VpnHints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase))) return "vpn";
        return "none";
    }

    /// <summary>Addresses a route covers: 2^(32 - prefix length). 0.0.0.0/0 = 2^32.</summary>
    public static ulong RouteSize(byte[] mask)
    {
        int bits = 0;
        foreach (byte b in mask) bits += System.Numerics.BitOperations.PopCount(b);
        return 1UL << (32 - Math.Clamp(bits, 0, 32));
    }

    /// <summary>
    /// An interface carries internet traffic when its routes cover at least half the IPv4 space: 0.0.0.0/0, the
    /// 0/1 + 128/1 halves VPN clients add, or WARP's ~117 routes that cover everything but private ranges.
    /// Tailscale without an exit node (100.64.0.0/10) stays far below.
    /// </summary>
    public static bool IsInternetCoverage(ulong addresses) => addresses >= 1UL << 31;

    /// <summary>Route holder's kind first, then the other tunnels with internet routes, each kind once.</summary>
    public static string Stack(string holderKind, IEnumerable<string> otherTunnelKinds)
    {
        if (holderKind == "none") return "none";
        var kinds = new List<string> { holderKind };
        foreach (string k in otherTunnelKinds)
            if (k != "none") kinds.Add(k);
        return string.Join('+', kinds.Distinct());
    }

    /// <summary>Parses the trace's key=value lines; null when ip or loc is missing.</summary>
    public static ExitInfo? ParseTrace(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (!kv.TryGetValue("ip", out var ip) || !kv.TryGetValue("loc", out var loc) || ip == "" || loc == "") return null;
        return new ExitInfo(ip, loc, kv.TryGetValue("warp", out var w) && w != "" ? w : "-");
    }

    public static void AddTo(VpnStatus s, List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts)
    {
        texts.Add(new("vpn", s.Check.Kind));
        texts.Add(new("vpn_by", PowerReasons.Clean(s.Check.By, 60)));
        texts.Add(new("vpn_stack", s.Check.Stack));
        texts.Add(new("exit_ip", s.Exit?.Ip ?? "-"));
        texts.Add(new("exit_loc", s.Exit?.Loc ?? "-"));
        texts.Add(new("exit_warp", s.Exit?.Warp ?? "-"));
    }
}
