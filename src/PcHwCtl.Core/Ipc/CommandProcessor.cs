using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcHwCtl.Core.Activity;
using PcHwCtl.Core.Control;
using PcHwCtl.Core.Display;
using PcHwCtl.Core.Lasso;
using PcHwCtl.Core.Rgb;
using PcHwCtl.Core.Sensors;

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
    private readonly PersistedSettings? _persisted;
    private readonly IDisplayControl? _displays;
    private readonly ILassoControl? _lasso;
    private readonly SensorFeedHub? _sensors;
    private readonly IProcessActivity? _processes;
    private readonly IPowerRequests? _power;
    private readonly IPowerPlans? _plans;
    private readonly Func<PowerConfig> _powerConfig;
    private readonly IPowerActions? _actions;
    private readonly IPowerStatus? _status;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
    /// </summary>
    /// <param name="persisted">Where Zero RPM toggles and brightness are saved; null = not saved.</param>
    /// <param name="displays">Monitor control; null = display commands report it is off.</param>
    public CommandProcessor(IFanControl fans, IRgbControl rgb, Func<IpcConfig> ipcConfig, IClock clock, PersistedSettings? persisted = null, IDisplayControl? displays = null, ILassoControl? lasso = null, SensorFeedHub? sensors = null,
        IProcessActivity? processes = null, IPowerRequests? power = null,
        IPowerPlans? plans = null, Func<PowerConfig>? powerConfig = null, IPowerActions? actions = null,
        IPowerStatus? status = null)
    {
        _actions = actions;
        _status = status;
        _displays = displays;
        _lasso = lasso;
        _sensors = sensors;
        _processes = processes;
        _power = power;
        _plans = plans;
        _powerConfig = powerConfig ?? (() => new PowerConfig());
        _fans = fans;
        _rgb = rgb;
        _ipcConfig = ipcConfig;
        _clock = clock;
        _persisted = persisted;
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
                "subscribe" => HandleSubscribe(tokens),
                "get" => HandleGet(tokens),
                "fan" => HandleFan(tokens),
                "rgb" => HandleRgb(tokens),
                "display" => _displays == null
                    ? Error("Display control is off (config displays.enabled).")
                    : DisplayCommands.Execute(_displays, tokens[1..]),
                "aff" => HandleAff(tokens),
                "lasso" => HandleLasso(tokens),
                "eco" => HandleEco(tokens),
                "proc" => HandleProc(tokens),
                "power" => HandlePower(tokens),
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

    /// <summary>"subscribe": get state JSON each tick. "subscribe sensors": key=value snapshots (PipeServer streams both).</summary>
    private string HandleSubscribe(string[] tokens)
    {
        if (tokens.Length == 1) return Ok(new { subscribed = true });
        if (tokens.Length == 2 && tokens[1].Equals("sensors", StringComparison.OrdinalIgnoreCase))
        {
            return _sensors == null ? Error("Sensor feed is off (config feed.enabled).") : Ok(new { subscribed = "sensors" });
        }
        return Error("Usage: subscribe [sensors]");
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

        if (tokens[1].Equals("sensors", StringComparison.OrdinalIgnoreCase))
        {
            if (_sensors == null) return Error("Sensor feed is off (config feed.enabled).");
            if (_sensors.Latest is not { } s) return Error("No sensor snapshot yet.");
            return Ok(new
            {
                seq = s.Seq,
                ts = s.UnixMs,
                values = s.Values.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value is double v ? Math.Round(v, 2) : null))
                    .Concat((s.Texts ?? []).Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            });
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
            "zerorpm" => HandleFanZeroRpm(tokens),
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

    private string HandleFanZeroRpm(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            return Error("Usage: fan zerorpm <fan|all> <on|off|default>");
        }

        bool? enabled = tokens[3].ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            "default" => null,
            _ => throw new ArgumentException($"Zero RPM mode must be on, off or default, not '{tokens[3]}'.")
        };

        string target = tokens[2];
        List<string> targetFans;
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetFans = _fans.FanNames.Where(_fans.SupportsZeroRpm).ToList();
            if (targetFans.Count == 0)
            {
                return Error("No fan has a zeroRpm config.");
            }
        }
        else
        {
            string? matched = _fans.FanNames.FirstOrDefault(f => f.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched == null)
            {
                return Error($"Unknown fan '{target}'.");
            }
            if (!_fans.SupportsZeroRpm(matched))
            {
                return Error($"Fan '{matched}' has no zeroRpm config.");
            }
            targetFans = [matched];
        }

        foreach (string fan in targetFans)
        {
            _fans.SetZeroRpm(fan, enabled);
            _persisted?.SetZeroRpm(fan, enabled);
        }

        return Ok(null);
    }

    private string HandleAff(string[] tokens)
    {
        if (_lasso == null) return Error("Process Lasso control is off (config lasso.enabled).");
        const string usage = "Usage: aff set <exe> <cpus> | aff clear <exe> | aff list";
        return LassoCall(() => (tokens.Length, tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "") switch
        {
            (4, "set") => Ok(ResultDto(_lasso.SetAffinity(tokens[2], tokens[3]))),
            (3, "clear") => Ok(ResultDto(_lasso.ClearAffinity(tokens[2]))),
            (2, "list") => Ok(new { rules = _lasso.Rules().Select(r => new { name = r.Name, cpus = r.Cpus }) }),
            _ => Error(usage)
        });
    }

    private string HandleLasso(string[] tokens)
    {
        if (_lasso == null) return Error("Process Lasso control is off (config lasso.enabled).");
        const string usage = "Usage: lasso load <profile> | lasso save <profile> | lasso profiles";
        return LassoCall(() => (tokens.Length, tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "") switch
        {
            (3, "load") => Ok(ResultDto(_lasso.Load(tokens[2]))),
            (3, "save") => Ok(ResultDto(_lasso.Save(tokens[2]))),
            (2, "profiles") => Ok(new { profiles = _lasso.Profiles() }),
            _ => Error(usage)
        });
    }

    private static string LassoCall(Func<string> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is LassoException or IOException or UnauthorizedAccessException)
        {
            return Error(ex.Message);
        }
    }

    private string HandleEco(string[] tokens)
    {
        if (_lasso == null) return Error("Process Lasso control is off (config lasso.enabled).");
        const string usage = "Usage: eco on <exe> | eco off <exe|all> | eco list";
        return LassoCall(() => (tokens.Length, tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "") switch
        {
            (3, "on") => Ok(ResultDto(_lasso.Eco(tokens[2], true))),
            (3, "off") when tokens[2].Equals("all", StringComparison.OrdinalIgnoreCase) => Ok(ResultDto(_lasso.EcoOffAll())),
            (3, "off") => Ok(ResultDto(_lasso.Eco(tokens[2], false))),
            (2, "list") => Ok(new { eco = _lasso.State.Eco }),
            _ => Error(usage)
        });
    }

    private string HandleProc(string[] tokens)
    {
        if (_processes == null) return Error("Process CPU sampling is off.");
        int n = 5;
        if (tokens.Length is < 2 or > 3 || !tokens[1].Equals("top", StringComparison.OrdinalIgnoreCase)
            || (tokens.Length == 3 && (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n is < 1 or > 50)))
        {
            return Error("Usage: proc top [1-50]");
        }
        return Ok(new { processes = _processes.Top(n).Select(p => new { exe = p.Exe, cores = Math.Round(p.Cores, 2), instances = p.Instances }) });
    }

    /// <summary>"power plan": list; "power plan night|day": the configured plans; "power plan &lt;name|guid&gt;": any plan.
    /// "power caps" and "power do &lt;action&gt;": sleep, hibernate, restart, shutdown, lock. "power status": going, last down,
    /// wake, updates, last power do.</summary>
    private string HandlePower(string[] tokens)
    {
        const string usage = "Usage: power plan [night|day|<name|guid>] | power caps | power do <sleep|hibernate|restart|shutdown|lock> [delayMs] | power status";
        if (tokens.Length < 2) return Error(usage);
        string sub = tokens[1].ToLowerInvariant();
        if (sub == "status") return tokens.Length != 2 ? Error(usage) : _status == null ? Error("Power status is off.") : Ok(StatusDto(_status.Status));
        if (sub is "caps" or "do") return HandlePowerAction(tokens, usage);
        if (sub != "plan") return Error(usage);
        if (_plans == null) return Error("Power plan control is off.");
        try
        {
            if (tokens.Length == 2)
            {
                return Ok(new { plans = _plans.List().Select(p => new { name = p.Name, guid = p.Guid, active = p.Active }) });
            }
            string target = string.Join(' ', tokens.Skip(2));
            var cfg = _powerConfig();
            if (target.Equals("night", StringComparison.OrdinalIgnoreCase)) target = cfg.NightPlan;
            else if (target.Equals("day", StringComparison.OrdinalIgnoreCase)) target = cfg.DayPlan;
            var plan = _plans.Set(target);
            return Ok(new { plan = plan.Name, guid = plan.Guid });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error(ex.Message);
        }
    }

    private string HandlePowerAction(string[] tokens, string usage)
    {
        if (_actions == null) return Error("Power actions are off.");
        try
        {
            if (tokens[1].Equals("caps", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length != 2) return Error(usage);
                var caps = _actions.Caps();
                return Ok(new { sleep = caps.Sleep, hibernate = caps.Hibernate });
            }
            if (tokens.Length is < 3 or > 4 || !Enum.TryParse<PowerAction>(tokens[2], true, out var action) || !Enum.IsDefined(action) || int.TryParse(tokens[2], out _))
                return Error(usage);
            int delayMs = 0;
            if (tokens.Length == 4 && (!int.TryParse(tokens[3], NumberStyles.None, CultureInfo.InvariantCulture, out delayMs) || delayMs > 10000))
                return Error("delayMs is 0-10000.");
            _actions.Do(action, delayMs);
            return Ok(new { action = action.ToString().ToLowerInvariant(), accepted = true });
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    private static object StatusDto(PowerStatus s) => new
    {
        going = new { kind = s.Going.Kind, by = s.Going.By, why = s.Going.Why, code = s.Going.Code, user = s.Going.User, id = s.Going.Id,
            at = s.Going.Kind == "none" ? (DateTime?)null : s.Going.AtUtc },
        lastDown = new { kind = s.LastDown.Kind, at = s.LastDown.At, by = s.LastDown.By, why = s.LastDown.Why },
        wake = s.Wake == null ? null : new { from = s.Wake.From, at = s.Wake.At, sleptAt = s.Wake.SleptAt, by = s.Wake.By },
        update = new { pending = s.Update.Pending, pendingBy = s.Update.PendingBy, running = s.Update.Running },
        lastDo = s.LastDo == null ? null : new { action = s.LastDo.Action, outcome = s.LastDo.Outcome, why = s.LastDo.Why, at = s.LastDo.AtUtc }
    };

    private string? PlanName()
    {
        try
        {
            return _plans?.Active?.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static object PowerDto(PowerRequestState s, string? plan) => new
    {
        plan,
        display = s.Count("DISPLAY"),
        displayBy = s.First("DISPLAY"),
        system = s.Count("SYSTEM"),
        systemBy = s.First("SYSTEM"),
        execution = s.Count("EXECUTION"),
        executionBy = s.First("EXECUTION")
    };

    private static object ResultDto(LassoResult r) => new { result = r.Message, changed = r.Changed };

    private static object LassoDto(LassoState s) => new
    {
        rules = s.Rules,
        eco = s.Eco
    };

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
        _persisted?.SetRgbBrightness(brightness);
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
                Override: ovr,
                ZeroRpm: s.ZeroRpm,
                Parked: s.Parked);
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

        DisplaysView? displays = _displays == null ? null : new DisplaysView(_displays.Status, _displays.Displays);
        return new StateDto(fanDict, rgbState, displays, _lasso == null ? null : LassoDto(_lasso.State), _power == null ? null : PowerDto(_power.State, PlanName()));
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
    [property: JsonPropertyName("rgb")] RgbStateDto Rgb,
    [property: JsonPropertyName("displays")] DisplaysView? Displays = null,
    [property: JsonPropertyName("lasso")] object? Lasso = null,
    [property: JsonPropertyName("power")] object? Power = null);

/// <summary>Fan state data transfer object.</summary>
public sealed record FanStateDto(
    [property: JsonPropertyName("pct")] double Pct,
    [property: JsonPropertyName("target")] double Target,
    [property: JsonPropertyName("temp")] double? Temp,
    [property: JsonPropertyName("rpm")] int? Rpm,
    [property: JsonPropertyName("missing")] bool Missing,
    [property: JsonPropertyName("curve")] string Curve,
    [property: JsonPropertyName("override")] FanOverrideDto? Override,
    [property: JsonPropertyName("zeroRpm")] bool? ZeroRpm,
    [property: JsonPropertyName("parked")] bool Parked);

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
