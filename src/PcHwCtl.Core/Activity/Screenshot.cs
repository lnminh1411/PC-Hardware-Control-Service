namespace PcHwCtl.Core.Activity;

/// <summary>
/// Screenshot on request (the ESP32 helper's `S` command): the user-session agent captures every monitor into one
/// PNG in memory, shows a red "Screenshot Captured" border so nobody is captured unknowingly, posts the PNG to a
/// Discord webhook and deletes that post after DeleteMin. The PNG never touches the disk.
/// </summary>
public sealed class ScreenshotConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes before the Discord post is deleted; 0 = keep it.</summary>
    public double DeleteMin { get; set; } = 5;

    /// <summary>Seconds the red border stays up (shown after the capture, never in it); 0 = no border.</summary>
    public int BorderS { get; set; } = 5;
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

public static class ScreenshotLogic
{
    /// <summary>
    /// Only Discord webhooks: a screenshot of the user's desktop must not go to whatever URL a pipe client names.
    /// </summary>
    public static bool IsWebhook(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com"
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    /// <summary>Webhook without its query, for the message URL (…/messages/&lt;id&gt;).</summary>
    public static string Base(string webhook) => webhook.Split('?', 2)[0].TrimEnd('/');

    public static string PostUrl(string webhook) => webhook + (webhook.Contains('?') ? "&" : "?") + "wait=true";

    public static string MessageUrl(string webhook, string messageId) => $"{Base(webhook)}/messages/{messageId}";
}
