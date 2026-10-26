using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcHwCtl.Core;

public static class FanControlImporter
{
    public static Config Import(string fanControlJsonText)
    {
        using var doc = JsonDocument.Parse(fanControlJsonText, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        if (!root.TryGetProperty("FanControl", out var fcElement))
        {
            throw new FormatException("Missing 'FanControl' root property in JSON.");
        }

        var config = new Config
        {
            TickMs = 1000,
            StaleSensorS = 5
        };

        // 1. Process CustomSensors (mix)
        var mixKeyById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fcElement.TryGetProperty("CustomSensors", out var customSensorsElement) &&
            customSensorsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in customSensorsElement.EnumerateArray())
            {
                string nickName = s.GetProperty("NickName").GetString() ?? "";
                string key = nickName.ToLowerInvariant();
                string identifier = s.GetProperty("Identifier").GetString() ?? "";
                mixKeyById[identifier] = key;
            }

            foreach (var s in customSensorsElement.EnumerateArray())
            {
                string nickName = s.GetProperty("NickName").GetString() ?? "";
                string key = nickName.ToLowerInvariant();

                int mixFn = s.GetProperty("SelectedMixFunction").GetInt32();
                if (mixFn != 1)
                {
                    throw new NotSupportedException($"SelectedMixFunction value '{mixFn}' is not supported; only 1 (max) is supported.");
                }

                bool allowMissing = false;
                if (s.TryGetProperty("AllowMissingSensor", out var amProp))
                {
                    allowMissing = amProp.GetBoolean();
                }

                var sources = new List<string>();
                if (s.TryGetProperty("SelectedSensors", out var selSensorsProp) &&
                    selSensorsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var srcItem in selSensorsProp.EnumerateArray())
                    {
                        string srcId = srcItem.GetProperty("Identifier").GetString() ?? "";
                        sources.Add(MapIdentifier(srcId, mixKeyById));
                    }
                }

                config.Sensors[key] = new SensorConfig
                {
                    Type = "mix",
                    Fn = "max",
                    Sources = sources,
                    AllowMissing = allowMissing
                };
            }
        }

        // 2. Process FanCurves
        if (fcElement.TryGetProperty("FanCurves", out var fanCurvesElement) &&
            fanCurvesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in fanCurvesElement.EnumerateArray())
            {
                string name = c.GetProperty("Name").GetString() ?? "";
                string key = name.ToLowerInvariant();

                if (c.TryGetProperty("Points", out var pointsProp) && pointsProp.ValueKind == JsonValueKind.Array)
                {
                    // Graph curve
                    string sourceId = "";
                    if (c.TryGetProperty("SelectedTempSource", out var srcProp) &&
                        srcProp.TryGetProperty("Identifier", out var idProp))
                    {
                        sourceId = MapIdentifier(idProp.GetString() ?? "", mixKeyById);
                    }

                    var points = new List<List<float>>();
                    foreach (var ptElem in pointsProp.EnumerateArray())
                    {
                        string ptStr = ptElem.GetString() ?? "";
                        var parts = ptStr.Split(',');
                        float temp = float.Parse(parts[0], CultureInfo.InvariantCulture);
                        float pct = float.Parse(parts[1], CultureInfo.InvariantCulture);
                        points.Add([temp, pct]);
                    }

                    float hystUp = 0f, hystDown = 0f, respUp = 0f, respDown = 0f;
                    bool ignoreLimits = false;

                    if (c.TryGetProperty("HysteresisConfig", out var hProp))
                    {
                        if (hProp.TryGetProperty("HysteresisValueUp", out var hUp)) hystUp = (float)hUp.GetDouble();
                        if (hProp.TryGetProperty("HysteresisValueDown", out var hDown)) hystDown = (float)hDown.GetDouble();
                        if (hProp.TryGetProperty("ResponseTimeUp", out var rUp)) respUp = (float)rUp.GetDouble();
                        if (hProp.TryGetProperty("ResponseTimeDown", out var rDown)) respDown = (float)rDown.GetDouble();
                        if (hProp.TryGetProperty("IgnoreHysteresisAtLimits", out var ign)) ignoreLimits = ign.GetBoolean();
                    }

                    config.Curves[key] = new CurveConfig
                    {
                        Type = "graph",
                        Source = sourceId,
                        Points = points,
                        HysteresisUp = hystUp,
                        HysteresisDown = hystDown,
                        ResponseUpS = respUp,
                        ResponseDownS = respDown,
                        IgnoreHysteresisAtLimits = ignoreLimits
                    };
                }
                else if (c.TryGetProperty("IdleTemperature", out var idleProp))
                {
                    // Auto curve
                    string sourceId = "";
                    if (c.TryGetProperty("SelectedTempSource", out var srcProp) &&
                        srcProp.TryGetProperty("Identifier", out var idProp))
                    {
                        sourceId = MapIdentifier(idProp.GetString() ?? "", mixKeyById);
                    }

                    float idleTemp = (float)idleProp.GetDouble();
                    float loadTemp = (float)c.GetProperty("LoadTemperature").GetDouble();
                    float minPct = (float)c.GetProperty("MinFanSpeed").GetDouble();
                    float maxPct = (float)c.GetProperty("MaxFanSpeed").GetDouble();
                    float step = (float)c.GetProperty("Step").GetDouble();
                    float deadband = (float)c.GetProperty("Deadband").GetDouble();
                    float responseS = (float)c.GetProperty("SelectedResponseTime").GetDouble();

                    config.Curves[key] = new CurveConfig
                    {
                        Type = "auto",
                        Source = sourceId,
                        IdleTemp = idleTemp,
                        LoadTemp = loadTemp,
                        MinPct = minPct,
                        MaxPct = maxPct,
                        Step = step,
                        Deadband = deadband,
                        ResponseS = responseS
                    };
                }
                else if (c.TryGetProperty("Percent", out var pctProp))
                {
                    // Flat curve
                    float pct = (float)pctProp.GetDouble();
                    config.Curves[key] = new CurveConfig
                    {
                        Type = "flat",
                        Pct = pct
                    };
                }
            }
        }

        // 3. Process Controls (fans)
        if (fcElement.TryGetProperty("Controls", out var controlsElement) &&
            controlsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ctrl in controlsElement.EnumerateArray())
            {
                bool enabled = false;
                if (ctrl.TryGetProperty("Enable", out var enableProp))
                {
                    enabled = enableProp.GetBoolean();
                }

                if (!enabled)
                {
                    continue;
                }

                if (!ctrl.TryGetProperty("SelectedFanCurve", out var curveProp) ||
                    curveProp.ValueKind != JsonValueKind.Object ||
                    !curveProp.TryGetProperty("Name", out var curveNameProp))
                {
                    continue;
                }

                string curveName = curveNameProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(curveName))
                {
                    continue;
                }

                string nickName = ctrl.GetProperty("NickName").GetString() ?? "";
                string fanKey = Regex.Replace(nickName.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

                string controlId = ctrl.GetProperty("Identifier").GetString() ?? "";

                string? tachId = null;
                if (ctrl.TryGetProperty("PairedFanSensor", out var pairedProp) &&
                    pairedProp.ValueKind == JsonValueKind.Object &&
                    pairedProp.TryGetProperty("Identifier", out var tachProp))
                {
                    tachId = tachProp.GetString();
                }

                float stepUp = 8f;
                if (ctrl.TryGetProperty("SelectedCommandStepUp", out var suProp)) stepUp = (float)suProp.GetDouble();

                float stepDown = 8f;
                if (ctrl.TryGetProperty("SelectedCommandStepDown", out var sdProp)) stepDown = (float)sdProp.GetDouble();

                float startPct = 0f;
                if (ctrl.TryGetProperty("SelectedStart", out var startProp)) startPct = (float)startProp.GetDouble();

                float minPct = 0f;
                if (ctrl.TryGetProperty("MinimumPercent", out var minProp)) minPct = (float)minProp.GetDouble();

                float offset = 0f;
                if (ctrl.TryGetProperty("SelectedOffset", out var offProp)) offset = (float)offProp.GetDouble();

                config.Fans[fanKey] = new FanConfig
                {
                    Control = controlId,
                    Tach = tachId,
                    Curve = curveName.ToLowerInvariant(),
                    Enabled = true,
                    StepUp = stepUp,
                    StepDown = stepDown,
                    StartPct = startPct,
                    MinPct = minPct,
                    FloorPct = 0f,
                    Offset = offset
                };
            }
        }

        return config;
    }

    public static void ImportFile(string inputPath, string outputPath)
    {
        string json = File.ReadAllText(inputPath);
        Config config = Import(json);
        string outputJson = JsonSerializer.Serialize(config, ConfigLoader.JsonOptions);
        File.WriteAllText(outputPath, outputJson);
    }

    private static string MapIdentifier(string id, Dictionary<string, string> mixKeyById)
    {
        if (mixKeyById.TryGetValue(id, out var mixKey))
        {
            return mixKey;
        }

        if (id.StartsWith("ADLX/", StringComparison.OrdinalIgnoreCase))
        {
            if (id.EndsWith("/temp/GPU", StringComparison.OrdinalIgnoreCase))
            {
                return "sel:GpuAmd|Temperature|GPU Core";
            }
            throw new NotSupportedException($"ADLX sensor '{id}' is not supported.");
        }

        return id;
    }
}
