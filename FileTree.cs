namespace ConsoleEditor;

/// <summary>Tree node: a folder or a file. Root folders have Parent == null.</summary>
public sealed class TreeNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDir { get; init; }
    public required int Depth { get; init; }
    public TreeNode? Parent { get; init; }

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
            var entries = new DirectoryInfo(node.FullPath).EnumerateFileSystemInfos()
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                node.Children.Add(new TreeNode
                {
                    Name = e.Name,
                    FullPath = e.FullName,
                    IsDir = e is DirectoryInfo,
                    Depth = node.Depth + 1,
                    Parent = node,
                });
            }
        }
        catch (UnauthorizedAccessException) { /* folder not accessible — just empty */ }
        catch (IOException) { /* folder may have disappeared */ }
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
