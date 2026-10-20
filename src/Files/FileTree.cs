using System.Diagnostics;

/// <summary>Tree node: a folder or a file. Root folders have Parent == null.</summary>
public sealed class TreeNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDir { get; init; }
    public required int Depth { get; init; }
    public TreeNode? Parent { get; init; }

    /// <summary>Hidden from git by .gitignore (also .git and .gitignore themselves) — drawn dimmed.</summary>
    public bool Ignored { get; set; }

    /// <summary>
    /// Size in bytes for sorting: a file's length; a folder's recursive total
    /// (approximate when the scan budget runs out on huge trees).
    /// </summary>
    public long Size { get; set; }

    /// <summary>True when Size is final; false while it is still a lower-bound estimate.</summary>
    public bool SizeExact { get; set; }
    public bool Expanded { get; set; }
    public bool ChildrenLoaded { get; set; }
    public List<TreeNode> Children { get; } = new();

    public bool IsRoot => Parent == null;
}

/// <summary>
/// Left panel: one or more root folders, each with its own hierarchy.
/// Roots are visible as top rows and collapse like regular folders.
/// Subdirectories are loaded lazily on first expansion.
/// </summary>
public sealed class FileTree
{
    public List<TreeNode> Roots { get; private set; } = new();

    /// <summary>Exact folder sizes from finished scans, valid while the folder's own mtime stands.</summary>
    private readonly Dictionary<string, (long Size, DateTime WriteUtc)> _sizeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(TreeNode Parent, FolderSizer Sizer)> _unfinishedSizing = new();

    public IReadOnlyList<string> RootPaths => Roots.Select(r => r.FullPath).ToList();
    public List<TreeNode> Visible { get; private set; } = new();
    public int SelectedIndex { get; private set; }
    public int ScrollOffset { get; private set; }

    /// <summary>Viewport height, updated on each redraw.</summary>
    public int ViewHeight { get; set; } = 20;

    public FileTree(string rootPath) : this(new[] { rootPath }) { }

    public FileTree(IEnumerable<string> rootPaths)
    {
        foreach (var p in rootPaths)
        {
            var root = CreateRoot(p);
            if (Roots.All(r => !string.Equals(r.FullPath, root.FullPath, StringComparison.OrdinalIgnoreCase)))
                Roots.Add(root);
        }
        if (Roots.Count == 0)
            Roots.Add(CreateRoot(Directory.GetCurrentDirectory()));
        RebuildVisible();
    }

    private TreeNode CreateRoot(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
        try
        {
            var dirMtimes = new Dictionary<TreeNode, DateTime>();
            foreach (var e in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos())
            {
                var child = new TreeNode
                {
                    Name = e.Name,
                    FullPath = e.FullName,
                    IsDir = e is DirectoryInfo,
                    Depth = node.Depth + 1,
                    Parent = node,
                    Size = e is FileInfo fi ? SafeLength(fi) : 0,
                    SizeExact = e is FileInfo,
                };
                node.Children.Add(child);
                if (e is DirectoryInfo di) dirMtimes[child] = di.LastWriteTimeUtc;
            }
            MarkIgnored(node);
            StartSizing(node, dirMtimes);
            SortChildren(node);
        }
        catch (UnauthorizedAccessException) { /* folder not accessible — just empty */ }
        catch (IOException) { /* folder may have disappeared */ }
    }

    /// <summary>Order: normal entries first, git-ignored (dimmed) last;
    /// within each group folders before files, largest first, name breaks ties.</summary>
    private static void SortChildren(TreeNode node)
    {
        var sorted = node.Children
            .OrderBy(c => c.Ignored)
            .ThenByDescending(c => c.IsDir)
            .ThenByDescending(c => c.Size)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
    }

