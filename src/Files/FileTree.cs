/// <summary>Tree node: a folder or a file. Root folders have Parent == null.</summary>
public sealed class TreeNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDir { get; init; }
    public required int Depth { get; init; }
    public TreeNode? Parent { get; init; }

    /// <summary>A symbolic link (git asks about the link itself, never about paths beyond it).</summary>
    public bool IsLink { get; init; }

    /// <summary>Hidden from git by .gitignore (also .git and .gitignore themselves) — drawn dimmed.</summary>
    public bool Ignored { get; set; }

    /// <summary>Bytes: a file's length; a folder's recursive total (a lower bound until SizeExact).</summary>
    public long Size { get; set; }

    /// <summary>True when Size is final; false while it is still a lower-bound estimate.</summary>
    public bool SizeExact { get; set; }

    /// <summary>Modification time when listed: a folder's size stays cached while it stands.</summary>
    public DateTime WriteUtc { get; set; }

    /// <summary>Appeared after its folder was first read (outside git: a new entry).</summary>
    public bool IsNew { get; init; }

    /// <summary>Bumped by every new count of this folder: a count started earlier no longer applies.</summary>
    internal int SizeGeneration;

    public bool Expanded { get; set; }
    public bool ChildrenLoaded { get; set; }
    public List<TreeNode> Children { get; } = new();

    public bool IsRoot => Parent == null;
}

/// <summary>
/// Left panel: root folders as top rows, each with its lazily loaded
/// hierarchy. A refresh re-reads loaded folders only and keeps every node
/// that still exists, expansion included. Entries are sorted by size, largest
/// first: folder sizes are counted in the background (FolderSizer), and again
/// whenever something below them changes.
/// </summary>
public sealed class FileTree
{
    public List<TreeNode> Roots { get; } = new();

    /// <summary>
    /// Exact folder sizes from finished counts, valid while the folder's own
    /// mtime stands and nothing below it is reported changed (see RefreshDirs).
    /// </summary>
    private readonly Dictionary<string, (long Size, DateTime WriteUtc)> _sizeCache = new(Paths.Comparer);
    private List<(TreeNode Parent, FolderSizer Sizer)> _unfinishedSizing = new();

    public IReadOnlyList<string> RootPaths => Roots.Select(r => r.FullPath).ToList();
    public List<TreeNode> Visible { get; private set; } = new();
    public int SelectedIndex { get; private set; }
    public int ScrollOffset { get; private set; }

    /// <summary>Viewport height, updated on each redraw.</summary>
    public int ViewHeight { get; set; } = 20;

    /// <summary>Bumped whenever folders are read: new ones may hold a repository.</summary>
    public int Generation { get; private set; }

