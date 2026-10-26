namespace PcHwCtl.Core.Rgb;

/// <summary>
/// Validates RGB configurations against schema rules and system sensors/fans.
/// </summary>
public static class RgbConfigValidator
{
    private static readonly HashSet<string> ValidRuleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "static", "gradient", "breathing", "rainbow"
    };

    /// <summary>
    /// Validates the specified RGB configuration against root configuration.
    /// </summary>
    /// <param name="rgb">The RGB configuration to validate.</param>
    /// <param name="root">The root configuration containing fans and sensors.</param>
    /// <returns>A list of validation error messages, or an empty list if valid.</returns>
    public static IReadOnlyList<string> Validate(RgbConfig rgb, Config root)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentNullException.ThrowIfNull(root);

        var problems = new List<string>();

        if (rgb.Fps < 1 || rgb.Fps > 60)
        {
            problems.Add($"RGB fps {rgb.Fps} outside valid range 1..60.");
        }

        foreach (var (device, fps) in rgb.DeviceFps)
        {
            if (fps < 1 || fps > 60)
            {
                problems.Add($"RGB deviceFps '{device}' = {fps} outside valid range 1..60.");
            }
        }

        if (rgb.Brightness < 0f || rgb.Brightness > 100f)
        {
            problems.Add($"RGB brightness {rgb.Brightness} outside valid range 0..100.");
        }

        if (rgb.Groups != null)
        {
            foreach (var (groupName, groupCfg) in rgb.Groups)
            {
                if (groupCfg.Zones != null)
                {
                    foreach (var zoneRef in groupCfg.Zones)
                    {
                        if (string.IsNullOrWhiteSpace(zoneRef) || !zoneRef.Contains('/'))
                        {
                            problems.Add($"Group '{groupName}' has invalid zone reference '{zoneRef}', expected '<deviceId>/<zone>' or '<deviceId>/*'.");
                        }
                    }
                }

                var rule = groupCfg.Rule;
                if (rule == null)
                {
                    problems.Add($"Group '{groupName}' has null rule.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.Type) || !ValidRuleTypes.Contains(rule.Type))
                {
                    problems.Add($"Group '{groupName}' has unknown rule type '{rule.Type}'.");
                    continue;
                }

                string type = rule.Type.ToLowerInvariant();

                if (type is "static" or "breathing")
                {
                    if (string.IsNullOrWhiteSpace(rule.Color) || !RgbColor.TryParse(rule.Color, out _))
                    {
                        problems.Add($"Group '{groupName}' has bad color '{rule.Color}'.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(rule.Color) && !RgbColor.TryParse(rule.Color, out _))
                {
                    problems.Add($"Group '{groupName}' has bad color '{rule.Color}'.");
                }

                if (!string.IsNullOrWhiteSpace(rule.Fallback) && !RgbColor.TryParse(rule.Fallback, out _))
                {
                    problems.Add($"Group '{groupName}' has bad fallback color '{rule.Fallback}'.");
                }

                if (type is "breathing" or "rainbow")
                {
                    if (rule.PeriodS <= 0f)
                    {
                        problems.Add($"Group '{groupName}' has PeriodS {rule.PeriodS} <= 0 for rule '{type}'.");
                    }
                }

                if (type == "gradient")
                {
                    if (string.IsNullOrWhiteSpace(rule.Source))
                    {
                        problems.Add($"Group '{groupName}' gradient rule is missing Source.");
                    }
                    else if (!IsValidGradientSource(rule.Source, root))
                    {
                        problems.Add($"Group '{groupName}' gradient has invalid source '{rule.Source}'.");
                    }

                    if (rule.Stops == null || rule.Stops.Count < 2)
                    {
                        problems.Add($"Group '{groupName}' gradient rule requires at least 2 stops.");
                    }
                    else
                    {
                        bool stopsSorted = true;
                        for (int i = 0; i < rule.Stops.Count; i++)
                        {
                            var stop = rule.Stops[i];
                            if (string.IsNullOrWhiteSpace(stop.Color) || !RgbColor.TryParse(stop.Color, out _))
                            {
                                problems.Add($"Group '{groupName}' gradient stop at {stop.At} has bad color '{stop.Color}'.");
                            }

                            if (i > 0 && stop.At < rule.Stops[i - 1].At)
                            {
                                stopsSorted = false;
                            }
                        }

                        if (!stopsSorted)
                        {
                            problems.Add($"Group '{groupName}' gradient stops are not sorted by At position.");
                        }
                    }
                }
            }
        }

        return problems;
    }

    private static bool IsValidGradientSource(string source, Config root)
    {
        if (source.StartsWith('/'))
        {
            return true;
        }

        if (source.StartsWith("sel:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (root.Sensors != null && root.Sensors.ContainsKey(source))
        {
            return true;
        }

        if (source.StartsWith("fan:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = source[4..];
            string? fanName = null;
            if (rest.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase))
            {
                fanName = rest[..^4];
            }
            else if (rest.EndsWith(".pct", StringComparison.OrdinalIgnoreCase))
            {
                fanName = rest[..^4];
            }

            if (!string.IsNullOrEmpty(fanName) && root.Fans != null && root.Fans.ContainsKey(fanName))
            {
                return true;
            }
        }

        return false;
    }
}
