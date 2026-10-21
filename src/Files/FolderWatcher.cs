/// <summary>What changed on disk since the last batch, as seen by FolderWatcher.</summary>
/// <param name="Summary">A short description for the status line.</param>
/// <param name="Overflow">Events were lost or the roots themselves changed: re-read everything.</param>
/// <param name="Dirs">Folders whose entries were created, deleted, renamed or written to.</param>
/// <param name="Deep">A burst too big to list folder by folder: Dirs are where it happened, anywhere below.</param>
/// <param name="Renames">Renames seen (old path → new path), for open tabs to follow.</param>
/// <param name="Content">Some file's contents changed.</param>
/// <param name="Restart">A watcher failed: the watchers are to be created anew.</param>
internal sealed record WatchBatch(string Summary, bool Overflow, IReadOnlyCollection<string> Dirs, bool Deep,
    IReadOnlyList<(string Old, string New)> Renames, bool Content, bool Restart);

/// <summary>
/// Watching the panel's root folders, recursively, so the tree follows
/// changes made outside. Each root has a FileSystemWatcher, plus one on its
/// parent folder for the root's own deletion, renaming and return. A watcher
/// that fails is recreated (the tree is re-read, as events were lost); one
/// that keeps failing, or cannot start, gives way to polling a signature of
/// the root's folders in the background. Events arrive on foreign threads and
/// only accumulate into a debounced batch, which the main thread takes with
/// TryConsumeDue.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private const int MaxPollDirs = 20_000; // polling ceiling; beyond that a polled tree may go stale
    private const int MaxBatchDirs = 2_000; // beyond that a batch keeps the folders above the changes

    private readonly object _lock = new();
    private readonly int _debounceMs;
    private readonly int _maxDelayMs;
    private readonly int _pollIntervalMs;
    private readonly Dictionary<string, FileSystemWatcher> _rootWatchers = new(Paths.Comparer);
    private readonly Dictionary<string, FileSystemWatcher> _parentWatchers = new(Paths.Comparer);
    private readonly HashSet<string> _pollRoots = new(Paths.Comparer);  // no live FSW — polled instead
    private readonly HashSet<string> _deadParents = new(Paths.Comparer); // parent watcher died
    private readonly HashSet<string> _pollGaveUp = new(Paths.Comparer);   // polling too expensive
    private readonly Dictionary<string, int?> _pollSig = new(Paths.Comparer); // null — root missing on disk
    private readonly Dictionary<string, (int Count, DateTime Since)> _errors = new(Paths.Comparer);
    private HashSet<string> _roots = new(Paths.Comparer);
    private DateTime _lastPollAt = DateTime.MinValue;
    private Task? _polling;
    private bool _disposed;

    // The batch being accumulated.
    private bool _pending;
    private bool _overflow;
    private bool _deep;
    private bool _restart;
    private bool _content;
    private int _count;
    private DateTime _firstAt;
    private DateTime _lastAt;
    private string _lastWhat = "";
    private HashSet<string> _dirs = new(Paths.Comparer);
    private List<(string Old, string New)> _renames = new();

    public FolderWatcher(int debounceMs = 400, int maxDelayMs = 2000, int pollIntervalMs = 1500)
    {
        _debounceMs = debounceMs;
        _maxDelayMs = maxDelayMs;
        _pollIntervalMs = pollIntervalMs;
    }

    /// <summary>Roots actually covered by a FileSystemWatcher.</summary>
    public IReadOnlyCollection<string> WatchedRoots
    {
        get { lock (_lock) return _rootWatchers.Keys.ToList(); }
    }

    /// <summary>Roots covered by neither FSW nor polling (for a user warning).</summary>
    public IReadOnlyList<string> UnwatchableRoots
    {
        get { lock (_lock) return _pollGaveUp.Where(r => _roots.Contains(r)).ToList(); }
    }

    /// <summary>Bring the watchers in line with the panel's roots; a vanished root's parent keeps watch for its return.</summary>
    public void SyncRoots(IEnumerable<string> roots, bool forceRecreate = false)
    {
        if (_disposed) return;
        var wanted = roots.Select(Paths.Normalize).Distinct(Paths.Comparer).ToList();
        lock (_lock)
        {
            _roots = new HashSet<string>(wanted, Paths.Comparer);
            _pollRoots.RemoveWhere(r => !_roots.Contains(r));
            _pollGaveUp.RemoveWhere(r => !_roots.Contains(r));
            foreach (var stale in _pollSig.Keys.Where(k => !_roots.Contains(k)).ToList())
                _pollSig.Remove(stale);
        }

        List<string> pollSnapshot;
        lock (_lock) pollSnapshot = _pollRoots.ToList();
        SyncSet(_rootWatchers,
            wanted.Where(p => Directory.Exists(p) && !pollSnapshot.Contains(p, Paths.Comparer)),
            recursive: true, isParent: false, forceRecreate);
        var parents = wanted.Select(p => Path.GetDirectoryName(p))
            .Where(p => p != null && Directory.Exists(p))
            .Select(p => p!)
            .Distinct(Paths.Comparer);
        SyncSet(_parentWatchers, parents, recursive: false, isParent: true, forceRecreate);
    }

    private void SyncSet(Dictionary<string, FileSystemWatcher> set, IEnumerable<string> wantedPaths,
        bool recursive, bool isParent, bool forceRecreate)
    {
        var wanted = new HashSet<string>(wantedPaths, Paths.Comparer);
        foreach (var path in set.Keys.Where(k => !wanted.Contains(k) || forceRecreate).ToList())
        {
            set[path].Dispose();
            set.Remove(path);
        }
        foreach (string path in wanted)
        {
            if (set.ContainsKey(path)) continue;
            var watcher = TryCreate(path, recursive, isParent);
            if (watcher != null) set[path] = watcher;
            else lock (_lock) (isParent ? _deadParents : _pollRoots).Add(path);
        }
    }

    private FileSystemWatcher? TryCreate(string path, bool recursive, bool isParent)
    {
        FileSystemWatcher? w = null;
        try
        {
            w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                // Names drive the tree; LastWrite tells that contents changed (open tabs, sizes).
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | (isParent ? 0 : NotifyFilters.LastWrite),
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnEvent(isParent, e.FullPath, null, "created");
            w.Deleted += (_, e) => OnEvent(isParent, e.FullPath, null, "deleted");
            w.Renamed += (_, e) => OnEvent(isParent, e.FullPath, e.OldFullPath, "renamed");
            // A write changes no entry, but it does change sizes (the file's and its folders').
            if (!isParent) w.Changed += (_, e) => Mark(null, content: true, dir: Path.GetDirectoryName(e.FullPath));
            w.Error += (_, e) => OnError(path, isParent, e.GetException());
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or PlatformNotSupportedException)
        {
            w?.Dispose();
            return null;
        }
    }

    private void OnError(string path, bool isParent, Exception? ex)
    {
        // Events were lost: re-read the tree. A watcher that failed otherwise is
        // recreated, and one that keeps failing (EventStream unavailable…) is retired to polling.
        bool lost = ex is InternalBufferOverflowException;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var (count, since) = _errors.GetValueOrDefault(path, (0, now));
            if (now - since > TimeSpan.FromMinutes(1)) (count, since) = (0, now);
            _errors[path] = (++count, since);
            if (count > 3 && !lost)
                (isParent ? _deadParents : _pollRoots).Add(path);
            _restart |= !lost;
        }
        Mark(lost ? "many changes at once" : "folder watcher restarted", overflow: true);
    }

    private void OnEvent(bool isParent, string fullPath, string? oldFullPath, string verb)
    {
        if (isParent)
        {
            // The parent only guards the fate of the roots themselves; ignore unrelated noise.
            lock (_lock)
            {
                if (!_roots.Contains(fullPath)
                    && (oldFullPath == null || !_roots.Contains(oldFullPath)))
                    return;
            }
            Mark($"{verb} {Path.GetFileName(fullPath)}", overflow: true);
            return;
        }
        string what = oldFullPath != null
            ? $"{verb} {Path.GetFileName(oldFullPath)} → {Path.GetFileName(fullPath)}"
            : $"{verb} {Path.GetFileName(fullPath)}";
        Mark(what, dir: Path.GetDirectoryName(fullPath), oldDir: oldFullPath == null ? null : Path.GetDirectoryName(oldFullPath),
            rename: oldFullPath == null ? null : (oldFullPath, fullPath));
    }

    /// <summary>Mark a change for the current batch (safe to call from FileSystemWatcher threads).</summary>
    internal void MarkDirty(string what, bool overflow = false) => Mark(what, overflow: overflow);

    private void Mark(string? what, bool overflow = false, bool content = false, string? dir = null,
        string? oldDir = null, (string, string)? rename = null)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_pending)
            {
                _pending = true;
                _firstAt = now;
                _count = 0;
                _overflow = _deep = false;
                _content = false;
                _lastWhat = "";
                _dirs = new HashSet<string>(Paths.Comparer);
                _renames = new List<(string, string)>();
            }
            _lastAt = now;
            _overflow |= overflow;
            _content |= content;
            if (dir != null) _dirs.Add(dir);
            if (oldDir != null) _dirs.Add(oldDir);
            if (_dirs.Count > MaxBatchDirs)
            {
                _deep = true;
                _dirs = Collapse(_dirs, MaxBatchDirs / 4);
            }
            if (what == null) return;
            _count++;
            _lastWhat = what;
            if (rename is { } r) _renames.Add(r);
        }
    }

    /// <summary>
    /// A burst (an install, a build): the deepest folders give way to their
    /// parents until at most `limit` are left, each standing for everything below it.
    /// </summary>
    internal static HashSet<string> Collapse(HashSet<string> dirs, int limit)
    {
        static int Depth(string p) => p.Count(c => c == Path.DirectorySeparatorChar);
        while (dirs.Count > limit)
        {
            int deepest = dirs.Max(Depth);
            dirs = new HashSet<string>(dirs.Select(d => Depth(d) == deepest ? Path.GetDirectoryName(d) ?? d : d),
                Paths.Comparer);
        }
        return dirs;
    }

    /// <summary>
    /// Hand out the batch once events have been quiet for the debounce interval,
    /// or maxDelay after the first one (a long build must not postpone it forever).
    /// </summary>
    public bool TryConsumeDue(out WatchBatch batch)
    {
        lock (_lock)
        {
            batch = new WatchBatch("", false, Array.Empty<string>(), false, Array.Empty<(string, string)>(), false, false);
            if (!_pending) return false;
            var due = new[] { _lastAt.AddMilliseconds(_debounceMs), _firstAt.AddMilliseconds(_maxDelayMs) }.Min();
            if (DateTime.UtcNow < due) return false;
            string summary = _count > 1 ? $"{_lastWhat}; {_count} changes in total" : _lastWhat;
            batch = new WatchBatch(summary, _overflow, _dirs, _deep, _renames, _content, _restart);
            _pending = _restart = false;
            return true;
        }
    }

    /// <summary>Poll roots without a live watcher (from the main loop, every pollIntervalMs); the walk runs in the background.</summary>
    public void PollIfDue()
    {
        if (_disposed || _polling is { IsCompleted: false }) return;
        var now = DateTime.UtcNow;
        if (now - _lastPollAt < TimeSpan.FromMilliseconds(_pollIntervalMs)) return;
        _lastPollAt = now;
        var targets = PollTargets();
        if (targets.Count > 0) _polling = Task.Run(() => { foreach (string root in targets) PollRoot(root); });
    }

    /// <summary>Roots to poll this round; also retires watchers that died since the last poll.</summary>
    private List<string> PollTargets()
    {
        lock (_lock)
        {
            RetireDead(_rootWatchers, _pollRoots);
            RetireDead(_parentWatchers, _deadParents);
            return _roots.Where(NeedsPoll).ToList();
        }
    }

    private static void RetireDead(Dictionary<string, FileSystemWatcher> set, HashSet<string> dead)
    {
        foreach (string path in dead.Where(set.ContainsKey).ToList())
        {
            set[path].Dispose();
            set.Remove(path);
        }
    }

    // Called under _lock.
    private bool NeedsPoll(string root) =>
        !_pollGaveUp.Contains(root)
        && (_pollRoots.Contains(root) || !_rootWatchers.ContainsKey(root)
            || (Path.GetDirectoryName(root) is { } par && _deadParents.Contains(par)));

    private void PollRoot(string root)
    {
        if (!TrySignature(root, out int? sig))
        {
            lock (_lock) { _pollGaveUp.Add(root); _pollSig.Remove(root); }
            return;
        }
        bool changed;
        lock (_lock)
        {
            changed = _pollSig.TryGetValue(root, out int? prev) && prev != sig;
            _pollSig[root] = sig;
        }
        if (changed)
            Mark($"contents of {Path.GetFileName(root)} changed", overflow: true);
    }

    /// <summary>
    /// Signature of a root's structure: a hash of all directories (relative
    /// path + modification time). Creating, deleting or renaming an entry
    /// changes its directory's mtime, so files need not be enumerated.
    /// Symlinked folders are not followed. false — too many directories to poll.
    /// </summary>
    private static bool TrySignature(string root, out int? sig)
    {
        sig = null;
        if (!Directory.Exists(root)) return true;
        int hash = 17;
        var stack = new Stack<string>();
        stack.Push(root);
        int count = 0;
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (++count > MaxPollDirs) return false;
            try
            {
                // Hash the path relative to the root without allocating it:
                // enumerated paths are built from the root string itself.
                unchecked
                {
                    hash = hash * 31 + string.GetHashCode(dir.AsSpan(root.Length));
                    hash = hash * 31 + Directory.GetLastWriteTimeUtc(dir).Ticks.GetHashCode();
                }
                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub.FullName);
            }
            catch (IOException) { /* folder vanished mid-walk — the signature will change anyway */ }
            catch (UnauthorizedAccessException) { /* just skip unreachable directories */ }
        }
        sig = hash;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _rootWatchers.Values) w.Dispose();
        foreach (var w in _parentWatchers.Values) w.Dispose();
        _rootWatchers.Clear();
        _parentWatchers.Clear();
    }
}