    /// <summary>Read folders that hold a .git: repositories, for the change marks.</summary>
    public IEnumerable<string> RepoFolders()
    {
        var stack = new Stack<TreeNode>(Roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.ChildrenLoaded) continue;
            foreach (var c in node.Children)
                if (c.Name == ".git") yield return node.FullPath;
                else if (c.IsDir && !c.IsLink) stack.Push(c);
        }
    }

    public FileTree(string rootPath) : this(new[] { rootPath }) { }

    public FileTree(IEnumerable<string> rootPaths)
    {
        foreach (var p in rootPaths)
        {
            var root = CreateRoot(p);
            if (Roots.All(r => !Paths.Same(r.FullPath, root.FullPath)))
                Roots.Add(root);
        }
        if (Roots.Count == 0)
            Roots.Add(CreateRoot(Directory.GetCurrentDirectory()));
        RebuildVisible();
    }

    private TreeNode CreateRoot(string path)
    {
        string full = Paths.Normalize(path);
        string name = Path.GetFileName(full);
        var root = new TreeNode
        {
            Name = name.Length == 0 ? full : name,
            FullPath = full,
            IsDir = true,
            Depth = 0,
            Expanded = true,
        };
        LoadChildren(root);
        return root;
    }

    public TreeNode? Selected =>
        Visible.Count == 0 ? null : Visible[Math.Clamp(SelectedIndex, 0, Visible.Count - 1)];

    private void LoadChildren(TreeNode node)
    {
        if (!node.IsDir || node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;
        Generation++;
        node.Children.Clear();
        node.Children.AddRange(ReadEntries(node));
        MarkIgnored(node);
        StartSizing(node, sync: true);
        SortChildren(node);
    }

    /// <summary>The folder's entries from disk as fresh nodes (empty when unreadable or gone).</summary>
    private static List<TreeNode> ReadEntries(TreeNode node)
    {
        var list = new List<TreeNode>();
        try
        {
            foreach (var e in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos())
                list.Add(new TreeNode
                {
                    Name = e.Name,
                    FullPath = e.FullName,
                    IsDir = e is DirectoryInfo,
                    IsLink = (e.Attributes & FileAttributes.ReparsePoint) != 0,
                    Depth = node.Depth + 1,
                    Parent = node,
                    Size = e is FileInfo fi ? SafeLength(fi) : 0,
                    SizeExact = e is FileInfo,
                    WriteUtc = SafeWriteTime(e),
                });
        }
        catch (UnauthorizedAccessException) { /* folder not accessible — just empty */ }
        catch (IOException) { /* folder may have disappeared */ }
        return list;
    }

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTime SafeWriteTime(FileSystemInfo fi)
    {
        try { return fi.LastWriteTimeUtc; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Order: normal entries first, git-ignored (dimmed) last; within each group
    /// folders before files, largest first, name breaks ties.</summary>
    private static void SortChildren(TreeNode node)
    {
        var sorted = node.Children
            .OrderBy(c => c.Ignored)
            .ThenByDescending(c => c.IsDir)
            .ThenByDescending(c => c.Size)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
    }

    private const int SyncSizeEntryBudget = 1_500;
    private const int SyncSizeMsBudget = 30;

    /// <summary>
    /// Sizes drive the sort order, so a load never blocks on them: cached
    /// folders are free; a first load (sync) counts the rest within a small
    /// budget (a lower bound for huge ones); what remains is queued for the
    /// background (TakeUnfinishedSizing), replacing a queued count of this folder.
    /// </summary>
    private void StartSizing(TreeNode parent, bool sync)
    {
        DropSizing(job => job.Parent == parent);
        List<(TreeNode, DateTime)>? need = null;
        foreach (var child in parent.Children)
        {
            if (!child.IsDir) continue;
            if (_sizeCache.TryGetValue(child.FullPath, out var hit) && hit.WriteUtc == child.WriteUtc)
            {
                child.Size = hit.Size;
                child.SizeExact = true;
            }
            else (need ??= new()).Add((child, child.WriteUtc));
        }
        if (need == null) return;
        var sizer = new FolderSizer(need);
        bool settled = sync && sizer.Step(SyncSizeEntryBudget, Environment.TickCount64 + SyncSizeMsBudget);
        if (sync) ApplySizer(sizer);
        if (settled) sizer.Dispose();
        else _unfinishedSizing.Add((parent, sizer));
    }

    /// <summary>Forget queued counts that match (their folders were re-read or left the panel).</summary>
    private void DropSizing(Func<(TreeNode Parent, FolderSizer Sizer), bool> match)
    {
        foreach (var job in _unfinishedSizing.Where(match)) job.Sizer.Dispose();
        _unfinishedSizing.RemoveAll(job => match(job));
    }

    private void ApplySizer(FolderSizer sizer)
    {
        foreach (var (node, size, mtime, complete, generation) in sizer.Snapshot())
        {
            if (node.SizeGeneration != generation) continue; // counted again since
            node.Size = size;
            node.SizeExact = complete;
            if (!complete) continue;
            if (_sizeCache.Count > 8192) _sizeCache.Clear();
            _sizeCache[node.FullPath] = (size, mtime);
        }
    }

    /// <summary>Next folder count for the background (null when none).</summary>
    internal (TreeNode Parent, FolderSizer Sizer)? TakeUnfinishedSizing()
    {
        if (_unfinishedSizing.Count == 0) return null;
        var job = _unfinishedSizing[0];
        _unfinishedSizing.RemoveAt(0);
        return job;
    }

    /// <summary>Apply background-counted sizes and re-sort, keeping the selected node selected.</summary>
    internal void CompleteSizing(TreeNode parent, FolderSizer sizer)
    {
        var selected = Selected;
        ApplySizer(sizer);
        SortChildren(parent);
        RebuildVisible();
        if (selected != null) SelectNode(selected);
    }

    /// <summary>
    /// Mark children that git ignores, asking git itself (`git check-ignore`) so
    /// nested .gitignore files, negations and global excludes count; outside a
    /// repository nothing is marked. .git and .gitignore are marked too, as
    /// service entries git does not report.
    /// </summary>
    private static void MarkIgnored(TreeNode dir)
    {
        foreach (var c in dir.Children)
            c.Ignored = c is { IsDir: true, Name: ".git" } or { IsDir: false, Name: ".gitignore" };
        if (dir.Children.Count == 0) return;
        // A folder is asked about as "name/"; a symlink as the link itself — a path
        // through it would make git refuse the whole batch ("beyond a symbolic link").
        static string Query(TreeNode c) => c.Name + (c.IsDir && !c.IsLink ? "/" : "");
        string? output = Proc.Run("git", "check-ignore --stdin -z", dir.FullPath,
            string.Concat(dir.Children.Select(c => Query(c) + "\0")), timeoutMs: 2000, okExitCodes: new[] { 0, 1 });
        if (output == null) return;
        var ignored = new HashSet<string>(output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        foreach (var c in dir.Children)
            if (ignored.Contains(Query(c))) c.Ignored = true;
    }

    /// <summary>
    /// Mark ignored children of many folders under one root with a single
    /// `git check-ignore`. false — git refused the batch (a path in a nested
    /// repository, say) and the folders should be asked one by one; outside a
    /// repository there is nothing to mark, so that counts as done.
    /// </summary>
    private static bool MarkIgnoredBatch(string root, IReadOnlyList<TreeNode> dirs)
    {
        var queries = new List<(TreeNode Child, string Query)>();
        foreach (var dir in dirs)
        {
            string rel = Path.GetRelativePath(root, dir.FullPath).Replace('\\', '/');
            string prefix = rel == "." ? "" : rel + "/";
            foreach (var c in dir.Children)
            {
                c.Ignored = c is { IsDir: true, Name: ".git" } or { IsDir: false, Name: ".gitignore" };
                queries.Add((c, prefix + c.Name + (c.IsDir && !c.IsLink ? "/" : "")));
            }
        }
        if (queries.Count == 0) return true;
        string? output = Proc.Run("git", "check-ignore --stdin -z", root, string.Concat(queries.Select(q => q.Query + "\0")),
            timeoutMs: 5000, okExitCodes: new[] { 0, 1 });
        if (output == null) return Proc.Run("git", "rev-parse --show-prefix", root) == null; // not a repository at all
        var ignored = new HashSet<string>(output.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        foreach (var (c, q) in queries)
            if (ignored.Contains(q)) c.Ignored = true;
        return true;
    }

    private void RebuildVisible()
    {
        var list = new List<TreeNode>();
        void Walk(TreeNode node)
        {
            list.Add(node);
            if (node.IsDir && node.Expanded)
            {
                LoadChildren(node);
                foreach (var child in node.Children) Walk(child);
            }
        }
        foreach (var root in Roots) Walk(root);
        Visible = list;
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Visible.Count - 1));
        EnsureVisible();
    }

    private void SelectNode(TreeNode node)
    {
        int idx = Visible.IndexOf(node);
        if (idx >= 0) { SelectedIndex = idx; EnsureVisible(); }
    }

    /// <summary>Add another root folder to the panel. false — already present (it just gets selected).</summary>
    public bool AddRoot(string path)
    {
        string full = Paths.Normalize(path);
        var existing = Roots.FirstOrDefault(r => Paths.Same(r.FullPath, full));
        if (existing != null)
        {
            SelectNode(existing);
            return false;
        }
        var root = CreateRoot(full);
        Roots.Add(root);
        RebuildVisible();
        SelectNode(root);
        return true;
    }

    /// <summary>Remove a root from the panel (files on disk are untouched). The last root cannot be removed.</summary>
    public bool RemoveRoot(TreeNode root)
    {
        if (Roots.Count <= 1 || !Roots.Remove(root)) return false;
        DropSizing(job => Paths.IsSameOrInside(job.Parent.FullPath, root.FullPath));
        // No longer watched: what changes there from now on would go unnoticed.
        foreach (string key in _sizeCache.Keys.Where(k => Paths.IsSameOrInside(k, root.FullPath)).ToList())
            _sizeCache.Remove(key);
        RebuildVisible();
        return true;
    }

    public void EnsureVisible()
    {
        if (ViewHeight <= 0) return;
        if (SelectedIndex < ScrollOffset) ScrollOffset = SelectedIndex;
        if (SelectedIndex >= ScrollOffset + ViewHeight)
            ScrollOffset = SelectedIndex - ViewHeight + 1;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, Visible.Count - ViewHeight));
    }

    /// <summary>Move the selection by delta rows (mouse wheel).</summary>
    public void MoveBy(int delta)
    {
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Math.Max(0, Visible.Count - 1));
        EnsureVisible();
    }

    /// <summary>Select a row by index in the visible list (mouse click).</summary>
    public void SelectIndex(int index)
    {
        SelectedIndex = Math.Clamp(index, 0, Math.Max(0, Visible.Count - 1));
        EnsureVisible();
    }

    public void MoveUp() { if (SelectedIndex > 0) SelectedIndex--; EnsureVisible(); }
    public void MoveDown() { if (SelectedIndex < Visible.Count - 1) SelectedIndex++; EnsureVisible(); }
    public void PageUp() { SelectedIndex = Math.Max(0, SelectedIndex - ViewHeight); EnsureVisible(); }
    public void PageDown() { SelectedIndex = Math.Min(Math.Max(0, Visible.Count - 1), SelectedIndex + ViewHeight); EnsureVisible(); }
    public void Home() { SelectedIndex = 0; EnsureVisible(); }
    public void End() { SelectedIndex = Math.Max(0, Visible.Count - 1); EnsureVisible(); }

    /// <summary>Right arrow: expand a folder or move to its first child.</summary>
    public void ExpandOrDescend()
    {
        var node = Selected;
        if (node is not { IsDir: true }) return;
        if (!node.Expanded)
        {
            node.Expanded = true;
            LoadChildren(node);
            RebuildVisible();
        }
        else if (node.Children.Count > 0)
        {
            MoveDown();
        }
    }

    /// <summary>Left arrow: collapse a folder or move to the parent.</summary>
    public void CollapseOrAscend()
    {
        var node = Selected;
        if (node == null) return;
        if (node.IsDir && node.Expanded)
        {
            node.Expanded = false;
            RebuildVisible();
        }
        else if (node.Parent != null)
        {
            SelectNode(node.Parent);
        }
    }

    /// <summary>Enter on a folder: collapse/expand. Returns true if the node is a folder.</summary>
    public bool ToggleSelected()
    {
        var node = Selected;
        if (node is not { IsDir: true }) return false;
        node.Expanded = !node.Expanded;
        if (node.Expanded) LoadChildren(node);
        RebuildVisible();
        return true;
    }

    /// <summary>A root was renamed on disk — replace its path, keeping position and expanded state.</summary>
    public bool ReplaceRootPath(string oldPath, string newPath)
    {
        int i = Roots.FindIndex(r => Paths.Same(r.FullPath, Paths.Normalize(oldPath)));
        if (i < 0) return false;
        bool expanded = Roots[i].Expanded;
        var root = CreateRoot(newPath);
        root.Expanded = expanded;
        Roots[i] = root;
        RebuildVisible();
        SelectNode(root);
        return true;
    }

    /// <summary>The node of a path among the folders read so far (nothing is read or expanded); null — not there.</summary>
    public TreeNode? Find(string fullPath)
    {
        string full = Paths.Normalize(fullPath);
        foreach (var root in Roots.Where(r => Paths.IsSameOrInside(full, r.FullPath)).OrderByDescending(r => r.FullPath.Length))
        {
            var node = root;
            while (!Paths.Same(node.FullPath, full))
            {
                var next = node.ChildrenLoaded
                    ? node.Children.FirstOrDefault(c => Paths.IsSameOrInside(full, c.FullPath))
                    : null;
                if (next == null) break;
                node = next;
            }
            if (Paths.Same(node.FullPath, full)) return node;
        }
        return null;
    }

    /// <summary>Select a node by full path in any root, expanding its parents.</summary>
    public void SelectPath(string fullPath)
    {
        string full = Paths.Normalize(fullPath);
        foreach (var root in Roots)
        {
            if (Paths.Same(full, root.FullPath))
            {
                RebuildVisible();
                SelectNode(root);
                return;
            }
            if (!Paths.IsInside(full, root.FullPath)) continue;

            string rel = full[root.FullPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            bool ok = true;
            foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                LoadChildren(current);
                current.Expanded = true;
                var next = current.Children.FirstOrDefault(c => Paths.Same(c.Name, part));
                if (next == null) { ok = false; break; }
                current = next;
            }
            if (!ok) continue;
            RebuildVisible();
            SelectNode(current);
            return;
        }
    }

    /// <summary>Re-read every loaded folder from disk, keeping expansion and the selection.</summary>
    public void Refresh() => RefreshDirs(null);

    /// <summary>
    /// Something changed in these folders (null — anywhere; deep — anywhere below
    /// them too). Loaded ones are re-read, keeping the nodes that still exist with
    /// their expansion; every folder holding a change is counted again up to the
    /// root, showing its old size as approximate meanwhile. Returns whether
    /// anything was re-read.
    /// </summary>
    public bool RefreshDirs(IReadOnlyCollection<string>? dirs, bool deep = false)
    {
        // Every listed folder by path (a root nested in another is listed twice).
        static IEnumerable<TreeNode> Folders(TreeNode n) =>
            n.ChildrenLoaded ? n.Children.Where(c => c.IsDir).SelectMany(Folders).Prepend(n) : new[] { n };
        var listed = Roots.SelectMany(Folders).ToLookup(n => n.FullPath, Paths.Comparer);
        var targets = new HashSet<TreeNode>(); // loaded folders to re-read
        var recount = new HashSet<TreeNode>(); // folders whose subfolders are counted again
        void Invalidate(TreeNode n)
        {
            if (n.Parent == null) return; // a root shows no size of its own
            _sizeCache.Remove(n.FullPath);
            n.SizeExact = false;
            recount.Add(n.Parent);
        }
        void Below(TreeNode n)
        {
            Invalidate(n);
            if (!n.ChildrenLoaded) return;
            targets.Add(n);
            foreach (var c in n.Children.Where(c => c.IsDir)) Below(c);
        }
        if (dirs == null)
            foreach (var r in Roots) Below(r);
        else
            foreach (string d in dirs.Select(Paths.Normalize))
            {
                foreach (var n in listed[d])
                    if (deep) Below(n);
                    else if (n.ChildrenLoaded) targets.Add(n);
                for (string? p = d; p != null; p = Path.GetDirectoryName(p))
                    foreach (var n in listed[p]) Invalidate(n);
            }
        foreach (var parent in recount.Where(p => !targets.Contains(p))) StartSizing(parent, sync: false);
        if (targets.Count == 0) return false;

        Generation++;
        var selected = Selected;
        var order = targets.ToList();
        var probes = new TreeNode[order.Count];
        Parallel.For(0, order.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            probes[i] = new TreeNode
            {
                Name = order[i].Name, FullPath = order[i].FullPath, IsDir = true, Depth = order[i].Depth,
            };
            probes[i].Children.AddRange(ReadEntries(order[i]));
        });
        // One git call per root answers for every re-read folder; per-folder calls only if git refuses the batch.
        foreach (var group in probes.GroupBy(p => Roots.First(r => Paths.IsSameOrInside(p.FullPath, r.FullPath)).FullPath))
            if (!MarkIgnoredBatch(group.Key, group.ToList()))
                Parallel.ForEach(group, new ParallelOptions { MaxDegreeOfParallelism = 8 }, MarkIgnored);
        var fresh = probes.Select(p => p.Children).ToArray();
        for (int i = 0; i < order.Count; i++)
        {
            var node = order[i];
            var old = node.Children.ToDictionary(c => c.Name + (c.IsDir ? "/" : ""), StringComparer.Ordinal);
            node.Children.Clear();
            foreach (var f in fresh[i])
            {
                if (old.TryGetValue(f.Name + (f.IsDir ? "/" : ""), out var keep) && keep.IsLink == f.IsLink)
                {
                    keep.Ignored = f.Ignored;
                    keep.WriteUtc = f.WriteUtc;
                    if (!f.IsDir) keep.Size = f.Size;
                    node.Children.Add(keep);
                }
                else
                    node.Children.Add(new TreeNode
                    {
                        Name = f.Name, FullPath = f.FullPath, IsDir = f.IsDir, IsLink = f.IsLink,
                        Depth = node.Depth + 1, Parent = node, Ignored = f.Ignored,
                        Size = f.Size, SizeExact = f.SizeExact, WriteUtc = f.WriteUtc, IsNew = true,
                    });
            }
            StartSizing(node, sync: false);
            SortChildren(node);
        }
        RebuildVisible();
        if (selected != null)
        {
            int idx = Visible.FindIndex(v => Paths.Same(v.FullPath, selected.FullPath));
            if (idx >= 0) { SelectedIndex = idx; EnsureVisible(); }
        }
        return true;
    }
}

