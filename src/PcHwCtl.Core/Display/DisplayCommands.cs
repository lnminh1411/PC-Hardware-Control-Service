using System.Globalization;
using System.Text.Json;

namespace PcHwCtl.Core.Display;

/// <summary>
/// The display command set, shared by the service ("display ..." on the main pipe) and the user-session agent
/// (same words without "display" on its own pipe). Replies are {"ok":true,"data":...} or {"ok":false,"error":"..."}.
/// </summary>
public static class DisplayCommands
{
    public const string Usage = "display list | bright <n|all> <0-100> | power <n|all> on|off | input <n|all> <code> | sleep | wake";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="args">Tokens starting at the subcommand, e.g. ["power", "2", "off"].</param>
    public static string Execute(IDisplayControl displays, string[] args)
    {
        if (args.Length == 0) return Error("Usage: " + Usage);

        string sub = args[0].ToLowerInvariant();
        if (sub == "list") return Ok(new DisplaysView(displays.Status, displays.Displays));
        if (sub == "sleep") return Reply(displays.Sleep());
        if (sub == "wake") return Reply(displays.Wake());

        if (args.Length != 3) return Error("Usage: " + Usage);
        string target = args[1];
        string value = args[2];

        switch (sub)
        {
            case "bright":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)
                    ? Reply(displays.Brightness(target, pct))
                    : Error("brightness is 0-100");
            case "power":
                return value.ToLowerInvariant() switch
                {
                    "on" => Reply(displays.Power(target, true)),
                    "off" => Reply(displays.Power(target, false)),
                    _ => Error("power is on or off")
                };
            case "input":
                return DisplayController.TryParseCode(value, out uint code)
                    ? Reply(displays.Input(target, (int)code))
                    : Error("input code is a number, e.g. 0x11 or 17");
            default:
                return Error($"Unknown display subcommand '{args[0]}'. Usage: {Usage}");
        }
    }

    private static string Reply(DisplayResult result) => result.Ok ? Ok(null) : Error(result.Error ?? "failed");

    private static string Ok(object? data) => JsonSerializer.Serialize(new { ok = true, data }, Json);

    private static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, Json);
}

/// <summary>"display list" data and the "displays" part of "get state".</summary>
public sealed record DisplaysView(string? Status, IReadOnlyList<DisplayState> Monitors);
