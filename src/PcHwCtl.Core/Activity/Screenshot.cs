using System.Globalization;
using System.Text.RegularExpressions;

namespace PcHwCtl.Core.Activity;

/// <summary>
/// Screenshot on request (the ESP32 helper's `S` command), off unless the config turns it on: the user-session agent
/// captures every monitor into one PNG in memory, shows a red "Screenshot Captured" border so nobody is captured
/// unknowingly, posts the PNG to a Discord webhook listed in WebhookIds and deletes that post after DeleteMin. The PNG
/// is never written to a file.
/// </summary>
public sealed class ScreenshotConfig
{
    public bool Enabled { get; set; }

    /// <summary>Webhooks a screenshot may go to: the number in .../webhooks/&lt;id&gt;/&lt;token&gt;, not a secret. Empty = none.</summary>
    public List<string> WebhookIds { get; set; } = [];

    /// <summary>Minutes before the Discord post is deleted; 0 = keep it.</summary>
    public double DeleteMin { get; set; } = 5;

    /// <summary>Seconds the red border stays up (shown after the capture, never in it), 1-60. No border, no post.</summary>
    public int BorderS { get; set; } = 5;

    /// <summary>Seconds after a capture before the next one is accepted; sooner gets "busy".</summary>
    public int MinIntervalS { get; set; } = 10;
}

/// <param name="Why">"-" when accepted, else: off, busy, webhook, capture, agent.</param>
public sealed record ShotResult(bool Accepted, string Why)
{
    public static readonly ShotResult Ok = new(true, "-");
    public static ShotResult Declined(string why) => new(false, why);
}

public interface IScreenshot
{
    /// <summary>Captures now and returns; the border and the upload run on after. Never throws.</summary>
    ShotResult Take(string nonce, string webhook);
}

public static partial class ScreenshotLogic
{
    /// <summary>
    /// A plain Discord webhook URL, nothing that would change where the post goes or what Discord answers: no port, user
    /// info or fragment, and of the query only thread_id (the delete needs ?wait=true's reply).
    /// </summary>
    public static bool IsWebhook(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com"
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && !url!.Contains('#')
        && WebhookPath().IsMatch(uri.AbsolutePath)
        && (uri.Query.Length == 0 || ThreadQuery().IsMatch(uri.Query));

    /// <summary>
    /// A webhook this config lists. Any local program can make its own Discord webhook, so "is Discord" alone would
    /// still let a pipe client send the desktop to itself.
    /// </summary>
    public static bool IsAllowed(string? url, ScreenshotConfig cfg) =>
        IsWebhook(url) && cfg.WebhookIds.Contains(WebhookPath().Match(new Uri(url!).AbsolutePath).Groups[1].Value);

    public static string PostUrl(string webhook) => webhook + (webhook.Contains('?') ? "&" : "?") + "wait=true";

    /// <summary>…/messages/&lt;id&gt;, keeping the query: a post in a thread is only found with its thread_id.</summary>
    public static string MessageUrl(string webhook, string messageId)
    {
        string[] parts = webhook.Split('?', 2);
        return $"{parts[0].TrimEnd('/')}/messages/{messageId}{(parts.Length == 2 ? "?" + parts[1] : "")}";
    }

    /// <summary>A failed delete worth trying again: no answer, timeout, rate limit, Discord's own errors. Null = no answer.</summary>
    public static bool RetryDelete(int? status) => status is null or 408 or 429 or >= 500;

    [GeneratedRegex(@"\A/api/webhooks/([0-9]+)/[A-Za-z0-9_-]+/?\z", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookPath();

    [GeneratedRegex(@"\A\?thread_id=[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadQuery();
}

/// <summary>One line of shot-deletes.txt. Webhook is whatever the caller stored (the agent seals it).</summary>
public sealed record PendingDelete(long DueTicks, long GiveUpTicks, string Webhook, string MessageId)
{
    private static readonly long Window = TimeSpan.FromDays(7).Ticks;

    public static PendingDelete New(DateTime dueUtc, string webhook, string messageId) => new(dueUtc.Ticks, dueUtc.Ticks + Window, webhook, messageId);

    public string Format() => string.Create(CultureInfo.InvariantCulture, $"{DueTicks}\t{GiveUpTicks}\t{Webhook}\t{MessageId}");

    /// <summary>Reads a line, also the older "due, webhook, id".</summary>
    public static bool TryParse(string line, out PendingDelete job)
    {
        job = null!;
        string[] p = line.Split('\t');
        if (p.Length is not (3 or 4) || !long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out long due)) return false;
        long giveUp = due + Window;
        if (p.Length == 4 && !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out giveUp)) return false;
        string id = p[^1];
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) return false;
        job = new PendingDelete(due, giveUp, p[^2], id);
        return true;
    }

    public bool Expired(long nowTicks) => nowTicks >= GiveUpTicks;

    /// <summary>The next try: a minute at first, then as long again as it has been failing.</summary>
    public PendingDelete Retry(long nowTicks)
    {
        long failing = nowTicks - (GiveUpTicks - Window);
        return this with { DueTicks = Math.Min(GiveUpTicks, nowTicks + Math.Max(TimeSpan.TicksPerMinute, failing)) };
    }
}
