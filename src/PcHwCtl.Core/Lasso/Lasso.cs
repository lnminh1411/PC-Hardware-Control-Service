using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PcHwCtl.Core.Lasso;

/// <summary>
/// Process Lasso control for the board: affinity rules in prolasso.ini (applied to running processes at once) and
/// whole-config profiles to save and load. Policy such as night mode lives on the board; see
/// docs/protocols/process-lasso.md.
/// </summary>
public sealed class LassoConfig
{
    public bool Enabled { get; set; } = true;
    public string Ini { get; set; } = @"C:\ProgramData\ProcessLasso\config\prolasso.ini";

    /// <summary>Where `lasso save|load &lt;name&gt;` keep profiles, as &lt;name&gt;.ini.</summary>
    public string ProfilesDir { get; set; } = @"C:\ProgramData\PcHwCtl\lasso";

    /// <summary>How often `powercfg /requests` is read for display-required power requests (a video playing).</summary>
    public int DisplayRequestPollS { get; set; } = 10;
}

public sealed class LassoException(string message) : Exception(message);

/// <param name="Changed">Running processes whose affinity changed.</param>
public sealed record LassoResult(string Message, int Changed);

/// <param name="DisplayRequest">1 while some process holds a display-required power request, 0 none, -1 unknown.</param>
public sealed record LassoState(int Rules, int DisplayRequest, string DisplayRequestBy);

public interface ILassoControl
{
    LassoState State { get; }

    /// <summary>The ini's DefaultAffinitiesEx rules as written.</summary>
    IReadOnlyList<(string Name, string Cpus)> Rules();

    /// <exception cref="LassoException">Bad input or no ini.</exception>
    LassoResult SetAffinity(string exe, string cpus);

    LassoResult ClearAffinity(string exe);
    LassoResult Load(string profile);
    LassoResult Save(string profile);
    IReadOnlyList<string> Profiles();
}

