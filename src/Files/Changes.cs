using System.Text;

/// <summary>
/// What changed, for the tree's colours and the editor's line marks. In a git
/// repository it is everything since the last commit (git status, a file's
/// committed text); elsewhere, what changed since ltt started (modification
/// times, the folders the watcher reported, a file's text as first opened).
/// git runs in the background; Poll applies its answers on the UI thread.
/// </summary>
internal sealed class Changes
{
    public enum Kind : byte { None, Added, Modified }

    private const long MaxCommittedBytes = 8 << 20; // bigger files are compared with their first-opened text

    private sealed class Repo(string root)
    {
        public readonly string Root = root; // as the tree spells it
        public string Head = "";            // the last commit; "" before the first one
        public Dictionary<string, Kind> Entries = new(Paths.Comparer); // changed files and the folders above them
        public List<string> NewDirs = new();                          // untracked folders: all inside is new
        public (DateTime, DateTime, DateTime)? GitStamp;               // its .git as last seen (see CheckGitDirs)
    }

    private sealed record Answer(string Folder, string? Root, string Head, Dictionary<string, Kind> Entries,
        List<string> NewDirs, (DateTime, DateTime, DateTime) Git = default);

    private readonly DateTime _since = DateTime.UtcNow;
    private long SinceNs => (_since - DateTime.UnixEpoch).Ticks * 100; // as inode change times go
    private readonly List<Repo> _repos = new();
    private readonly HashSet<string> _asked = new(Paths.Comparer);   // folders already looked up
    private readonly HashSet<string> _wanted = new(Paths.Comparer);  // to look up or re-read next
    private readonly HashSet<string> _touched = new(Paths.Comparer); // outside git: folders with changes inside
    private readonly List<string> _created = new();                  // made by ltt itself (all inside a folder too)
    private readonly Dictionary<string, string[]?> _committed = new(Paths.Comparer); // "head:path" → text
    private Task<List<Answer>>? _running;

    /// <summary>Find the repositories of these folders (each folder is looked up once).</summary>
    public void Discover(IEnumerable<string> folders)
    {
        foreach (string f in folders)
            if (_asked.Add(f)) _wanted.Add(f);
    }

    /// <summary>Something changed in these folders: re-read their repositories; outside one, remember the folders.</summary>
    public void Changed(IEnumerable<string> dirs, IReadOnlyList<string> panelRoots)
    {
        foreach (string d in dirs)
        {
            if (RepoFor(d) is { } repo) { _wanted.Add(repo.Root); continue; }
            if (Path.Exists(Path.Combine(d, ".git"))) _wanted.Add(d); // `git init` here: a repository from now on
            string? root = panelRoots.Where(r => Paths.IsSameOrInside(d, r)).MaxBy(r => r.Length);
            for (string? p = d; root != null && p != null && Paths.IsInside(p, root); p = Path.GetDirectoryName(p))
                _touched.Add(p);
        }
    }

    /// <summary>ltt made this file or folder (a paste, a new file): outside git it shows as new.</summary>
    public void Created(string path)
    {
        _created.Add(Paths.Normalize(path));
        _wanted.UnionWith(_repos.Where(r => Paths.IsInside(path, r.Root)).Select(r => r.Root));
    }

    private bool IsCreated(string path) => _created.Any(c => Paths.IsSameOrInside(path, c));

    /// <summary>Re-read every repository (after a file operation of ours).</summary>
    public void RefreshAll()
    {
        foreach (var r in _repos) _wanted.Add(r.Root);
    }

    /// <summary>
    /// A repository whose .git lies outside the panel's roots (a root inside a
    /// repository) gets no watcher events from it: look at its .git now and then,
    /// so an outside commit or checkout re-reads its status.
    /// </summary>
    public void CheckGitDirs(IReadOnlyList<string> panelRoots)
    {
        foreach (var r in _repos)
        {
            // A worktree's .git is a file: its own git folder lies elsewhere, beyond the watcher's reach.
            string git = GitDir(Path.Combine(r.Root, ".git"));
            if (panelRoots.Any(p => Paths.IsSameOrInside(git, p))) continue;
            var stamp = GitStamp(git);
            if (r.GitStamp == stamp) continue;
            r.GitStamp = stamp;
            _wanted.Add(r.Root);
        }
    }