/// <summary>
/// Steppable recursive folder sizing. Folders share the walk round-robin, so a
/// limited step gives each a fair lower bound. Results stay private until
/// Snapshot, so a background step never races the UI. Symlinked folders are
/// not entered, unreadable ones are skipped, and a total cap keeps even a
/// whole-home walk finite.
/// </summary>
internal sealed class FolderSizer : IDisposable
{
    private const int TotalEntryBudget = 2_000_000;

    private sealed class Work(TreeNode node, DateTime writeUtc)
    {
        public readonly TreeNode Node = node;
        public readonly DateTime WriteUtc = writeUtc;
        public readonly int Generation = ++node.SizeGeneration; // on the UI thread, like every node access
        public readonly Stack<string> Todo = new();
        public IEnumerator<FileSystemInfo>? Current; // directory enumeration in progress (resumable)
        public long Partial;
        public bool Complete => Todo.Count == 0 && Current == null;
    }

    private readonly List<Work> _work = new();
    private int _spent;

    public FolderSizer(IEnumerable<(TreeNode Node, DateTime WriteUtc)> dirs)
    {
        foreach (var (node, mtime) in dirs)
        {
            var w = new Work(node, mtime);
            if (!node.IsLink) w.Todo.Push(node.FullPath);
            _work.Add(w);
        }
    }