    /// <summary>
    /// Sizes only drive the sort order, so a load must never block on them:
    /// cached folders are free, the rest get a small synchronous budget
    /// (exact for small folders, a lower bound for huge ones) and any
    /// remainder is queued for background refinement via TakeUnfinishedSizing.
    /// </summary>
    private void StartSizing(TreeNode parent, Dictionary<TreeNode, DateTime> dirMtimes)
    {
        List<(TreeNode, DateTime)>? need = null;
        foreach (var (child, mtime) in dirMtimes)
        {
            if (_sizeCache.TryGetValue(child.FullPath, out var hit) && hit.WriteUtc == mtime)
            {
                child.Size = hit.Size;
                child.SizeExact = true;
            }
            else
                (need ??= new()).Add((child, mtime));
        }
        if (need == null) return;
        var sizer = new FolderSizer(need);
        bool settled = sizer.Step(SyncSizeEntryBudget, Environment.TickCount64 + SyncSizeMsBudget);
        ApplySizer(sizer);
        if (settled) sizer.Dispose();
        else _unfinishedSizing.Enqueue((parent, sizer));
    }

    private void ApplySizer(FolderSizer sizer)
    {
        foreach (var (node, size, mtime, complete) in sizer.Snapshot())
        {
            node.Size = size;
            node.SizeExact = complete;
            if (!complete) continue;
            if (_sizeCache.Count > 8192) _sizeCache.Clear();
            _sizeCache[node.FullPath] = (size, mtime);
        }
    }

    /// <summary>Next folder-sizing job for the background (null when none).</summary>
    internal (TreeNode Parent, FolderSizer Sizer)? TakeUnfinishedSizing() =>
        _unfinishedSizing.Count > 0 ? _unfinishedSizing.Dequeue() : null;

    /// <summary>Apply background-refined sizes and re-sort, keeping the selected node selected.</summary>
    internal void CompleteSizing(TreeNode parent, FolderSizer sizer)
    {
        var selected = Selected;
        ApplySizer(sizer);
        SortChildren(parent);
        RebuildVisible();
        if (selected != null) SelectNode(selected);
    }

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    // Synchronous sizing allowance per directory load; the rest is refined in the background.
    private const int SyncSizeEntryBudget = 1_500;
    private const int SyncSizeMsBudget = 30;

    /// <summary>
    /// Mark children that git ignores, asking git itself (`git check-ignore`)
    /// so nested .gitignore files, negations and global excludes all match
    /// real git behaviour. Outside a repository, or without git, nothing is
    /// marked. The .git directory and .gitignore files are marked too:
    /// check-ignore does not report them, but they are service entries.
    /// </summary>
    private static void MarkIgnored(TreeNode dir)
    {
        if (dir.Children.Count == 0) return;
        foreach (var c in dir.Children)
            if (c is { IsDir: true, Name: ".git" } or { IsDir: false, Name: ".gitignore" })
                c.Ignored = true;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "check-ignore --stdin -z",
                WorkingDirectory = dir.FullPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc == null) return;
            var reading = proc.StandardOutput.ReadToEndAsync();
            foreach (var c in dir.Children)
            {
                proc.StandardInput.Write(c.Name + (c.IsDir ? "/" : ""));
                proc.StandardInput.Write('\0');
            }
            proc.StandardInput.Close();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(); } catch (InvalidOperationException) { }
                return;
            }
            if (proc.ExitCode is not (0 or 1) || !reading.IsCompletedSuccessfully) return;
            var ignored = new HashSet<string>(
                reading.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in dir.Children)
                if (ignored.Contains(c.Name + (c.IsDir ? "/" : ""))) c.Ignored = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // git is unavailable — everything simply stays unmarked.
        }
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
        string full = Path.GetFullPath(path);
        var existing = Roots.FirstOrDefault(
            r => string.Equals(r.FullPath, full, StringComparison.OrdinalIgnoreCase));
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
        int i = Roots.FindIndex(r => string.Equals(r.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        bool expanded = Roots[i].Expanded;
        var root = CreateRoot(newPath);
        root.Expanded = expanded;
        Roots[i] = root;
        RebuildVisible();
        SelectNode(root);
        return true;
    }

    /// <summary>Select a node by full path in any root, expanding its parents.</summary>
    public void SelectPath(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        foreach (var root in Roots)
        {
            string rel = Path.GetRelativePath(root.FullPath, full);
            if (rel == ".")
            {
                RebuildVisible();
                SelectNode(root);
                return;
            }
            if (rel.StartsWith("..")) continue;

            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = root;
            bool ok = true;
            foreach (var part in parts)
            {
                LoadChildren(current);
                current.Expanded = true;
                var next = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
                if (next == null) { ok = false; break; }
                current = next;
            }
            if (!ok) continue;
            RebuildVisible();
            SelectNode(current);
            return;
        }
    }

    private static void CollectExpanded(TreeNode n, HashSet<string> into)
    {
        if (n.Expanded) into.Add(n.FullPath);
        foreach (var c in n.Children) CollectExpanded(c, into);
    }

    private void RestoreExpanded(TreeNode n, HashSet<string> expanded)
    {
        foreach (var c in n.Children)
            if (c.IsDir && expanded.Contains(c.FullPath))
            {
                c.Expanded = true;
                LoadChildren(c);
                RestoreExpanded(c, expanded);
            }
    }

    /// <summary>Re-read all roots from disk, keeping expanded folders and the selection.</summary>
    public void Refresh()
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Roots) CollectExpanded(r, expanded);
        string? selectedPath = Selected?.FullPath;

        Roots = RootPaths.Select(CreateRoot).ToList();
        foreach (var r in Roots)
        {
            r.Expanded = expanded.Contains(r.FullPath);
            if (r.Expanded) RestoreExpanded(r, expanded);
        }
        RebuildVisible();
        if (selectedPath != null)
        {
            int idx = Visible.FindIndex(v => v.FullPath == selectedPath);
            if (idx >= 0) { SelectedIndex = idx; EnsureVisible(); }
        }
    }
}

