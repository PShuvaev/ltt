using System.Text;

/// <summary>Live path completion for the path prompts (Ctrl+O, Ctrl+N, Ctrl+L).</summary>
internal static class PathCompletion
{
    internal static string ExpandHome(string p)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p == "~") return home;
        if (p.StartsWith("~/") || p.StartsWith("~\\"))
            return Path.Combine(home, p[2..]);
        return p;
    }

    internal static (List<string> Candidates, string Ghost) Compute(string rawInput, string currentDir) =>
        Compute(rawInput, currentDir, out _);

    /// <summary>
    /// Entries of the directory from the already-typed part whose names start
    /// with the typed fragment (folders first; é typed matches é however it is
    /// stored). Returns full candidate strings in the style of the typed prefix
    /// (the first 50) and the ghost text; common — what every match shares, Tab's completion.
    /// </summary>
    internal static (List<string> Candidates, string Ghost) Compute(string rawInput, string currentDir, out string common)
    {
        common = "";
        var empty = (new List<string>(), "");
        int sep = rawInput.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        string rawDir = sep >= 0 ? rawInput[..(sep + 1)] : "";
        string partial = sep >= 0 ? rawInput[(sep + 1)..] : rawInput;

        string lookupBase = ExpandHome(rawDir);
        string lookupDir = lookupBase.Length == 0
            ? currentDir
            : Path.IsPathRooted(lookupBase) ? lookupBase : Path.GetFullPath(Path.Combine(currentDir, lookupBase));
        if (!Directory.Exists(lookupDir)) return empty;

        List<string> matches;
        string typed = Composed(partial); // macOS often stores é as e and a mark
        try
        {
            matches = new DirectoryInfo(lookupDir).EnumerateFileSystemInfos()
                .Where(e => Composed(e.Name).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => rawDir + e.Name + (e is DirectoryInfo ? Path.DirectorySeparatorChar.ToString() : ""))
                .ToList();
        }
        catch (IOException) { return empty; }
        catch (UnauthorizedAccessException) { return empty; }

        string ghost = "";
        if (matches.Count > 0
            && matches[0].Length > rawInput.Length
            && matches[0].StartsWith(rawInput, StringComparison.OrdinalIgnoreCase))
            ghost = matches[0][rawInput.Length..];
        common = CommonPrefix(matches);
        return (matches.Count > 50 ? matches.GetRange(0, 50) : matches, ghost);
    }

    /// <summary>The text in composed form (é as one character); as it is when it cannot be (half an emoji typed so far).</summary>
    private static string Composed(string s)
    {
        try { return s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }

    internal static string CommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return "";
        string first = items[0];
        int len = first.Length;
        foreach (var s in items.Skip(1))
        {
            len = Math.Min(len, s.Length);
            int i = 0;
            while (i < len && char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(s[i])) i++;
            len = i;
            if (len == 0) break;
        }
        return first[..len];
    }
}
