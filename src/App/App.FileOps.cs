namespace ConsoleEditor;

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

    private void CreateFromInput(bool makeFolderRequested)
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.Normal;
        _focus = Focus.Tree;
        if (raw.Length == 0) { _status = "Creation cancelled."; return; }

        bool makeFolder = makeFolderRequested
            || raw.EndsWith(Path.DirectorySeparatorChar) || raw.EndsWith(Path.AltDirectorySeparatorChar);
        string expanded = PathCompletion.ExpandHome(raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (expanded.Length == 0) { _status = "Creation cancelled."; return; }
        string p = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(_pathBaseDir, expanded));

        try
        {
            if (makeFolder)
            {
                if (File.Exists(p)) { _status = $"A file with this name already exists: {p}"; return; }
                bool existed = Directory.Exists(p);
                Directory.CreateDirectory(p);
                ResetLiveSearch();
                _tree.Refresh();
                _tree.SelectPath(p);
                _status = existed ? $"Folder already exists: {p}" : $"Folder created: {p}";
                return;
            }

            if (Directory.Exists(p)) { _status = $"That is a folder, not a file: {p}"; return; }
            if (File.Exists(p))
            {
                _tree.Refresh();
                TryOpen(p, 0);
                _status = $"The file already existed — opened it: {p}";
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            using (File.Create(p)) { }
            ResetLiveSearch();
            _tree.Refresh();
            _tree.SelectPath(p);
            TryOpen(p, 0);
            _status = $"File created and opened: {p}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Creation error: " + ex.Message;
        }
    }

    private void StartRename()
    {
        var node = _tree.Selected;
        if (node == null) return;
        StartRenamePath(node.FullPath, node.IsDir);
    }

    /// <summary>F2 in either panel: rename a tree node, or the file open in the editor.</summary>
    private void StartRenamePath(string fullPath, bool isDir)
    {
        if (Path.GetDirectoryName(fullPath) == null)
        {
            _status = "The filesystem root cannot be renamed.";
            return;
        }
        _pendingRenamePath = fullPath;
        _pendingRenameIsDir = isDir;
        _renameFromEditor = _focus == Focus.Editor;
        OpenPathInput(PathPurpose.Rename, Path.GetFileName(fullPath));
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
        bool caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
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
            if (string.Equals(fp, oldPath, StringComparison.OrdinalIgnoreCase))
                tab.Editor.RenameTo(newPath);
            else if (isDir && fp.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                tab.Editor.RenameTo(newPath + fp[oldPath.Length..]);
        }

        ResetLiveSearch();
        if (!_tree.ReplaceRootPath(oldPath, newPath))
        {
            _tree.Refresh();
            _tree.SelectPath(newPath);
        }
        SyncWatcher();
        _status = $"Renamed: {Path.GetFileName(oldPath)} → {newName}";
    }

    private void RequestDeleteSelected()
    {
        var node = _tree.Selected;
        if (node == null) return;
        _pendingDeletePath = node.FullPath;
        _pendingDeleteIsDir = node.IsDir;
        _pending = PendingKind.DeleteNode;
        _mode = UiMode.Confirm;
    }

    private void ExecuteDeletePending()
    {
        string? path = _pendingDeletePath;
        bool isDir = _pendingDeleteIsDir;
        _pendingDeletePath = null;
        if (path == null) return;

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

        // Tabs of the deleted file (and of files inside a deleted folder) are closed.
        var doomed = _tabs.Tabs.Where(t =>
        {
            string? fp = t.Editor.FilePath;
            return fp != null
                && (string.Equals(fp, path, StringComparison.OrdinalIgnoreCase)
                    || fp.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        foreach (var t in doomed) _tabs.ForceClose(t);
        if (_tabs.Tabs.Count == 0) _focus = Focus.Tree;

        var root = _tree.Roots.FirstOrDefault(
            r => string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (root != null && _tree.Roots.Count > 1) _tree.RemoveRoot(root);
        else _tree.Refresh();

        SyncWatcher();
        ResetLiveSearch();
        _status = $"Deleted from disk: {path}" + (doomed.Count > 0 ? $" (tabs closed: {doomed.Count})" : "");
    }
}
