namespace ConsoleEditor;

/// <summary>One line of a content search result.</summary>
public sealed record SearchHit(
    string FilePath,
    string RelativePath,
    int LineNumber,          // 1-based
    string LineText,
    int Score,
    int MatchStart,
    int MatchLength);

/// <summary>
/// Fuzzy search over the contents of all text files in a directory,
/// recursively (Shift+F). A multi-word query: every word must fuzzy-match
/// somewhere in the line. Fuzzy means the query characters appear in the
/// same order but not necessarily adjacent: "mngr" finds "manager".
/// An exact substring scores the maximum; beyond that, ranking is by
/// match compactness and hits at word starts.
/// </summary>
public static class FuzzySearch
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "__pycache__",
    };

    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",".dll",".so",".dylib",".png",".jpg",".jpeg",".gif",".ico",".bmp",".pdf",
        ".zip",".gz",".tar",".7z",".rar",".mp3",".mp4",".mov",".avi",".woff",".woff2",
        ".ttf",".otf",".class",".jar",".nupkg",".bin",".dat",".db",".sqlite",
    };

    private const long MaxFileSize = 2_000_000;

    /// <summary>Score of one query token against a line; -1 means no match.</summary>
    public static int ScoreToken(string token, string text, out int matchStart, out int matchLength)
    {
        matchStart = 0;
        matchLength = 0;
        if (token.Length == 0 || text.Length == 0) return -1;

        string t = token.ToLowerInvariant();
        string s = text.ToLowerInvariant();

        // Exact substring — the best case.
        int exact = s.IndexOf(t, StringComparison.Ordinal);
        if (exact >= 0)
        {
            matchStart = exact;
            matchLength = t.Length;
            bool wordStart = exact == 0 || !char.IsLetterOrDigit(s[exact - 1]);
            return 1000 - Math.Min(exact, 100) + (wordStart ? 50 : 0);
        }

        // Subsequence: all query characters in order.
        int ti = 0, first = -1, last = -1, prev = -2;
        int consecutiveBonus = 0, boundaryBonus = 0;
        for (int si = 0; si < s.Length && ti < t.Length; si++)
        {
            if (s[si] != t[ti]) continue;
            if (first < 0) first = si;
            if (si == prev + 1) consecutiveBonus += 10;
            if (si == 0 || !char.IsLetterOrDigit(s[si - 1])) boundaryBonus += 15;
            prev = si;
            last = si;
            ti++;
        }
        if (ti < t.Length) return -1; // not all characters were found

        matchStart = first;
        matchLength = last - first + 1;
        int gaps = matchLength - t.Length;
        int score = 500 - gaps * 6 - Math.Min(first, 80) + consecutiveBonus + boundaryBonus;
        return Math.Max(score, 1);
    }

    /// <summary>Score of the full query (all words must match) or -1.</summary>
    public static int ScoreQuery(string query, string text, out int matchStart, out int matchLength)
    {
        matchStart = int.MaxValue;
        int matchEnd = 0, total = 0;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) { matchStart = 0; matchLength = 0; return -1; }

        foreach (var token in tokens)
        {
            int sc = ScoreToken(token, text, out int st, out int len);
            if (sc < 0) { matchStart = 0; matchLength = 0; return -1; }
            total += sc;
            matchStart = Math.Min(matchStart, st);
            matchEnd = Math.Max(matchEnd, st + len);
        }
        matchLength = matchEnd - matchStart;
        return total;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                yield return f;
            foreach (var d in subdirs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                if (!IgnoredDirs.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }

    /// <summary>
    /// Refine previous results with a grown query without re-scanning the disk.
    /// Only valid when the new query is a prefix extension of the old one:
    /// then every match of the new query was also a match of the old.
    /// </summary>
    public static List<SearchHit> Refine(IReadOnlyList<SearchHit> previous, string query)
    {
        var result = new List<SearchHit>();
        foreach (var h in previous)
        {
            int score = ScoreQuery(query, h.LineText, out int st, out int len);
            if (score >= 0)
                result.Add(h with { Score = score, MatchStart = st, MatchLength = len });
        }
        return Rank(result);
    }

    private static List<SearchHit> Rank(List<SearchHit> hits) => hits
        .OrderByDescending(h => h.Score)
        .ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(h => h.LineNumber)
        .ToList();

    /// <summary>
    /// Search several left-panel roots at once. With multiple roots, a hit's
    /// relative path is prefixed with the root name: "projA/src/x.cs".
    /// </summary>
    public static List<SearchHit> SearchMany(IEnumerable<string> roots, string query, int maxResults = 500)
    {
        var rootList = roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rootList.Count <= 1)
            return rootList.Count == 1 ? Search(rootList[0], query, maxResults) : new List<SearchHit>();

        var all = new List<SearchHit>();
        foreach (var root in rootList)
        {
            string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Length == 0) name = root;
            foreach (var h in Search(root, query, maxResults))
                all.Add(h with { RelativePath = name + Path.DirectorySeparatorChar + h.RelativePath });
        }
        return Rank(all).Take(maxResults).ToList();
    }

    /// <summary>Search a whole directory recursively. onProgress — current file (for the status line).</summary>
    public static List<SearchHit> Search(string root, string query, int maxResults = 500,
        Action<string>? onProgress = null)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        int hardCap = maxResults * 10; // don't accumulate forever on queries like "a"

        foreach (var file in EnumerateFiles(root))
        {
            if (TryReadLines(file) is not { } lines) continue;
            onProgress?.Invoke(file);

            string rel = Path.GetRelativePath(root, file);
            for (int i = 0; i < lines.Length && hits.Count < hardCap; i++)
            {
                if (lines[i].Contains('\0')) break; // binary file — stop looking further
                int score = ScoreQuery(query, lines[i], out int st, out int len);
                if (score >= 0)
                    hits.Add(new SearchHit(file, rel, i + 1, lines[i], score, st, len));
            }
            if (hits.Count >= hardCap) break;
        }

        return Rank(hits).Take(maxResults).ToList();
    }

    /// <summary>Lines of a searchable text file, or null to skip it (binary, too big, unreadable).</summary>
    private static string[]? TryReadLines(string file)
    {
        if (BinaryExt.Contains(Path.GetExtension(file))) return null;
        try
        {
            if (new FileInfo(file).Length > MaxFileSize) return null;
            return File.ReadAllLines(file, System.Text.Encoding.UTF8);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