/// <summary>
/// Steppable recursive folder sizing for sort order. Folders share the walk
/// round-robin, so a limited step gives every folder a fair partial estimate
/// (a lower bound) instead of the first folder eating the whole budget.
/// Accumulates privately — callers copy results onto nodes via Snapshot(),
/// so a background step never races the UI. Symlinked folders are never
/// descended into, unreadable subtrees are skipped, and a generous total
/// cap keeps even a whole-home walk finite (sizes stay approximate past it).
/// </summary>
internal sealed class FolderSizer : IDisposable
{
    private const int TotalEntryBudget = 2_000_000;

    private sealed class Work(TreeNode node, DateTime writeUtc)
    {
        public TreeNode Node = node;
        public DateTime WriteUtc = writeUtc;
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
            try
            {
                if ((File.GetAttributes(node.FullPath) & FileAttributes.ReparsePoint) == 0)
                    w.Todo.Push(node.FullPath);
            }
            catch (IOException) { /* vanished — stays zero and complete */ }
            catch (UnauthorizedAccessException) { /* same */ }
            _work.Add(w);
        }
    }

    public void Dispose()
    {
        foreach (var w in _work) { w.Current?.Dispose(); w.Current = null; }
    }

    /// <summary>True when no further progress will be made (done, capped or cancelled).</summary>
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
            catch (IOException) { return budget; }
            catch (UnauthorizedAccessException) { return budget; }
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
            catch (IOException) { en.Dispose(); w.Current = null; return budget; }
            catch (UnauthorizedAccessException) { en.Dispose(); w.Current = null; return budget; }
            budget--;
            if ((++_spent & 0xFF) == 0 && Environment.TickCount64 > deadlineTickMs) return budget;
            if (e is FileInfo fi)
            {
                try { w.Partial += fi.Length; }
                catch (IOException) { /* vanished mid-walk */ }
                catch (UnauthorizedAccessException) { /* same */ }
            }
            else if ((e.Attributes & FileAttributes.ReparsePoint) == 0)
                w.Todo.Push(e.FullName);
        }
        return budget;
    }

    /// <summary>Current sizes: exact where Complete, a lower bound elsewhere.</summary>
    public List<(TreeNode Node, long Size, DateTime WriteUtc, bool Complete)> Snapshot() =>
        _work.Select(w => (w.Node, w.Partial, w.WriteUtc, w.Complete)).ToList();
}
