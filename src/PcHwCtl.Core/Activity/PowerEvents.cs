using System.Globalization;

namespace PcHwCtl.Core.Activity;

/// <summary>
/// The PC going down right now (event 1074, Kernel-Power 42/109, or PcHwCtl's own `power do`).
/// Kind: none, shutdown, restart, update_shutdown, update_restart, sleep, hibernate.
/// </summary>
/// <param name="By">Process that asked (file name), "board" for `power do`, or "-".</param>
/// <param name="Why">Windows' reason title, then the comment after ": " when there is one.</param>
/// <param name="Code">Reason code with its decoded major/minor/flags, e.g. "0x80020010 os/service_pack planned".</param>
public sealed record GoingState(string Kind, string By, string Why, string Code, string User, long Id, DateTime AtUtc)
{
    public static readonly GoingState None = new("none", "-", "-", "-", "-", 0, DateTime.MinValue);
}

/// <summary>How the PC last went down before this boot.</summary>
/// <param name="Kind">shutdown, restart, update_shutdown, update_restart, unexpected, bluescreen, power_button or unknown.</param>
public sealed record LastDown(string Kind, DateTime? At, string By, string Why)
{
    public static readonly LastDown Unknown = new("unknown", null, "-", "-");
}

/// <summary>The last return from sleep or hibernate since boot (Power-Troubleshooter event 1).</summary>
/// <param name="From">sleep or hibernate.</param>
/// <param name="By">Windows' wake source, e.g. "Power Button", "Device -USB Input Device".</param>
public sealed record WakeInfo(string From, DateTime At, DateTime SleptAt, string By);

/// <param name="PendingBy">wu (Windows Update wants a restart), cbs (servicing stack), both as "wu+cbs", or "-".</param>
/// <param name="Running">Windows is installing updates with nobody signed in (after an update restart).</param>
public sealed record UpdateState(bool Pending, string PendingBy, bool Running)
{
    public static readonly UpdateState None = new(false, "-", false);
}

/// <summary>The last `power do`. Outcome: accepted, declined (refused before starting) or failed (Windows refused after).</summary>
public sealed record PowerDoResult(string Action, string Outcome, string Why, DateTime AtUtc);

public sealed record PowerStatus(GoingState Going, LastDown LastDown, WakeInfo? Wake, UpdateState Update, PowerDoResult? LastDo)
{
    public static readonly PowerStatus Empty = new(GoingState.None, LastDown.Unknown, null, UpdateState.None, null);
}

public interface IPowerStatus
{
    PowerStatus Status { get; }
}

