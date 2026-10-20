namespace ConsoleEditor;

/// <summary>Live path completion for the path prompts (Ctrl+O, Ctrl+N).</summary>
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

    /// <summary>
    /// Entries of the directory from the already-typed part whose names start
    /// with the typed fragment (folders first). Returns full candidate strings
    /// in the style of the typed prefix, plus ghost text.
    /// </summary>
    internal static (List<string> Candidates, string Ghost) Compute(string rawInput, string currentDir)
    {
        var empty = (new List<string>(), "");
        int sep = rawInput.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        string rawDir = sep >= 0 ? rawInput[..(sep + 1)] : "";
        string partial = sep >= 0 ? rawInput[(sep + 1)..] : rawInput;

        string lookupBase = ExpandHome(rawDir);
        string lookupDir = lookupBase.Length == 0
            ? currentDir
            : Path.IsPathRooted(lookupBase) ? lookupBase : Path.GetFullPath(Path.Combine(currentDir, lookupBase));
        if (!Directory.Exists(lookupDir)) return empty;

        List<string> candidates;
        try
        {
            candidates = new DirectoryInfo(lookupDir).EnumerateFileSystemInfos()
                .Where(e => e.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .Select(e => rawDir + e.Name + (e is DirectoryInfo ? Path.DirectorySeparatorChar.ToString() : ""))
                .ToList();
        }
        catch (IOException) { return empty; }
        catch (UnauthorizedAccessException) { return empty; }

        string ghost = "";
        if (candidates.Count > 0
            && candidates[0].Length > rawInput.Length
            && candidates[0].StartsWith(rawInput, StringComparison.OrdinalIgnoreCase))
            ghost = candidates[0][rawInput.Length..];
        return (candidates, ghost);
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
