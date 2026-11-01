using System.Text.Json;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Display;

namespace PcHwCtl.Service.Display;

/// <summary>
/// What the user-session agent answers besides display commands: "presence" (one camera check) and
/// "shot &lt;nonce&gt; &lt;webhook&gt;" (screenshot). Both need the user's session: session 0 has no camera access and no desktop.
/// </summary>
public sealed class AgentCommands(IDisplayControl displays, IPresence presence, IScreenshot shooter)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Execute(string[] tokens)
    {
        string verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "";
        return verb switch
        {
            "presence" => Ok(presence.Check()),
            "shot" when tokens.Length == 3 => Ok(shooter.Take(tokens[1], tokens[2])),
            "shot" => """{"ok":false,"error":"Usage: shot <nonce> <webhook>"}""",
            _ => DisplayCommands.Execute(displays, tokens)
        };
    }

    /// <summary>The line as logged: a webhook URL is a secret (anyone holding it can post), so it stays out of the log.</summary>
    public static string ForLog(string[] tokens) =>
        tokens.Length > 0 && tokens[0].Equals("shot", StringComparison.OrdinalIgnoreCase)
            ? $"shot {(tokens.Length > 1 ? tokens[1] : "")} <webhook>"
            : string.Join(' ', tokens);

    private static string Ok(object data) => JsonSerializer.Serialize(new { ok = true, data }, Json);
}

/// <summary>Presence and screenshots for the session-0 service, relayed to the agent.</summary>
public sealed class AgentFeatures(AgentDisplayClient client) : IPresence, IScreenshot
{
    // Camera open + settle + inference is 1-3 s; the first check after the agent starts also compiles the model.
    private const int PresenceTimeoutMs = 60_000;

    public PresenceResult Check()
    {
        string? reply = client.Request("presence", PresenceTimeoutMs);
        return reply == null ? PresenceResult.Unknown("agent") : Data<PresenceResult>(reply) ?? PresenceResult.Unknown("agent");
    }

    public ShotResult Take(string nonce, string webhook)
    {
        // Checked here too, so a bad URL never reaches the agent.
        if (!ScreenshotLogic.IsWebhook(webhook)) return ShotResult.Declined("webhook");
        string? reply = client.Request($"shot {nonce} {webhook}", 15_000);
        return reply == null ? ShotResult.Declined("agent") : Data<ShotResult>(reply) ?? ShotResult.Declined("agent");
    }

    private static T? Data<T>(string reply) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.TryGetProperty("data", out var data) ? data.Deserialize<T>(AgentCommands.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
