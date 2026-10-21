/// <summary>Creating, renaming and deleting files/folders from the tree panel.</summary>
internal sealed partial class App
{
    /// <summary>Directory to create in: the selected node's folder or the open file's folder.</summary>
    private string TargetDirectory()
    {
        if (_focus == Focus.Editor && _editor.HasFile)
            return Path.GetDirectoryName(_editor.FilePath!) ?? _tree.Roots[0].FullPath;
        var sel = _tree.Selected;
        if (sel == null) return _tree.Roots[0].FullPath;
        if (sel.IsDir) return sel.FullPath;
        return sel.Parent?.FullPath ?? Path.GetDirectoryName(sel.FullPath) ?? _tree.Roots[0].FullPath;
    }

    /// <summary>A typed path: ~ expanded, relative ones taken from the prompt's base folder.</summary>
    private string ResolveInputPath(string raw)
    {
        string expanded = PathCompletion.ExpandHome(raw);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(_pathBaseDir, expanded));
    }

    /// <summary>
    /// Re-read the loaded folders on the way to `path` (a new entry may sit in any
    /// of them — "a/b/c.txt" creates folders too), then select `path`.
    /// </summary>
    private void ShowInTree(string path)
    {
        var dirs = new List<string>();
        for (string? d = Path.GetDirectoryName(Paths.Normalize(path)); d != null; d = Path.GetDirectoryName(d))
            dirs.Add(d);
        _tree.RefreshDirs(dirs);
        _tree.SelectPath(path);
        FilesChanged();
    }

    /// <summary>After our own file operation: search must look again.</summary>
    private void FilesChanged()
    {
        _liveSourceStale = true;
        FuzzySearch.InvalidateFileLists();
    }

    private void CreateFromInput(bool makeFolderRequested)
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.Normal;
        _focus = Focus.Tree;
        if (raw.Length == 0) { _status = "Creation cancelled."; return; }

        bool makeFolder = makeFolderRequested
            || raw.EndsWith(Path.DirectorySeparatorChar) || raw.EndsWith(Path.AltDirectorySeparatorChar);
        string trimmed = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) { _status = "Creation cancelled."; return; }
        string p = ResolveInputPath(trimmed);

        try
        {
            if (makeFolder)
            {
                if (File.Exists(p)) { _status = $"A file with this name already exists: {p}"; return; }
                bool existed = Directory.Exists(p);
                Directory.CreateDirectory(p);
                ShowInTree(p);
                _status = existed ? $"Folder already exists: {p}" : $"Folder created: {p}";
                return;
            }

            if (Directory.Exists(p)) { _status = $"That is a folder, not a file: {p}"; return; }
            if (File.Exists(p))
            {
                ShowInTree(p);
                TryOpen(p, 0);
                _status = $"The file already existed — opened it: {p}";
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            using (new FileStream(p, FileMode.CreateNew, FileAccess.Write)) { }
            ShowInTree(p);
            TryOpen(p, 0);
            _status = $"File created and opened: {p}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status = "Creation error: " + ex.Message;
        }
    }

    /// <summary>Ctrl+R in either panel: rename a tree node, or the file open in the editor.</summary>
    private void StartRenamePath(string fullPath, bool isDir)
    {
        if (Path.GetDirectoryName(Paths.Normalize(fullPath)) == null)
        {
            _status = "The filesystem root cannot be renamed.";
            return;
        }
        _pendingRenamePath = Paths.Normalize(fullPath);
        _pendingRenameIsDir = isDir;
        _renameFromEditor = _focus == Focus.Editor;
        OpenPathInput(PathPurpose.Rename, Path.GetFileName(_pendingRenamePath));
    }

    private void RenameFromInput()
    {
        string newName = _pathInput.ToString().Trim();
        string? oldPath = _pendingRenamePath;
        bool isDir = _pendingRenameIsDir;
        _pendingRenamePath = null;
        _mode = UiMode.Normal;
        _focus = _renameFromEditor && _editor.HasFile ? Focus.Editor : Focus.Tree;
        _renameFromEditor = false;
        if (oldPath == null) return;
        if (newName.Length == 0 || newName is "." or ".."
            || newName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _status = "Enter just the new name, with no path or special characters.";
            return;
        }

        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            _status = "The name is unchanged.";
            return;
        }
        bool caseOnly = Paths.Same(oldPath, newPath); // the same entry on this file system, new spelling
        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            _status = $"Name already taken: {newPath}";
            return;
        }

        try
        {
            FileOps.Move(oldPath, newPath, isDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Rename error: " + ex.Message;
            return;
        }

        // Open tabs of the renamed file/folder follow along.
        foreach (var tab in _tabs.Tabs)
        {
            string? fp = tab.Editor.FilePath;
            if (fp == null) continue;
            if (Paths.Same(fp, oldPath)) tab.Editor.RenameTo(newPath);
            else if (isDir && Paths.IsInside(fp, oldPath)) tab.Editor.RenameTo(newPath + fp[oldPath.Length..]);
        }

        if (_tree.ReplaceRootPath(oldPath, newPath)) FilesChanged();
        else ShowInTree(newPath);
        SyncWatcher();
        _status = $"Renamed: {Path.GetFileName(oldPath)} → {newName}";
    }

    /// <summary>
    /// Paths that must never be deleted from disk through the UI: panel roots
    /// and the folders above them, the home folder and everything above it,
    /// and filesystem roots.
    /// </summary>
    internal static bool IsDeleteProtected(string path, IEnumerable<string> rootPaths)
    {
        string full = Paths.Normalize(path);
        if (Path.GetDirectoryName(full) is not { } parent) return true; // filesystem root
        // Compared as written and with symlinks resolved (/tmp/x is /private/tmp/x on macOS);
        // the path's own name is kept, as deleting a symlink removes just the link.
        string real = Path.Combine(Paths.Real(parent), Path.GetFileName(full));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var anchors = rootPaths.Append(home).Where(a => a.Length > 0)
            .SelectMany(a => new[] { Paths.Normalize(a), Paths.Real(a) });
        return anchors.Any(a => Paths.IsSameOrInside(a, full) || Paths.IsSameOrInside(a, real));
    }

    private void RequestDeleteSelected()
    {
        var node = _tree.Selected;
        if (node == null) return;
        if (IsDeleteProtected(node.FullPath, _tree.RootPaths))
        {
            _status = node.IsRoot
                ? "Roots are never deleted from disk. Plain Delete removes a root from the panel."
                : $"Protected path - never deleted from disk: {node.FullPath}";
            return;
        }
        string path = node.FullPath;
        bool isDir = node.IsDir;
        int unsaved = _tabs.Tabs.Count(t => t.Editor.Dirty && t.Editor.FilePath is { } fp && Paths.IsSameOrInside(fp, path));
        string loss = unsaved > 0 ? $" {unsaved} open tab(s) there have unsaved edits — they go too!" : "";
        Ask($"Delete {(isDir ? "folder with all its contents" : "file")} \"{node.Name}\" from disk forever?{loss}  D deletes · Esc cancels",
            ('d', () => ExecuteDelete(path, isDir)),
            ('y', () => ExecuteDelete(path, isDir)));
    }

    private void ExecuteDelete(string path, bool isDir)
    {
        if (IsDeleteProtected(path, _tree.RootPaths))
        {
            _status = $"Protected path - not deleted: {path}";
            return;
        }

        try
        {
            if (isDir) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Delete error: " + ex.Message;
            return;
        }

        var doomed = _tabs.Tabs.Where(t => t.Editor.FilePath is { } fp && Paths.IsSameOrInside(fp, path)).ToList();
        foreach (var t in doomed) _tabs.Close(t);
        if (_tabs.Tabs.Count == 0) _focus = Focus.Tree;

        _tree.RefreshDirs(new[] { Path.GetDirectoryName(path)! });
        FilesChanged();
        SyncWatcher();
        _status = $"Deleted from disk: {path}" + (doomed.Count > 0 ? $" (tabs closed: {doomed.Count})" : "");
    }
}