    /// <summary>A repository's git folder: .git itself, or the one a worktree's (a submodule's) .git file points to.</summary>
    private static string GitDir(string dotGit)
    {
        try
        {
            if (File.Exists(dotGit) && File.ReadAllText(dotGit) is var link && link.StartsWith("gitdir:", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dotGit)!, link[7..].Trim()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return dotGit;
    }

    /// <summary>When a git folder, its index and HEAD last changed.</summary>
    private static (DateTime, DateTime, DateTime) GitStamp(string git)
    {
        try
        {
            return (Directory.GetLastWriteTimeUtc(git), File.GetLastWriteTimeUtc(Path.Combine(git, "index")),
                File.GetLastWriteTimeUtc(Path.Combine(git, "HEAD")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Apply finished git answers and start the next round; true when the marks may have changed.</summary>
    public bool Poll()
    {
        bool changed = false;
        if (_running is { IsCompleted: true } done)
        {
            _running = null;
            if (done.Status == TaskStatus.RanToCompletion)
                foreach (var a in done.Result)
                {
                    if (a.Root == null)
                    {
                        // A repository's own folder without .git any more: what is in it is plain files now.
                        if (!Path.Exists(Path.Combine(a.Folder, ".git"))
                            && _repos.RemoveAll(r => Paths.Same(r.Root, a.Folder)) > 0)
                            changed = true;
                        continue;
                    }
                    var repo = _repos.Find(r => Paths.Same(r.Root, a.Root));
                    if (repo == null) _repos.Add(repo = new Repo(a.Root!));
                    (repo.Head, repo.Entries, repo.NewDirs, repo.GitStamp) = (a.Head, a.Entries, a.NewDirs, a.Git);
                    changed = true;
                }
        }
        if (_running == null && _wanted.Count > 0)
        {
            var batch = _wanted.ToList();
            _wanted.Clear();
            var roots = _repos.Select(r => r.Root).ToHashSet(Paths.Comparer);
            _running = Task.Run(() => batch.Select(f => Ask(f, roots.Contains(f)))
                .GroupBy(a => a.Root ?? "\0" + a.Folder).Select(g => g.First()).ToList());
        }
        return changed;
    }

    /// <summary>How a tree entry changed (roots and git-ignored entries are never marked).</summary>
    public Kind Of(TreeNode node)
    {
        if (node.IsRoot || node.Ignored) return Kind.None;
        if (RepoFor(node.FullPath) is { } repo)
        {
            // A repository's own folder inside a plain one: marked by what changed in it.
            if (Paths.Same(node.FullPath, repo.Root)) return repo.Entries.Count > 0 ? Kind.Modified : Kind.None;
            return repo.Entries.TryGetValue(node.FullPath, out var kind) ? kind : InNewDir(repo, node.FullPath);
        }
        if (node.IsNew || IsCreated(node.FullPath)) return Kind.Added;
        if (node.IsDir) return _touched.Contains(node.FullPath) ? Kind.Modified : Kind.None;
        // A file changed since the start: its time moved since it was first listed or is past the start,
        // and its inode changed since then too — a time set back (cp -p, rsync -t) cannot hide a change,
        // nor a time in the future make one.
        return (node.WriteUtc > _since || node.WriteUtc != node.FirstWriteUtc) && InodeChangedSince(node)
            ? Kind.Modified : Kind.None;
    }

    private bool InodeChangedSince(TreeNode node)
    {
        if (node.ChangeNs < 0) node.ChangeNs = TextFile.Stat(node.FullPath)?.ChangeNs ?? long.MaxValue; // unknown: the time alone
        return node.ChangeNs > SinceNs;
    }

    /// <summary>
    /// A file's committed text to mark its changed lines against: empty for a
    /// file git does not have yet (outside git: one that is new, isNew); null
    /// outside a repository and for an ignored or a very big file, which are
    /// compared with their first-opened text.
    /// </summary>
    public string[]? Committed(string path, bool isNew)
    {
        if (RepoFor(path) is not { } repo) return isNew || IsCreated(path) ? Array.Empty<string>() : null;
        if (Target(repo, path) is not { } file) return null;
        if (repo.Entries.GetValueOrDefault(file) == Kind.Added || InNewDir(repo, file) == Kind.Added)
            return Array.Empty<string>();
        if (repo.Head.Length == 0 || TextFile.Stamp(path) is not { Length: <= MaxCommittedBytes }) return null;
        string key = repo.Head + ":" + file;
        if (_committed.TryGetValue(key, out var cached)) return cached;
        string rel = Path.GetRelativePath(repo.Root, file).Replace(Path.DirectorySeparatorChar, '/');
        string[]? text = null;
        if (Proc.RunBytes("git", "cat-file --batch", repo.Root, stdin: $"{repo.Head}:{rel}\n") is { } output
            && Blob(output) is { } blob)
        {
            try { text = TextFile.Lines(blob).ToArray(); }
            catch (InvalidDataException) { /* binary */ }
        }
        if (_committed.Count > 256) _committed.Clear();
        return _committed[key] = text;
    }

    private Repo? RepoFor(string path) =>
        _repos.Where(r => Paths.IsSameOrInside(path, r.Root)).MaxBy(r => r.Root.Length);

    /// <summary>
    /// The file a path leads to, spelled from the repository's root as git lists
    /// it: git keeps a symlink as the name of its target, so the text to compare
    /// is the target's. Null when a link leads out of the repository.
    /// </summary>
    private static string? Target(Repo repo, string path)
    {
        string real = Paths.Real(path), root = Paths.Real(repo.Root);
        return Paths.IsInside(real, root) ? Path.Combine(repo.Root, Path.GetRelativePath(root, real)) : null;
    }

    private static Kind InNewDir(Repo repo, string path) =>
        repo.NewDirs.Any(d => Paths.IsInside(path, d)) ? Kind.Added : Kind.None;

    /// <summary>In the background: the repository holding `folder` (a known root as it is) and its status.</summary>
    private static Answer Ask(string folder, bool isRoot)
    {
        var none = new Answer(folder, null, "", new(), new());
        string root = folder;
        if (!isRoot)
        {
            if (Proc.Run("git", "rev-parse --show-prefix", folder) is not { } prefix) return none;
            // "src/app/" below the repository's top: climb as many folders.
            foreach (var _ in prefix.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
                root = Path.GetDirectoryName(root) ?? root;
        }
        var git = GitStamp(GitDir(Path.Combine(root, ".git"))); // before the status: a commit after it shows as a change
        string? status = Proc.Run("git", "--no-optional-locks status --porcelain=v2 -z --branch --untracked-files=normal",
            root, timeoutMs: 15_000);
        if (status == null) return none;
        var (head, entries, newDirs) = Parse(root, status);
        return new Answer(folder, root, head, entries, newDirs, git);
    }

    /// <summary>`git status --porcelain=v2 -z --branch`: the last commit and the changed paths, folders above them included.</summary>
    internal static (string Head, Dictionary<string, Kind> Entries, List<string> NewDirs) Parse(string root, string status)
    {
        string head = "";
        var entries = new Dictionary<string, Kind>(Paths.Comparer);
        var newDirs = new List<string>();
        var fields = status.Split('\0');
        for (int i = 0; i < fields.Length; i++)
        {
            string f = fields[i];
            if (f.StartsWith("# branch.oid ", StringComparison.Ordinal))
                head = f[13..] == "(initial)" ? "" : f[13..];
            else if (f.StartsWith("? ", StringComparison.Ordinal))
            {
                string full = Full(root, f[2..]);
                if (f.EndsWith('/')) newDirs.Add(full);
                Mark(entries, root, full, Kind.Added, gone: false);
            }
            else if (f.Length > 4 && f[0] is '1' or '2' or 'u' && f[1] == ' ')
            {
                // "1 XY sub mH mI mW hH hI path"; a rename has a score before the path and its old path after it.
                int at = AfterSpaces(f, f[0] == '1' ? 8 : f[0] == '2' ? 9 : 10);
                if (at < 0) continue;
                string xy = f.Substring(2, 2);
                Mark(entries, root, Full(root, f[at..]), xy[0] == 'A' ? Kind.Added : Kind.Modified, gone: xy.Contains('D'));
                if (f[0] == '2' && i + 1 < fields.Length) Mark(entries, root, Full(root, fields[++i]), Kind.Modified, gone: true);
            }
        }
        return (head, entries, newDirs);
    }

    /// <summary>Where the text after the first `count` spaces starts; -1 — there are fewer.</summary>
    private static int AfterSpaces(string s, int count)
    {
        int at = -1;
        while (count-- > 0 && (at = s.IndexOf(' ', at + 1)) >= 0) { }
        return at < 0 ? -1 : at + 1;
    }

    private static string Full(string root, string rel) =>
        Path.Combine(root, rel.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A changed path and the folders above it: a folder is modified when anything in it is (or went), new when all of it is.</summary>
    private static void Mark(Dictionary<string, Kind> entries, string root, string full, Kind kind, bool gone)
    {
        static Kind Stronger(Kind a, Kind b) => a > b ? a : b;
        if (!gone) entries[full] = Stronger(entries.GetValueOrDefault(full), kind);
        var above = gone ? Kind.Modified : kind;
        for (string? p = Path.GetDirectoryName(full); p != null && Paths.IsInside(p, root); p = Path.GetDirectoryName(p))
            entries[p] = Stronger(entries.GetValueOrDefault(p), above);
    }

    /// <summary>The contents in `git cat-file --batch` output: null when the object is missing or no blob.</summary>
    private static byte[]? Blob(byte[] output)
    {
        int nl = Array.IndexOf(output, (byte)'\n');
        if (nl < 0) return null;
        var header = Encoding.ASCII.GetString(output, 0, nl).Split(' '); // "<oid> blob <size>" or "<name> missing"
        if (header.Length != 3 || header[1] != "blob" || !int.TryParse(header[2], out int size)
            || nl + 1 + size > output.Length)
            return null;
        return output.AsSpan(nl + 1, size).ToArray();
    }
}
