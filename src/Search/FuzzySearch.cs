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
/// recursively. A multi-word query: every word must fuzzy-match
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
    public static int ScoreToken(string token, string text, out int matchStart, out int matchLength) =>
        ScoreToken(token.AsSpan(), text.AsSpan(), out matchStart, out matchLength);

    /// <summary>Span core: case-insensitive on the fly, so scoring a line allocates nothing.</summary>
    private static int ScoreToken(ReadOnlySpan<char> token, ReadOnlySpan<char> text,
        out int matchStart, out int matchLength)
    {
        matchStart = 0;
        matchLength = 0;
        if (token.Length == 0 || text.Length == 0) return -1;

        // Exact substring — the best case.
        int exact = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0)
        {
            matchStart = exact;
            matchLength = token.Length;
            bool wordStart = exact == 0 || !char.IsLetterOrDigit(text[exact - 1]);
            return 1000 - Math.Min(exact, 100) + (wordStart ? 50 : 0);
        }

        // Subsequence: all query characters in order.
        int ti = 0, first = -1, last = -1, prev = -2;
        int consecutiveBonus = 0, boundaryBonus = 0;
        char need = char.ToLowerInvariant(token[0]);
        for (int si = 0; si < text.Length && ti < token.Length; si++)
        {
            if (char.ToLowerInvariant(text[si]) != need) continue;
            if (first < 0) first = si;
            if (si == prev + 1) consecutiveBonus += 10;
            if (si == 0 || !char.IsLetterOrDigit(text[si - 1])) boundaryBonus += 15;
            prev = si;
            last = si;
            if (++ti < token.Length) need = char.ToLowerInvariant(token[ti]);
        }
        if (ti < token.Length) return -1; // not all characters were found

        matchStart = first;
        matchLength = last - first + 1;
        int gaps = matchLength - token.Length;
        int score = 500 - gaps * 6 - Math.Min(first, 80) + consecutiveBonus + boundaryBonus;
        return Math.Max(score, 1);
    }

    /// <summary>Score of the full query (all words must match) or -1.</summary>
    public static int ScoreQuery(string query, string text, out int matchStart, out int matchLength)
    {
        matchStart = int.MaxValue;
        int matchEnd = 0, total = 0, tokens = 0;
        var textSpan = text.AsSpan();
        var rest = query.AsSpan();
        while (true)
        {
            int sp = rest.IndexOf(' ');
            var token = (sp < 0 ? rest : rest[..sp]).Trim();
            if (!token.IsEmpty)
            {
                tokens++;
                int sc = ScoreToken(token, textSpan, out int st, out int len);
                if (sc < 0) { matchStart = 0; matchLength = 0; return -1; }
                total += sc;
                matchStart = Math.Min(matchStart, st);
                matchEnd = Math.Max(matchEnd, st + len);
            }
            if (sp < 0) break;
            rest = rest[(sp + 1)..];
        }
        if (tokens == 0) { matchStart = 0; matchLength = 0; return -1; }
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

    /// <summary>Like <see cref="Refine"/>, but preserves the existing (document) order.</summary>
    public static List<SearchHit> RefineInOrder(IReadOnlyList<SearchHit> previous, string query)
    {
        var result = new List<SearchHit>();
        foreach (var h in previous)
        {
            int score = ScoreQuery(query, h.LineText, out int st, out int len);
            if (score >= 0)
                result.Add(h with { Score = score, MatchStart = st, MatchLength = len });
        }
        return result;
    }

    /// <summary>
    /// Search supplied lines in document order (used for an open editor buffer,
    /// so Ctrl+F sees unsaved edits). Stops at a binary-looking line or maxResults.
    /// </summary>
    public static List<SearchHit> SearchLines(
        string filePath, string relativePath, IEnumerable<string> lines, string query, int maxResults = 500)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        int lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            if (line.Contains('\0')) break;
            int score = ScoreQuery(query, line, out int st, out int len);
            if (score < 0) continue;
            hits.Add(new SearchHit(filePath, relativePath, lineNo, line, score, st, len));
            if (hits.Count >= maxResults) break;
        }
        return hits;
    }

    /// <summary>Stream-search a single file from disk in document order.</summary>
    public static List<SearchHit> SearchFile(
        string filePath, string relativePath, string query, int maxResults = 500)
    {
        if (BinaryExt.Contains(Path.GetExtension(filePath))) return new List<SearchHit>();
        string full = Path.GetFullPath(filePath);
        try
        {
            if (new FileInfo(full).Length > MaxFileSize) return new List<SearchHit>();
            return SearchLines(
                full,
                relativePath,
                File.ReadLines(full, System.Text.Encoding.UTF8),
                query,
                maxResults);
        }
        catch (IOException) { return new List<SearchHit>(); }
        catch (UnauthorizedAccessException) { return new List<SearchHit>(); }
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
            ScanFile(file, Path.GetRelativePath(root, file), query, hits, hardCap, onProgress);
            if (hits.Count >= hardCap) break;
        }

        return Rank(hits).Take(maxResults).ToList();
    }

    /// <summary>
    /// Stream one searchable file line by line (never loading it whole), stopping
    /// at the first binary-looking line or when the hard cap is reached.
    /// Binary extensions and files over the size limit are skipped.
    /// </summary>
    private static void ScanFile(string file, string rel, string query,
        List<SearchHit> hits, int hardCap, Action<string>? onProgress)
    {
        if (BinaryExt.Contains(Path.GetExtension(file))) return;
        try
        {
            if (new FileInfo(file).Length > MaxFileSize) return;
            onProgress?.Invoke(file);
            int lineNo = 0;
            foreach (var line in File.ReadLines(file, System.Text.Encoding.UTF8))
            {
                lineNo++;
                if (line.Contains('\0')) break; // binary file — stop looking further
                int score = ScoreQuery(query, line, out int st, out int len);
                if (score >= 0)
                    hits.Add(new SearchHit(file, rel, lineNo, line, score, st, len));
                if (hits.Count >= hardCap) break;
            }
        }
        catch (IOException) { /* unreadable file — skip it */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
