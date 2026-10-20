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

    private static TreeNode CreateRoot(string path)
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

    private static void LoadChildren(TreeNode node)
    {
        if (!node.IsDir || node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;
        try
        {
            foreach (var e in new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos())
            {
                node.Children.Add(new TreeNode
                {
                    Name = e.Name,
                    FullPath = e.FullName,
                    IsDir = e is DirectoryInfo,
                    Depth = node.Depth + 1,
                    Parent = node,
                    Size = e is FileInfo fi ? SafeLength(fi) : 0,
                });
            }
            MarkIgnored(node);
            ComputeFolderSizes(node.Children.Where(c => c.IsDir).ToList());
            // Order: normal entries first, git-ignored (dimmed) last;
            // within each group folders before files, largest first, name breaks ties.
            var sorted = node.Children
                .OrderBy(c => c.Ignored)
                .ThenByDescending(c => c.IsDir)
                .ThenByDescending(c => c.Size)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            node.Children.Clear();
            node.Children.AddRange(sorted);
        }
        catch (UnauthorizedAccessException) { /* folder not accessible — just empty */ }
        catch (IOException) { /* folder may have disappeared */ }
    }

    private static long SafeLength(FileInfo fi)
    {
        try { return fi.Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Entries one directory load may scan while sizing child folders.</summary>
    private const int SizeScanBudget = 20_000;

    /// <summary>
    /// Recursive sizes of child folders, for size sorting. All folders share
    /// one entry budget, consumed round-robin: on huge trees the load stays
    /// fast and every folder gets a fair partial estimate instead of the
    /// first folder eating the whole budget. Symlinked folders are never
    /// descended into (cycles), unreadable subtrees are skipped.
    /// </summary>
    private static void ComputeFolderSizes(List<TreeNode> dirs)
    {
        var pending = new List<(TreeNode Node, Stack<string> Todo)>();
        foreach (var d in dirs)
        {
            var todo = new Stack<string>();
            try
            {
                if ((File.GetAttributes(d.FullPath) & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            todo.Push(d.FullPath);
            pending.Add((d, todo));
        }

        int budget = SizeScanBudget;
        bool progress = true;
        while (budget > 0 && progress)
        {
            progress = false;
            foreach (var (node, todo) in pending)
            {
                if (budget <= 0 || todo.Count == 0) continue;
                progress = true;
                string dir = todo.Pop();
                try
                {
                    foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        if (budget-- <= 0) break;
                        if (e is FileInfo fi) node.Size += SafeLength(fi);
                        else if ((e.Attributes & FileAttributes.ReparsePoint) == 0) todo.Push(e.FullName);
                    }
                }
                catch (IOException) { /* vanished mid-walk — the partial size stands */ }
                catch (UnauthorizedAccessException) { /* skip the unreadable subtree */ }
            }
        }
    }

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

    private static void RestoreExpanded(TreeNode n, HashSet<string> expanded)
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