    public void Dispose()
    {
        foreach (var w in _work) { w.Current?.Dispose(); w.Current = null; }
    }

    /// <summary>True when no further progress will be made (done or capped).</summary>
    public bool Settled => _spent >= TotalEntryBudget || _work.All(w => w.Complete);

    /// <summary>Advance the walk by up to maxEntries entries (and till the deadline). Returns Settled.</summary>
    public bool Step(int maxEntries, long deadlineTickMs = long.MaxValue, CancellationToken ct = default)
    {
        int budget = Math.Min(maxEntries, TotalEntryBudget - _spent);
        bool progress = true;
        while (budget > 0 && progress && !ct.IsCancellationRequested)
        {
            progress = false;
            foreach (var w in _work)
            {
                if (budget <= 0 || w.Complete) continue;
                progress = true;
                budget = DrainOneDirectory(w, budget, deadlineTickMs, ct);
            }
        }
        return Settled;
    }

    /// <summary>Process one directory of this folder (resuming where a previous step paused).</summary>
    private int DrainOneDirectory(Work w, int budget, long deadlineTickMs, CancellationToken ct)
    {
        if (w.Current == null)
        {
            if (w.Todo.Count == 0) return budget;
            string dir = w.Todo.Pop();
            try { w.Current = new DirectoryInfo(dir).EnumerateFileSystemInfos().GetEnumerator(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return budget; }
        }
        var en = w.Current;
        while (budget > 0 && !ct.IsCancellationRequested)
        {
            FileSystemInfo e;
            try
            {
                if (!en.MoveNext()) { en.Dispose(); w.Current = null; return budget; }
                e = en.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                en.Dispose();
                w.Current = null;
                return budget;
            }
            budget--;
            if ((++_spent & 0xFF) == 0 && Environment.TickCount64 > deadlineTickMs) return budget;
            if (e is FileInfo fi)
            {
                try { w.Partial += fi.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* vanished mid-walk */ }
            }
            else if ((e.Attributes & FileAttributes.ReparsePoint) == 0)
                w.Todo.Push(e.FullName);
        }
        return budget;
    }

    /// <summary>Current sizes: exact where Complete, a lower bound elsewhere; Generation — the node's when counting began.</summary>
    public List<(TreeNode Node, long Size, DateTime WriteUtc, bool Complete, int Generation)> Snapshot() =>
        _work.Select(w => (w.Node, w.Partial, w.WriteUtc, w.Complete, w.Generation)).ToList();
}
