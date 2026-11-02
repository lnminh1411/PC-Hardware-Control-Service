using System.Text.Json;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Display;
using PcHwCtl.Service.Activity;

namespace PcHwCtl.Service.Display;

/// <summary>
/// What the user-session agent answers besides display commands: "presence" (one camera check) and
/// "shot &lt;nonce&gt;" (capture + border; the PNG follows the reply line as data.bytes raw bytes). Both need the user's
/// session: session 0 has no camera access and no desktop. No webhook ever comes here.
/// </summary>
public sealed class AgentCommands(IDisplayControl displays, IPresence presence, ScreenShooter shooter)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The reply line, and for "shot" the PNG to send after it (the caller sends it, then clears it).</summary>
    public (string Reply, byte[]? Payload) Execute(string[] tokens)
    {
        string verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "";
        switch (verb)
        {
            case "presence":
                return (Ok(presence.Check()), null);
            case "shot" when tokens.Length == 2:
                var shot = shooter.Capture(tokens[1]);
                return (Ok(new { why = shot.Why, bytes = shot.Png?.Length ?? 0 }), shot.Png);
            case "shot":
                return ("""{"ok":false,"error":"Usage: shot <nonce>"}""", null);
            default:
                return (DisplayCommands.Execute(displays, tokens), null);
        }
    }

    private static string Ok(object data) => JsonSerializer.Serialize(new { ok = true, data }, Json);
}

/// <summary>Presence and screenshot captures for the session-0 service, relayed to the agent.</summary>
public sealed class AgentFeatures(AgentDisplayClient client) : IPresence
{
    // Camera open + settle + inference is 1-3 s; the first check after the agent starts also compiles the model.
    private const int PresenceTimeoutMs = 60_000;
    // Capture + PNG encode of every monitor + border paint.
    private const int ShotTimeoutMs = 30_000;

    public PresenceResult Check()
    {
        string? reply = client.Request("presence", PresenceTimeoutMs);
        if (reply == null) return PresenceResult.Unknown("agent");
        var r = Data<PresenceResult>(reply);
        // Only the agent's own words: a reply that isn't one of them reads as "the agent failed".
        return r is { State: "yes" or "no" or "unknown", Zone: "-" or "desk" or "near" or "room" } && double.IsFinite(r.Conf)
            && double.IsFinite(r.Luma) && double.IsFinite(r.Depth) ? r : PresenceResult.Unknown("agent");
    }

    /// <summary>The agent's capture; the PNG is the caller's to clear.</summary>
    public ShotCapture Capture(string nonce)
    {
        var (reply, png) = client.RequestWithPayload($"shot {nonce}", ShotTimeoutMs, BytesOf);
        if (reply == null) return ShotCapture.Declined("agent");
        var head = Data<ShotHead>(reply);
        if (head == null || png == null)
        {
            if (png != null) Array.Clear(png);
            return ShotCapture.Declined(head?.Why is "off" or "busy" or "display" or "locked" or "capture" ? head.Why : "agent");
        }
        return new ShotCapture("-", png);
    }

    private sealed record ShotHead(string Why, int Bytes);

    private static int BytesOf(string reply) => Data<ShotHead>(reply)?.Bytes ?? 0;

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
