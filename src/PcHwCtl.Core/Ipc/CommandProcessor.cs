using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcHwCtl.Core.Control;
using PcHwCtl.Core.Rgb;

namespace PcHwCtl.Core.Ipc;

/// <summary>
/// Processes line-based IPC commands for fan and RGB hardware controls.
/// </summary>
public sealed class CommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IFanControl _fans;
    private readonly IRgbControl _rgb;
    private readonly Func<IpcConfig> _ipcConfig;
    private readonly IClock _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
    /// </summary>
    public CommandProcessor(IFanControl fans, IRgbControl rgb, Func<IpcConfig> ipcConfig, IClock clock)
    {
        _fans = fans;
        _rgb = rgb;
        _ipcConfig = ipcConfig;
        _clock = clock;
    }

    /// <summary>
    /// Executes a single line IPC command and returns a compact JSON response without trailing newline. Never throws.
    /// </summary>
    public string Execute(string line)
    {
        try
        {
            // Clients that write with a BOM-emitting UTF-8 encoder prefix the first line with U+FEFF.
            line = line?.TrimStart('﻿') ?? "";
            if (string.IsNullOrWhiteSpace(line))
            {
                return Error("Empty command.");
            }

            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return Error("Empty command.");
            }

            string verb = tokens[0].ToLowerInvariant();
            return verb switch
            {
                "ping" => Ok(new { pong = true }),
                "subscribe" => Ok(new { subscribed = true }),
                "get" => HandleGet(tokens),
                "fan" => HandleFan(tokens),
                "rgb" => HandleRgb(tokens),
                "release" => HandleGenericRelease(tokens),
                _ => Error($"Unknown command '{tokens[0]}'.")
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Returns the current state data object serialized as compact JSON.
    /// </summary>
    public string StateJson()
    {
        var state = BuildState();
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    private string HandleGet(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return Error("Missing parameter for 'get'.");
        }

        if (tokens[1].Equals("state", StringComparison.OrdinalIgnoreCase))
        {
            return $"{{\"ok\":true,\"data\":{StateJson()}}}";
        }

        return Error($"Unknown get target '{tokens[1]}'.");
    }

    private string HandleFan(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return Error("Missing fan subcommand.");
        }

        string sub = tokens[1].ToLowerInvariant();
        return sub switch
        {
            "set" => HandleFanSet(tokens),
            "curve" => HandleFanCurve(tokens),
            "release" => HandleFanRelease(tokens),
            _ => Error($"Unknown fan subcommand '{tokens[1]}'.")
        };
    }

    private string HandleFanSet(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            return Error("Usage: fan set <fan|all> <pct> [ttl]");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetFans;
        if (isAll)
        {
            targetFans = _fans.FanNames.ToList();
        }
        else
        {
            string? matched = _fans.FanNames.FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown fan '{target}'.");
            }
            targetFans = [matched];
        }

        if (!float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) ||
            float.IsNaN(pct) || pct < 0f || pct > 100f)
        {
            return Error($"Invalid pct '{tokens[3]}', must be between 0 and 100.");
        }

        IpcConfig cfg = _ipcConfig();
        int ttl = cfg.OverrideTtlS;
        if (tokens.Length >= 5)
        {
            if (!int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTtl))
            {
                return Error($"Invalid ttl '{tokens[4]}'.");
            }
            if (parsedTtl < 1)
            {
                return Error($"Fan ttl must be >= 1, got {parsedTtl}.");
            }
            ttl = parsedTtl;
        }

        if (ttl > cfg.MaxTtlS)
        {
            ttl = cfg.MaxTtlS;
        }

        TimeSpan ttlSpan = TimeSpan.FromSeconds(ttl);
        foreach (string fan in targetFans)
        {
            _fans.SetOverride(fan, pct, ttlSpan);
        }

        return Ok(null);
    }

    private string HandleFanCurve(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            return Error("Usage: fan curve <fan|all> <curve>");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetFans;
        if (isAll)
        {
            targetFans = _fans.FanNames.ToList();
        }
        else
        {
            string? matched = _fans.FanNames.FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown fan '{target}'.");
            }
            targetFans = [matched];
        }

        string curve = tokens[3];
        if (!_fans.HasCurve(curve))
        {
            return Error($"Unknown curve '{curve}'.");
        }

        foreach (string fan in targetFans)
        {
            _fans.SetCurveOverride(fan, curve);
        }

        return Ok(null);
    }

    private string HandleFanRelease(string[] tokens)
    {
        if (tokens.Length < 3)
        {
            return Error("Usage: fan release <fan|all>");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetFans;
        if (isAll)
        {
            targetFans = _fans.FanNames.ToList();
        }
        else
        {
            string? matched = _fans.FanNames.FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown fan '{target}'.");
            }
            targetFans = [matched];
        }

        foreach (string fan in targetFans)
        {
            _fans.ClearOverride(fan);
            _fans.ClearCurveOverride(fan);
        }

        return Ok(null);
    }

    private string HandleRgb(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return Error("Missing rgb subcommand.");
        }

        string sub = tokens[1].ToLowerInvariant();
        return sub switch
        {
            "set" => HandleRgbSet(tokens),
            "mode" => HandleRgbMode(tokens),
            "brightness" => HandleRgbBrightness(tokens),
            "release" => HandleRgbRelease(tokens),
            _ => Error($"Unknown rgb subcommand '{tokens[1]}'.")
        };
    }

    private string HandleRgbSet(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            return Error("Usage: rgb set <group|all> <#RRGGBB> [ttl]");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetGroups;
        if (isAll)
        {
            targetGroups = _rgb.Groups.Keys.ToList();
        }
        else
        {
            string? matched = _rgb.Groups.Keys.FirstOrDefault(g => g.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown rgb group '{target}'.");
            }
            targetGroups = [matched];
        }

        if (!RgbColor.TryParse(tokens[3], out var color))
        {
            return Error($"Invalid color '{tokens[3]}'.");
        }

        TimeSpan? ttlSpan = null;
        if (tokens.Length >= 5)
        {
            if (!int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ttl) || ttl < 0)
            {
                return Error($"Invalid ttl '{tokens[4]}'.");
            }
            if (ttl > 0)
            {
                IpcConfig cfg = _ipcConfig();
                if (ttl > cfg.MaxTtlS)
                {
                    ttl = cfg.MaxTtlS;
                }
                ttlSpan = TimeSpan.FromSeconds(ttl);
            }
        }

        var rule = new RgbRuleConfig
        {
            Type = "static",
            Color = color.ToString()
        };

        foreach (string grp in targetGroups)
        {
            _rgb.SetOverride(grp, rule, ttlSpan);
        }

        return Ok(null);
    }

    private string HandleRgbMode(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            return Error("Usage: rgb mode <group|all> <off|static|breathing|rainbow|gradient> [#RRGGBB] [ttl]");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetGroups;
        if (isAll)
        {
            targetGroups = _rgb.Groups.Keys.ToList();
        }
        else
        {
            string? matched = _rgb.Groups.Keys.FirstOrDefault(g => g.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown rgb group '{target}'.");
            }
            targetGroups = [matched];
        }

        string mode = tokens[3].ToLowerInvariant();
        string[] validModes = ["off", "static", "breathing", "rainbow", "gradient"];
        if (!validModes.Contains(mode))
        {
            return Error($"Unknown rgb mode '{tokens[3]}'.");
        }

        string? colorStr = null;
        TimeSpan? ttlSpan = null;

        int nextIdx = 4;
        if (tokens.Length > nextIdx)
        {
            if (RgbColor.TryParse(tokens[nextIdx], out var parsedColor))
            {
                colorStr = parsedColor.ToString();
                nextIdx++;
            }
            else if (mode == "static" || mode == "breathing")
            {
                if (!int.TryParse(tokens[nextIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return Error($"Invalid color '{tokens[nextIdx]}'.");
                }
            }
            else if (!int.TryParse(tokens[nextIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return Error($"Invalid color '{tokens[nextIdx]}'.");
            }
        }

        if (tokens.Length > nextIdx)
        {
            if (!int.TryParse(tokens[nextIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ttl) || ttl < 0)
            {
                return Error($"Invalid ttl '{tokens[nextIdx]}'.");
            }
            nextIdx++;
            if (ttl > 0)
            {
                IpcConfig cfg = _ipcConfig();
                if (ttl > cfg.MaxTtlS)
                {
                    ttl = cfg.MaxTtlS;
                }
                ttlSpan = TimeSpan.FromSeconds(ttl);
            }
        }

        if (tokens.Length > nextIdx)
        {
            return Error("Too many arguments for 'rgb mode'.");
        }

        var rule = new RgbRuleConfig
        {
            Type = mode,
            Color = colorStr
        };

        foreach (string grp in targetGroups)
        {
            _rgb.SetOverride(grp, rule, ttlSpan);
        }

        return Ok(null);
    }

    private string HandleRgbBrightness(string[] tokens)
    {
        if (tokens.Length < 3)
        {
            return Error("Usage: rgb brightness <0-100>");
        }

        if (!float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float brightness) ||
            float.IsNaN(brightness) || brightness < 0f || brightness > 100f)
        {
            return Error($"Invalid brightness '{tokens[2]}', must be between 0 and 100.");
        }

        _rgb.Brightness = brightness;
        return Ok(null);
    }

    private string HandleRgbRelease(string[] tokens)
    {
        if (tokens.Length < 3)
        {
            return Error("Usage: rgb release <group|all>");
        }

        string target = tokens[2];
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);

        List<string> targetGroups;
        if (isAll)
        {
            targetGroups = _rgb.Groups.Keys.ToList();
        }
        else
        {
            string? matched = _rgb.Groups.Keys.FirstOrDefault(g => g.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown rgb group '{target}'.");
            }
            targetGroups = [matched];
        }

        foreach (string grp in targetGroups)
        {
            _rgb.ClearOverride(grp);
        }

        return Ok(null);
    }

    private string HandleGenericRelease(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return Error("Usage: release <target|all>");
        }

        string target = tokens[1];
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string fan in _fans.FanNames)
            {
                _fans.ClearOverride(fan);
                _fans.ClearCurveOverride(fan);
            }
            foreach (string grp in _rgb.Groups.Keys)
            {
                _rgb.ClearOverride(grp);
            }
            return Ok(null);
        }

        bool matchedAny = false;
        string? matchedFan = _fans.FanNames.FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (matchedFan != null)
        {
            _fans.ClearOverride(matchedFan);
            _fans.ClearCurveOverride(matchedFan);
            matchedAny = true;
        }

        string? matchedGroup = _rgb.Groups.Keys.FirstOrDefault(g => g.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (matchedGroup != null)
        {
            _rgb.ClearOverride(matchedGroup);
            matchedAny = true;
        }

        if (!matchedAny)
        {
            return Error($"Unknown target '{target}'.");
        }

        return Ok(null);
    }

    private StateDto BuildState()
    {
        var fanDict = new Dictionary<string, FanStateDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, s) in _fans.States)
        {
            FanOverrideDto? ovr = null;
            if (s.Override != null)
            {
                double rem = (s.Override.ExpiresAtUtc - _clock.UtcNow).TotalSeconds;
                int expiresInS = Math.Max(0, (int)Math.Round(rem, MidpointRounding.AwayFromZero));
                ovr = new FanOverrideDto(
                    Math.Round(s.Override.Pct, 1, MidpointRounding.AwayFromZero),
                    expiresInS);
            }

            fanDict[name] = new FanStateDto(
                Pct: Math.Round(s.CurrentPct, 1, MidpointRounding.AwayFromZero),
                Target: Math.Round(s.TargetPct, 1, MidpointRounding.AwayFromZero),
                Temp: s.CurveTemp.HasValue ? Math.Round(s.CurveTemp.Value, 1, MidpointRounding.AwayFromZero) : null,
                Rpm: s.Rpm.HasValue ? (int)Math.Round(s.Rpm.Value, MidpointRounding.AwayFromZero) : null,
                Missing: s.SourceMissing,
                Curve: s.Curve,
                Override: ovr);
        }

        var rgbGroups = new Dictionary<string, RgbGroupStateDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, g) in _rgb.Groups)
        {
            RgbOverrideDto? ovr = null;
            if (!string.IsNullOrEmpty(g.OverrideRule))
            {
                int? expiresInS = null;
                if (g.OverrideExpiresUtc.HasValue)
                {
                    double rem = (g.OverrideExpiresUtc.Value - _clock.UtcNow).TotalSeconds;
                    expiresInS = Math.Max(0, (int)Math.Round(rem, MidpointRounding.AwayFromZero));
                }
                ovr = new RgbOverrideDto(g.OverrideRule, expiresInS);
            }
            rgbGroups[name] = new RgbGroupStateDto(g.Rule, ovr);
        }

        var devices = _rgb.DeviceStatus ?? new Dictionary<string, string>();
        var rgbState = new RgbStateDto(
            Brightness: Math.Round(_rgb.Brightness, 1, MidpointRounding.AwayFromZero),
            Groups: rgbGroups,
            Devices: devices);

        return new StateDto(fanDict, rgbState);
    }

    private static string Ok(object? data)
    {
        return JsonSerializer.Serialize(new OkResponse(data), JsonOptions);
    }

    private static string Error(string message)
    {
        return JsonSerializer.Serialize(new ErrorResponse(message), JsonOptions);
    }

    private sealed class OkResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok => true;

        [JsonPropertyName("data")]
        public object? Data { get; }

        public OkResponse(object? data) => Data = data;
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok => false;

        [JsonPropertyName("error")]
        public string Error { get; }

        public ErrorResponse(string error) => Error = error;
    }
}

