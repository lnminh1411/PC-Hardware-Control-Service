namespace PcHwCtl.Core;

public static class IdentifierMatcher
{
    public static string Normalize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";

        bool startsWithSlash = id.StartsWith('/');
        string[] parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);

        for (int i = 0; i < parts.Length; i++)
        {
            bool isPureDigits = parts[i].Length > 0 && parts[i].All(char.IsAsciiDigit);
            bool isFollowedByAnother = i < parts.Length - 1;

            if (isPureDigits && isFollowedByAnother)
            {
                continue;
            }

            kept.Add(parts[i].ToLowerInvariant());
        }

        string joined = string.Join('/', kept);
        return startsWithSlash ? "/" + joined : joined;
    }

    public static string? Match(string wanted, IEnumerable<string> available)
    {
        if (string.IsNullOrEmpty(wanted)) return null;

        var availableList = available as IList<string> ?? available.ToList();

        // 1. Exact match first (ordinal)
        foreach (var item in availableList)
        {
            if (string.Equals(item, wanted, StringComparison.Ordinal))
            {
                return item;
            }
        }

        // 1b. Exact match (case-insensitive)
        foreach (var item in availableList)
        {
            if (string.Equals(item, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        // 2. Unique normalized match
        string normWanted = Normalize(wanted);
        string? matched = null;
        int count = 0;

        foreach (var item in availableList)
        {
            if (string.Equals(Normalize(item), normWanted, StringComparison.OrdinalIgnoreCase))
            {
                matched = item;
                count++;
                if (count > 1)
                {
                    return null; // Ambiguous match
                }
            }
        }

        return count == 1 ? matched : null;
    }
}