/// <summary>prolasso.ini text handling. The file is UTF-16 LE with a BOM.</summary>
public static partial class LassoIni
{
    public static readonly Encoding FileEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    [GeneratedRegex(@"^DefaultAffinitiesEx=.*?(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex AffinitiesLine();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex ProfileChars();

    [GeneratedRegex(@"^\d+(-\d+)?(;\d+(-\d+)?)*$")]
    private static partial Regex CpuList();

    /// <summary>"12-17" or "2-5,10-17" (also ';'-separated, as the ini writes it) -> one bit per core.</summary>
    public static long CoreMask(string cores)
    {
        long mask = 0;
        foreach (string part in cores.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ab = part.Split('-');
            if (!int.TryParse(ab[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)) continue;
            if (ab.Length < 2 || !int.TryParse(ab[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b)) b = a;
            for (int i = a; i <= b && i < 63; i++) mask |= 1L << i;
        }
        return mask;
    }

    /// <summary>"10-15" or "2-5,10-17" -> the ini's form "2-5;10-17". Rules are comma-separated, so ',' cannot stay.</summary>
    public static string NormalizeCpus(string cpus, int processorCount)
    {
        string n = cpus.Trim().Replace(',', ';').Replace(" ", "");
        if (!CpuList().IsMatch(n)) throw new LassoException($"CPU list '{cpus}' not understood; use e.g. 10-15 or 2-5,10-17.");
        long mask = CoreMask(n);
        long all = processorCount >= 63 ? long.MaxValue : (1L << processorCount) - 1;
        if (mask == 0 || (mask & ~all) != 0) throw new LassoException($"CPU list '{cpus}' is outside 0-{processorCount - 1}.");
        return n;
    }

    /// <summary>"brave" or "Brave.exe" -> "brave.exe". No paths and no separators the ini uses.</summary>
    public static string ExeName(string exe)
    {
        string n = exe.Trim().Trim('"');
        if (n.Length == 0 || n.IndexOfAny([',', ';', '\\', '/', '=', '(']) >= 0)
        {
            throw new LassoException($"'{exe}' is not a process name; use e.g. brave.exe.");
        }
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        return n.ToLowerInvariant();
    }

    /// <summary>Profile names only, never paths: "night" or "night.ini" -> "night.ini".</summary>
    public static string ProfileFile(string profile)
    {
        string n = profile.Trim();
        if (n.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (!ProfileChars().IsMatch(n) || n.Contains("..", StringComparison.Ordinal))
        {
            throw new LassoException($"'{profile}' is not a profile name; letters, digits, _ . - only.");
        }
        return n + ".ini";
    }

    public static string? Value(string text, string key)
    {
        var m = Regex.Match(text, "^" + Regex.Escape(key) + "=(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.TrimEnd('\r') : null;
    }

    /// <summary>DefaultAffinitiesEx: "name,0,cpus,name,0,cpus,..." -> rules as written.</summary>
    public static List<(string Name, string Flag, string Cpus)> ParseAffinities(string? value)
    {
        var rules = new List<(string, string, string)>();
        if (string.IsNullOrEmpty(value)) return rules;
        string[] v = value.Split(',');
        for (int i = 0; i + 2 < v.Length; i += 3) rules.Add((v[i], v[i + 1], v[i + 2]));
        return rules;
    }

    /// <summary>"svchost.exe (netprofm -p)" -> "svchost"; rules with " (" target one service instance.</summary>
    public static string RuleExe(string ruleName)
    {
        string n = ruleName;
        int sp = n.IndexOf(" (", StringComparison.Ordinal);
        if (sp > 0) n = n[..sp];
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.Trim();
    }

    private static bool IsInstanceRule(string ruleName) => ruleName.Contains(" (", StringComparison.Ordinal);

    /// <summary>Sets the whole-exe rule for exe (as from ExeName): replaced in place, else appended. Instance rules stay.</summary>
    public static string SetRule(string text, string exe, string cpus)
    {
        var rules = ParseAffinities(Value(text, "DefaultAffinitiesEx"));
        string key = RuleExe(exe);
        int at = rules.FindIndex(r => !IsInstanceRule(r.Name) && RuleExe(r.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) rules[at] = (rules[at].Name, rules[at].Flag, cpus);
        else rules.Add((exe, "0", cpus));
        return WriteRules(text, rules);
    }

    /// <summary>Drops the whole-exe rule for exe; Removed false when there was none.</summary>
    public static (string Text, bool Removed) RemoveRule(string text, string exe)
    {
        var rules = ParseAffinities(Value(text, "DefaultAffinitiesEx"));
        string key = RuleExe(exe);
        int n = rules.RemoveAll(r => !IsInstanceRule(r.Name) && RuleExe(r.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        return n == 0 ? (text, false) : (WriteRules(text, rules), true);
    }

    /// <summary>Replaces the DefaultAffinitiesEx line, or adds one after the first [section] header (else at the end).</summary>
    private static string WriteRules(string text, List<(string Name, string Flag, string Cpus)> rules)
    {
        string line = "DefaultAffinitiesEx=" + string.Join(",", rules.Select(r => $"{r.Name},{r.Flag},{r.Cpus}"));
        if (AffinitiesLine().IsMatch(text)) return AffinitiesLine().Replace(text, line.Replace("$", "$$"), 1);

        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var header = Regex.Match(text, @"^\[[^\]]+\]\r?$", RegexOptions.Multiline);
        if (!header.Success) return text + (text.Length == 0 || text.EndsWith('\n') ? "" : nl) + line + nl;
        int end = header.Index + header.Length;
        if (end < text.Length && text[end] == '\n') end++;
        else
        {
            text = text.Insert(end, nl);
            end += nl.Length;
        }
        return text.Insert(end, line + nl);
    }

    /// <summary>Whole-exe rules as masks, keyed by name without .exe.</summary>
    public static Dictionary<string, long> RuleMasks(string text)
    {
        var masks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, cpus) in ParseAffinities(Value(text, "DefaultAffinitiesEx")))
        {
            if (!IsInstanceRule(name)) masks[RuleExe(name)] = CoreMask(cpus);
        }
        return masks;
    }
}