/// <summary>Root state data transfer object.</summary>
public sealed record StateDto(
    [property: JsonPropertyName("fans")] IReadOnlyDictionary<string, FanStateDto> Fans,
    [property: JsonPropertyName("rgb")] RgbStateDto Rgb);

/// <summary>Fan state data transfer object.</summary>
public sealed record FanStateDto(
    [property: JsonPropertyName("pct")] double Pct,
    [property: JsonPropertyName("target")] double Target,
    [property: JsonPropertyName("temp")] double? Temp,
    [property: JsonPropertyName("rpm")] int? Rpm,
    [property: JsonPropertyName("missing")] bool Missing,
    [property: JsonPropertyName("curve")] string Curve,
    [property: JsonPropertyName("override")] FanOverrideDto? Override);

/// <summary>Fan override state data transfer object.</summary>
public sealed record FanOverrideDto(
    [property: JsonPropertyName("pct")] double Pct,
    [property: JsonPropertyName("expiresInS")] int ExpiresInS);

/// <summary>RGB state data transfer object.</summary>
public sealed record RgbStateDto(
    [property: JsonPropertyName("brightness")] double Brightness,
    [property: JsonPropertyName("groups")] IReadOnlyDictionary<string, RgbGroupStateDto> Groups,
    [property: JsonPropertyName("devices")] IReadOnlyDictionary<string, string> Devices);

/// <summary>RGB group state data transfer object.</summary>
public sealed record RgbGroupStateDto(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("override")] RgbOverrideDto? Override);

/// <summary>RGB override state data transfer object.</summary>
public sealed record RgbOverrideDto(
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("expiresInS")] int? ExpiresInS);
