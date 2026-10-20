namespace ConsoleEditor;

/// <summary>
/// Watching the left panel's root folders: files and folders created,
/// deleted or renamed anywhere inside the roots (recursively) must refresh
/// the tree display on their own, without pressing F5.
///
/// How it works:
/// - The primary path is a FileSystemWatcher per root (recursive) plus a
///   watcher on the parent folder, to notice deletion, renaming and
///   reappearance of the root itself.
/// - If a FileSystemWatcher fails to start for a root (e.g. EventStream
///   unavailable), the root falls back to polling: the main thread
///   periodically calls PollIfDue, and the signature of the root's
///   directories (paths + modification times; a directory's mtime changes
///   when entries are created/deleted/renamed inside it) is compared
///   with the previous one.
///
/// FileSystemWatcher events arrive on foreign threads, so here they only
/// mark "dirty" and accumulate into a batch (debounce); the tree is
/// re-read by the main thread, which polls TryConsumeDue.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    private const int MaxPollDirs = 20_000; // polling ceiling; beyond that it's manual F5 only

    private readonly object _lock = new();
    private readonly int _debounceMs;
    private readonly int _maxDelayMs;
    private readonly int _pollIntervalMs;
    private readonly Dictionary<string, FileSystemWatcher> _rootWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _parentWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pollRoots = new(StringComparer.OrdinalIgnoreCase);  // no live FSW — polled instead
    private readonly HashSet<string> _deadParents = new(StringComparer.OrdinalIgnoreCase); // parent watcher died
    private readonly HashSet<string> _pollGaveUp = new(StringComparer.OrdinalIgnoreCase);   // polling too expensive
    private readonly Dictionary<string, int?> _pollSig = new(StringComparer.OrdinalIgnoreCase); // null — root missing on disk
    private HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPollAt = DateTime.MinValue;
    private bool _disposed;

    private bool _pending;
    private bool _overflow;
    private int _count;
    private DateTime _firstAt;
    private DateTime _lastAt;
    private string _lastWhat = "";

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

    /// <summary>
    /// Bring the watchers in line with the panel's root list: create new
    /// ones, drop removed ones, drop ones whose root vanished from disk
    /// (the parent keeps watch for their return).
    /// </summary>
    public void SyncRoots(IEnumerable<string> roots, bool forceRecreate = false)
    {
        if (_disposed) return;
        var wanted = roots.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_lock)
        {
            _roots = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            _pollRoots.RemoveWhere(r => !_roots.Contains(r));
            _pollGaveUp.RemoveWhere(r => !_roots.Contains(r));
            foreach (var stale in _pollSig.Keys.Where(k => !_roots.Contains(k)).ToList())
                _pollSig.Remove(stale);
        }

        List<string> pollSnapshot;
        lock (_lock) pollSnapshot = _pollRoots.ToList();
        SyncSet(_rootWatchers,
            wanted.Where(p => Directory.Exists(p) && !pollSnapshot.Contains(p)),
            recursive: true, isParent: false, forceRecreate);
        var parents = wanted.Select(p => Path.GetDirectoryName(p))
            .Where(p => p != null && Directory.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        SyncSet(_parentWatchers, parents, recursive: false, isParent: true, forceRecreate);
    }

    private void SyncSet(Dictionary<string, FileSystemWatcher> set, IEnumerable<string> wantedPaths,
        bool recursive, bool isParent, bool forceRecreate)
    {
        var wanted = new HashSet<string>(wantedPaths, StringComparer.OrdinalIgnoreCase);
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
                // The tree only shows structure: file and folder names.
                // File content changes do not affect the display.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnEvent(isParent, e.FullPath, null, "created");
            w.Deleted += (_, e) => OnEvent(isParent, e.FullPath, null, "deleted");
            w.Renamed += (_, e) => OnEvent(isParent, e.FullPath, e.OldFullPath, "renamed");
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
        // Buffer overflow of a live watcher — reason to re-read the tree and
        // recreate the watchers. Other errors (e.g. EventStream unavailable)
        // retire the watcher: the root falls back to polling.
        if (ex is InternalBufferOverflowException)
            MarkDirty("many changes at once", overflow: true);
        else
            lock (_lock) (isParent ? _deadParents : _pollRoots).Add(path);
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
        }
        string what = oldFullPath != null
            ? $"{verb} {Path.GetFileName(oldFullPath)} → {Path.GetFileName(fullPath)}"
            : $"{verb} {Path.GetFileName(fullPath)}";
        MarkDirty(what);
    }

    /// <summary>Mark a batch of changes (safe to call from FileSystemWatcher threads).</summary>
    internal void MarkDirty(string what, bool overflow = false)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_pending)
            {
                _pending = true;
                _firstAt = now;
                _count = 0;
                _overflow = false;
            }
            _lastAt = now;
            _count++;
            _lastWhat = what;
            _overflow |= overflow;
        }
    }

    /// <summary>
    /// Hand out the accumulated batch once the event stream has been quiet
    /// for the debounce interval (but no later than maxDelay after the first
    /// event — a long burst, e.g. a build, must not postpone the refresh forever).
    /// </summary>
    public bool TryConsumeDue(out string summary, out bool overflow)
    {
        lock (_lock)
        {
            summary = "";
            overflow = false;
            if (!_pending) return false;
            var due = new[] { _lastAt.AddMilliseconds(_debounceMs), _firstAt.AddMilliseconds(_maxDelayMs) }.Min();
            if (DateTime.UtcNow < due) return false;
            summary = _count > 1 ? $"{_lastWhat}; {_count} changes in total" : _lastWhat;
            overflow = _overflow;
            _pending = false;
            _count = 0;
            _overflow = false;
            return true;
        }
    }

    /// <summary>
    /// Poll roots that have no live FileSystemWatcher (called by the main
    /// thread from the input loop; throttled internally by pollIntervalMs).
    /// </summary>
    public void PollIfDue()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        if (now - _lastPollAt < TimeSpan.FromMilliseconds(_pollIntervalMs)) return;
        _lastPollAt = now;

        foreach (string root in PollTargets()) PollRoot(root);
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
            MarkDirty($"contents of {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))} changed");
    }

    /// <summary>
    /// Signature of a root's structure: a hash of all directories (relative
    /// path + modification time). Creating, deleting or renaming an entry
    /// changes its directory's mtime, so files need not be enumerated.
    /// false — too many directories to poll.
    /// </summary>
    private static bool TrySignature(string root, out int? sig)
    {
        sig = null;
        if (!Directory.Exists(root)) return true;
        var hc = new HashCode();
        var stack = new Stack<string>();
        stack.Push(root);
        int count = 0;
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (++count > MaxPollDirs) return false;
            try
            {
                hc.Add(Path.GetRelativePath(root, dir), StringComparer.OrdinalIgnoreCase);
                hc.Add(Directory.GetLastWriteTimeUtc(dir).Ticks);
                foreach (string sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
            }
            catch (IOException) { /* folder vanished mid-walk — the signature will change anyway */ }
            catch (UnauthorizedAccessException) { /* just skip unreachable directories */ }
        }
        sig = hc.ToHashCode();
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