/// <summary>Turns Windows' shutdown, crash and sleep records into the board's words.</summary>
public static class PowerReasons
{
    /// <summary>Processes that only shut down or restart the PC for updates.</summary>
    public static readonly IReadOnlySet<string> UpdateProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MoUsoCoreWorker.exe", "TrustedInstaller.exe", "usoclient.exe", "wuauclt.exe", "MusNotification.exe", "MusNotifyIcon.exe"
    };

    private static readonly string[] Majors = ["other", "hardware", "os", "software", "application", "system", "power", "legacy_api"];

    private static readonly Dictionary<uint, string> Minors = new()
    {
        [0x0] = "other", [0x1] = "maintenance", [0x2] = "installation", [0x3] = "upgrade", [0x4] = "reconfig", [0x5] = "hung",
        [0x6] = "unstable", [0x7] = "disk", [0x8] = "processor", [0x9] = "network_card", [0xA] = "power_supply",
        [0xB] = "cord_unplugged", [0xC] = "environment", [0xD] = "hardware_driver", [0xE] = "other_driver", [0xF] = "blue_screen",
        [0x10] = "service_pack", [0x11] = "hotfix", [0x12] = "security_fix", [0x13] = "security", [0x14] = "network_connectivity",
        [0x15] = "wmi", [0x16] = "service_pack_uninstall", [0x17] = "hotfix_uninstall", [0x18] = "security_fix_uninstall",
        [0x19] = "mmc", [0x1A] = "system_restore", [0x1B] = "terminal_services", [0x1C] = "dc_promotion", [0x1D] = "dc_demotion",
        [0xFF] = "none"
    };

    /// <summary>"C:\Windows\system32\shutdown.exe (PC)" -> "shutdown.exe".</summary>
    public static string ProcessName(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return "";
        string p = process.Trim();
        int slash = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        int open = p.LastIndexOf(" (", StringComparison.Ordinal);
        if (p.EndsWith(')') && open > slash) p = p[..open].Trim();
        slash = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return slash >= 0 ? p[(slash + 1)..].Trim() : p;
    }

    public static bool TryParseCode(string? hex, out uint code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    /// <summary>0x80020010 -> "0x80020010 os/service_pack planned".</summary>
    public static string DescribeCode(uint code)
    {
        uint major = (code >> 16) & 0xFF, minor = code & 0xFFFF;
        string majorName = major < Majors.Length ? Majors[major] : $"major_{major:x}";
        string minorName = Minors.TryGetValue(minor, out var m) ? m : $"minor_{minor:x}";
        string text = $"0x{code:x8} {majorName}/{minorName}";
        if ((code & 0x80000000) != 0) text += " planned";
        if ((code & 0x40000000) != 0) text += " user_defined";
        return text;
    }

    /// <summary>Event 1074 -> shutdown, restart, update_shutdown or update_restart.</summary>
    /// <param name="type">Shutdown type: "restart", "power off", "shutdown".</param>
    public static string ClassifyShutdown(string? process, string? type, string? codeHex, bool updatePending)
    {
        bool restart = type != null && type.Contains("restart", StringComparison.OrdinalIgnoreCase);
        bool update = updatePending || UpdateProcesses.Contains(ProcessName(process));
        if (!update && TryParseCode(codeHex, out uint code))
        {
            uint major = (code >> 16) & 0xFF, minor = code & 0xFFFF;
            update = major == 2 && minor is 0x3 or 0x10 or 0x11 or 0x12;
        }
        return (update ? "update_" : "") + (restart ? "restart" : "shutdown");
    }

    /// <summary>Kernel-Power 41 (rebooted without a clean shutdown) -> kind and why.</summary>
    public static (string Kind, string Why) Unexpected(uint bugcheck, int sleepInProgress, ulong powerButtonTimestamp, bool longPowerButtonPress)
    {
        if (bugcheck != 0) return ("bluescreen", $"bugcheck 0x{bugcheck:x}");
        if (longPowerButtonPress || powerButtonTimestamp != 0) return ("power_button", "power button held");
        if (sleepInProgress != 0) return ("unexpected", $"lost power while {SleepState(sleepInProgress)}");
        return ("unexpected", "lost power or stopped responding");
    }

    /// <summary>SYSTEM_POWER_STATE: 2-4 = S1-S3 sleep, 5 = hibernate, 6 = shutdown.</summary>
    public static string SleepState(int state) => state switch
    {
        5 => "hibernate",
        6 => "shutdown",
        _ => "sleep"
    };

    /// <summary>One line or "-": the feed and the state file never carry empty or multi-line values.</summary>
    public static string Clean(string? s, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(s)) return "-";
        string t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>The reason title and the comment, as going_why shows them.</summary>
    public static string Why(string? title, string? comment)
    {
        string t = Clean(title), c = Clean(comment);
        if (c == "-") return t;
        return t == "-" ? c : Clean($"{t}: {c}");
    }
}

/// <summary>Power status as feed / state file keys (the state file adds "hw.").</summary>
public static class PowerStatusKeys
{
    public static void AddTo(PowerStatus s, List<KeyValuePair<string, double?>> values, List<KeyValuePair<string, string>> texts)
    {
        texts.Add(new("going", s.Going.Kind));
        texts.Add(new("going_by", s.Going.By));
        texts.Add(new("going_why", s.Going.Why));
        texts.Add(new("going_code", s.Going.Code));
        texts.Add(new("going_user", s.Going.User));
        values.Add(new("going_id", s.Going.Id));
        texts.Add(new("last_down", s.LastDown.Kind));
        texts.Add(new("last_down_at", Local(s.LastDown.At)));
        texts.Add(new("last_down_by", s.LastDown.By));
        texts.Add(new("last_down_why", s.LastDown.Why));
        texts.Add(new("wake_from", s.Wake?.From ?? "-"));
        texts.Add(new("wake_at", Local(s.Wake?.At)));
        texts.Add(new("wake_by", s.Wake?.By ?? "-"));
        values.Add(new("update_pending", s.Update.Pending ? 1 : 0));
        texts.Add(new("update_pending_by", s.Update.PendingBy));
        values.Add(new("update_running", s.Update.Running ? 1 : 0));
        texts.Add(new("power_do", s.LastDo == null ? "-" : PowerDoText(s.LastDo)));
        texts.Add(new("power_do_at", Local(s.LastDo?.AtUtc)));
    }

    /// <summary>"sleep accepted", "hibernate declined Hibernation is off."</summary>
    public static string PowerDoText(PowerDoResult r) => PowerReasons.Clean(r.Why == "-" ? $"{r.Action} {r.Outcome}" : $"{r.Action} {r.Outcome} {r.Why}", 100);

    /// <summary>Local "ddd HH:mm", like the board shows; "-" when unknown.</summary>
    public static string Local(DateTime? t) =>
        t is { } v && v != DateTime.MinValue ? (v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v).ToString("ddd HH:mm", CultureInfo.InvariantCulture) : "-";
}
