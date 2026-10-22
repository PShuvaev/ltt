/// <summary>One line of a content search result.</summary>
public sealed record SearchHit(
    string FilePath,
    string RelativePath,
    int LineNumber,          // 1-based
    string LineText,
    int Score,
    int MatchStart,
    int MatchLength);

/// <summary>Search results, best first; Truncated — more lines matched than were kept;
/// SkippedLarge — files left out of a folder search for their size.</summary>
public sealed record SearchResult(List<SearchHit> Hits, bool Truncated, int SkippedLarge = 0)
{
    public static SearchResult Empty => new(new List<SearchHit>(), false);
}

/// <summary>
/// Fuzzy search over the contents of text files, recursively. Every word of
/// the query must match somewhere in the line, its characters in order but not
/// necessarily adjacent: "mngr" finds "manager". An exact substring scores
/// highest; otherwise compact matches and word starts rank first. In a git
/// repository the files are the ones git lists (tracked and untracked, minus
/// .gitignore; nested repositories by their own git); elsewhere folders are walked.
/// </summary>
public static class FuzzySearch
{
    // Used only when git cannot list the files (no repository): folders never worth searching.
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv", ".tox", ".idea", ".vs",
    };

    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",".dll",".so",".dylib",".png",".jpg",".jpeg",".gif",".ico",".bmp",".pdf",
        ".zip",".gz",".tar",".7z",".rar",".mp3",".mp4",".mov",".avi",".woff",".woff2",
        ".ttf",".otf",".class",".jar",".nupkg",".bin",".dat",".db",".sqlite",
    };

    /// <summary>Folder searches skip files bigger than this (dumps, minified bundles); one file is searched whole.</summary>
    private const long MaxFileSize = 8_000_000;

    /// <summary>
    /// A fuzzy match may skip at most this many characters. The limit does not
    /// grow with the token, so one more typed character never matches a line
    /// the shorter query did not (refining results relies on that).
    /// </summary>
    private const int MaxGaps = 10;

    /// <summary>Score of one query token against a line; -1 means no match.</summary>
    public static int ScoreToken(string token, string text, out int matchStart, out int matchLength) =>
        ScoreToken(token.AsSpan(), text.AsSpan(), out matchStart, out matchLength);

    private static bool AtWordStart(ReadOnlySpan<char> text, int i) => i == 0 || !char.IsLetterOrDigit(text[i - 1]);

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
            return 1000 - Math.Min(exact, 100) + (AtWordStart(text, exact) ? 50 : 0);
        }

        // Is the token a subsequence at all? (One pass — most lines fail here.)
        int ti = 0;
        for (int si = 0; si < text.Length && ti < token.Length; si++)
            if (char.ToLowerInvariant(text[si]) == char.ToLowerInvariant(token[ti])) ti++;
        if (ti < token.Length) return -1;

        // The most compact window, tried from each occurrence of the first
        // character; characters spread wider than MaxGaps are noise, not a match.
        // A try looks at no more than MaxGaps + token.Length characters, so even
        // a long line full of the first character costs a bounded pass per start.
        int best = -1;
        char first = char.ToLowerInvariant(token[0]);
        for (int s = 0; s < text.Length; s++)
        {
            if (char.ToLowerInvariant(text[s]) != first) continue;
            int k = 1, last = s, consecutive = 0, boundary = AtWordStart(text, s) ? 15 : 0;
            for (int si = s + 1; si < text.Length && k < token.Length && si - s - k <= MaxGaps; si++)
            {
                if (char.ToLowerInvariant(text[si]) != char.ToLowerInvariant(token[k])) continue;
                if (si == last + 1) consecutive += 10;
                if (AtWordStart(text, si)) boundary += 15;
                last = si;
                k++;
            }
            if (k < token.Length) continue;
            int len = last - s + 1;
            int score = 500 - (len - token.Length) * 6 - Math.Min(s, 80) + consecutive + boundary;
            if (score > best)
            {
                best = score;
                matchStart = s;
                matchLength = len;
            }
        }
        return best < 0 ? -1 : Math.Max(best, 1);
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

    // ---- File lists ----

    // `git ls-files` per folder, reused for a few seconds (every keystroke may need a fresh scan).
    private static readonly Dictionary<string, (long At, List<string> Files)> FileLists = new(Paths.Comparer);
    private const long FileListTtlMs = 10_000;
    private static int _fileListGeneration; // a listing started before an invalidation is not kept

    /// <summary>Forget cached file lists (files were created, deleted or renamed).</summary>
    public static void InvalidateFileLists()
    {
        lock (FileLists)
        {
            FileLists.Clear();
            _fileListGeneration++;
        }
    }

    /// <summary>
    /// The files to search under root, as git lists them, else walked. A
    /// listed folder is a nested repository or a submodule (see SearchMany).
    /// A folder git ignores lists nothing at all — it is walked as well.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        List<string>? files;
        int generation;
        lock (FileLists)
        {
            generation = _fileListGeneration;
            files = FileLists.TryGetValue(root, out var cached) && Environment.TickCount64 - cached.At < FileListTtlMs
                ? cached.Files : null;
        }
        if (files == null)
        {
            string? listed = Proc.Run("git", "ls-files -z --cached --others --exclude-standard", root, timeoutMs: 15_000, ct: ct);
            if (listed is { Length: > 0 })
            {
                files = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal) // a conflicted file is listed once per stage
                    .Select(rel => Path.Combine(root, rel.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)))
                    .ToList();
                lock (FileLists)
                    if (generation == _fileListGeneration) FileLists[root] = (Environment.TickCount64, files);
            }
        }
        return files ?? WalkFiles(root);
    }

    /// <summary>
    /// All files under root outside IgnoredDirs; symlinked folders are not
    /// followed. A repository met on the way is yielded as a folder, for git to list.
    /// </summary>
    private static IEnumerable<string> WalkFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            if (dir != root && entries.Any(e => e.Name == ".git")) { yield return dir; continue; }
            foreach (var f in entries.OfType<FileInfo>().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                yield return f.FullName;
            foreach (var d in entries.OfType<DirectoryInfo>().OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase))
                if (!IgnoredDirs.Contains(d.Name) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                    stack.Push(d.FullName);
        }
    }

    // ---- Ranking ----

    /// <summary>Best first: higher score, then path, then line.</summary>
    private static int RankCompare(SearchHit a, SearchHit b)
    {
        int c = b.Score.CompareTo(a.Score);
        if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        if (c == 0) c = a.LineNumber.CompareTo(b.LineNumber);
        return c;
    }

    private sealed class WorstFirst : IComparer<SearchHit>
    {
        public static readonly WorstFirst Instance = new();
        public int Compare(SearchHit? a, SearchHit? b) => RankCompare(b!, a!);
    }

    /// <summary>Keeps the best `capacity` hits of however many are offered.</summary>
    private sealed class TopHits(int capacity)
    {
        private readonly PriorityQueue<SearchHit, SearchHit> _heap = new(WorstFirst.Instance);
        private int _offered;

        public void Offer(SearchHit h)
        {
            _offered++;
            if (_heap.Count < capacity) _heap.Enqueue(h, h);
            else if (RankCompare(h, _heap.Peek()) < 0) _heap.EnqueueDequeue(h, h);
        }

        /// <summary>Take back the `offered` hits of one file (it is being read again in another encoding).</summary>
        public void Forget(string file, int offered)
        {
            var keep = _heap.UnorderedItems.Select(x => x.Element).Where(h => h.FilePath != file).ToList();
            _heap.Clear();
            foreach (var h in keep) _heap.Enqueue(h, h);
            _offered -= offered;
        }

        public SearchResult Result()
        {
            var list = _heap.UnorderedItems.Select(x => x.Element).ToList();
            list.Sort(RankCompare);
            return new SearchResult(list, _offered > capacity);
        }
    }

    private static List<SearchHit> Rank(List<SearchHit> hits)
    {
        hits.Sort(RankCompare);
        return hits;
    }

    // ---- Searching ----

    /// <summary>
    /// Refine previous results with a grown query, without reading files again.
    /// Valid only when the old query is a prefix of the new one and the old
    /// results were complete: then every new match was an old one too.
    /// </summary>
    public static List<SearchHit> Refine(IReadOnlyList<SearchHit> previous, string query) =>
        Rank(RefineInOrder(previous, query));

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
    public static SearchResult SearchLines(string filePath, string relativePath, IEnumerable<string> lines,
        string query, int maxResults = 500, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return new SearchResult(hits, false);

        int lineNo = 0;
        foreach (var line in lines)
        {
            if ((++lineNo & 0x3FF) == 0 && ct.IsCancellationRequested) break;
            if (line.Contains('\0')) break;
            int score = ScoreQuery(query, line, out int st, out int len);
            if (score < 0) continue;
            if (hits.Count >= maxResults) return new SearchResult(hits, true);
            hits.Add(new SearchHit(filePath, relativePath, lineNo, line, score, st, len));
        }
        return new SearchResult(hits, false);
    }

    /// <summary>Stream-search a single file from disk in document order (whatever its size).</summary>
    public static SearchResult SearchFile(string filePath, string relativePath, string query, int maxResults = 500,
        CancellationToken ct = default)
    {
        string full = Path.GetFullPath(filePath);
        try
        {
            // A FIFO or a device reports no length and would block: only real content is read.
            if (new FileInfo(full).Length == 0) return SearchResult.Empty;
            try
            {
                return SearchLines(full, relativePath, TextFile.ReadLines(full, latin1: false), query, maxResults, ct);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return SearchLines(full, relativePath, TextFile.ReadLines(full, latin1: true), query, maxResults, ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SearchResult.Empty; }
    }

    /// <summary>
    /// Search the panel's roots. A root inside another is searched too, for what
    /// the outer listing leaves out (a nested repository, an ignored folder); no
    /// file is searched twice. With several roots, hits are prefixed with the
    /// root name ("projA/src/x.cs"). Dirty tabs are searched in their buffers
    /// (path → lines). A cancelled search returns what it found so far.
    /// </summary>
    public static SearchResult SearchMany(IEnumerable<string> roots, string query, int maxResults = 500,
        CancellationToken ct = default, IReadOnlyDictionary<string, string[]>? buffers = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return SearchResult.Empty;
        var all = roots.Select(Paths.Normalize).Distinct(Paths.Comparer).OrderBy(r => r.Length).ToList();
        var outer = all.Where(r => !all.Any(o => Paths.IsInside(r, o))).ToList();
        var top = new TopHits(maxResults);
        var seen = new HashSet<string>(Paths.Comparer);
        int skipped = 0;
        foreach (var root in all)
        {
            string home = outer.First(o => Paths.IsSameOrInside(root, o));
            string prefix = outer.Count > 1
                ? (Path.GetFileName(home) is { Length: > 0 } n ? n : home) + Path.DirectorySeparatorChar
                : "";
            var folders = new Stack<string>();
            folders.Push(root);
            while (folders.Count > 0 && !ct.IsCancellationRequested)
                foreach (var file in EnumerateFiles(folders.Pop(), ct))
                {
                    if (ct.IsCancellationRequested) break;
                    if (!seen.Add(file)) continue;
                    string rel = prefix + Path.GetRelativePath(home, file);
                    if (buffers != null && buffers.TryGetValue(file, out var lines))
                        ScanLines(file, rel, lines, query, top.Offer, ct);
                    else switch (ScanFile(file, rel, query, top, ct))
                    {
                        case Scan.TooLarge: skipped++; break;
                        case Scan.Folder: folders.Push(file); break; // a repository inside: listed by its own git
                    }
                }
        }
        return top.Result() with { SkippedLarge = skipped };
    }

    /// <summary>Search a whole directory recursively.</summary>
    public static SearchResult Search(string root, string query, int maxResults = 500, CancellationToken ct = default,
        IReadOnlyDictionary<string, string[]>? buffers = null) =>
        SearchMany(new[] { root }, query, maxResults, ct, buffers);

    private enum Scan { Done, TooLarge, Folder }

    /// <summary>
    /// Stream one searchable file line by line (never loading it whole), stopping
    /// at the first binary-looking line. Binary extensions, empty files (and so
    /// FIFOs and devices) are skipped, as are files over the size limit
    /// (TooLarge). Folder — a folder that is not a symlink, to be listed itself.
    /// </summary>
    private static Scan ScanFile(string file, string rel, string query, TopHits top, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return new DirectoryInfo(file) is { Exists: true } d && (d.Attributes & FileAttributes.ReparsePoint) == 0
                    ? Scan.Folder : Scan.Done;
            if (BinaryExt.Contains(info.Extension)) return Scan.Done;
            if (info.Length > MaxFileSize) return Scan.TooLarge;
            if (info.Length == 0) return Scan.Done;
            int offered = 0;
            try
            {
                ScanLines(file, rel, TextFile.ReadLines(file, latin1: false), query, h => { offered++; top.Offer(h); }, ct);
            }
            catch (System.Text.DecoderFallbackException)
            {
                // Not UTF-8: as the editor does, read every byte as one Latin-1 character.
                top.Forget(file, offered);
                ScanLines(file, rel, TextFile.ReadLines(file, latin1: true), query, top.Offer, ct);
            }
        }
        catch (IOException) { /* unreadable or vanished — skip it */ }
        catch (UnauthorizedAccessException) { /* same */ }
        return Scan.Done;
    }

    private static void ScanLines(string file, string rel, IEnumerable<string> lines, string query,
        Action<SearchHit> offer, CancellationToken ct)
    {
        int lineNo = 0;
        foreach (var line in lines)
        {
            if ((++lineNo & 0x3FF) == 0 && ct.IsCancellationRequested) return;
            if (line.Contains('\0')) break; // binary file — stop looking further
            int score = ScoreQuery(query, line, out int st, out int len);
            if (score >= 0) offer(new SearchHit(file, rel, lineNo, line, score, st, len));
        }
    }
}
