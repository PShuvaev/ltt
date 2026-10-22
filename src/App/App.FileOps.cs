/// <summary>Creating, renaming, deleting, copying and moving files/folders from the tree panel.</summary>
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
    /// of them — "a/b/c.txt" creates folders too) and `alsoDir` (one an entry
    /// left), then select `path` unless told not to.
    /// </summary>
    private void ShowInTree(string path, string? alsoDir = null, bool select = true)
    {
        var dirs = new List<string>();
        for (string? d = Path.GetDirectoryName(Paths.Normalize(path)); d != null; d = Path.GetDirectoryName(d))
            dirs.Add(d);
        if (alsoDir != null) dirs.Add(alsoDir);
        _tree.RefreshDirs(dirs);
        if (select) _tree.SelectPath(path);
        FilesChanged();
    }

    /// <summary>After our own file operation: search must look again.</summary>
    private void FilesChanged()
    {
        _liveSourceStale = true;
        FuzzySearch.InvalidateFileLists();
        _changes.RefreshAll();
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
                if (!existed) _changes.Created(p);
                ShowInTree(p);
                _status = existed ? $"Folder already exists: {Tilde(p)}" : $"Folder created: {Tilde(p)}";
                return;
            }

            if (Directory.Exists(p)) { _status = $"That is a folder, not a file: {p}"; return; }
            if (File.Exists(p))
            {
                ShowInTree(p);
                TryOpen(p, 0);
                _status = $"The file already existed — opened it: {Tilde(p)}";
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            using (new FileStream(p, FileMode.CreateNew, FileAccess.Write)) { }
            _changes.Created(p);
            ShowInTree(p);
            TryOpen(p, 0);
            _status = $"File created and opened: {Tilde(p)}";
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
        if (DirtyTabInTheWay(oldPath, newPath, isDir) is { } held)
        {
            _status = $"Name held by unsaved edits in the tab {held.Name} — save or close it first.";
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

        MoveTabs(oldPath, newPath, isDir);
        if (_tree.ReplaceRootPath(oldPath, newPath)) FilesChanged();
        else ShowInTree(newPath);
        SyncWatcher();
        _status = $"Renamed: {Path.GetFileName(oldPath)} → {newName}";
    }

    /// <summary>
    /// Open tabs of a moved file, or of the files in a moved folder, follow it. A tab already on
    /// the new path is one whose file is gone: it gives way, unless it holds unsaved edits (a
    /// rename or a paste avoids that name) — then the tab that would land on it stays behind.
    /// </summary>
    private void MoveTabs(string oldPath, string newPath, bool isDir, IReadOnlySet<TextEditor>? inSync = null)
    {
        foreach (var (tab, to) in Followers(oldPath, newPath, isDir))
        {
            if (TabAt(to, besides: tab) is { } gone)
            {
                if (gone.Editor.Dirty) continue;
                _tabs.Close(gone);
            }
            tab.Editor.RenameTo(to, inSync?.Contains(tab.Editor) == true);
            tab.Editor.SetBaseline(Baseline(to));
        }
    }

    /// <summary>The tabs a move takes along, and where each one lands.</summary>
    private List<(EditorTab Tab, string To)> Followers(string oldPath, string newPath, bool isDir)
    {
        var list = new List<(EditorTab, string)>();
        foreach (var tab in _tabs.Tabs)
            if (tab.Editor.FilePath is { } fp)
            {
                if (Paths.Same(fp, oldPath)) list.Add((tab, newPath));
                else if (isDir && Paths.IsInside(fp, oldPath)) list.Add((tab, newPath + fp[oldPath.Length..]));
            }
        return list;
    }

    private EditorTab? TabAt(string path, EditorTab besides) =>
        _tabs.Tabs.FirstOrDefault(t => t != besides && t.Editor.FilePath is { } p && Paths.SameReal(p, path));

    /// <summary>A tab with unsaved edits where a move would bring another tab: a name in use, though not on disk.</summary>
    private EditorTab? DirtyTabInTheWay(string oldPath, string newPath, bool isDir) =>
        Followers(oldPath, newPath, isDir).Select(f => TabAt(f.To, f.Tab)).FirstOrDefault(t => t is { Editor.Dirty: true });

    // ---- Copy and move: Ctrl+C / Ctrl+X picks an entry up, Ctrl+V pastes it into a folder of any root ----

    private sealed record PickedUp(string Path, bool IsDir, bool IsLink, bool Cut)
    {
        public string Label => System.IO.Path.GetFileName(Path) + (IsDir ? "/" : "");
    }

    /// <summary>A paste still at work: a big copy goes on in the background. InSync — tabs that knew their file's disk version.</summary>
    private sealed record PasteJob(PickedUp What, string To, string? SelectedAt, IReadOnlySet<TextEditor> InSync, Task Task,
        FileOps.CopyProgress Progress, CancellationTokenSource Cts);

    private PickedUp? _picked;
    private PasteJob? _paste;

    /// <summary>Ctrl+C / Ctrl+X in the tree: pick the selected entry up for Ctrl+V; a copy also puts its path on the clipboard.</summary>
    private void PickUp(bool cut)
    {
        if (_tree.Selected is not { } node) return;
        if (cut && IsDeleteProtected(node.FullPath, _tree.RootPaths))
        {
            _status = node.IsRoot
                ? "A root stays where it is: Ctrl+C copies it instead."
                : $"Protected path - never moved: {node.FullPath}";
            return;
        }
        _picked = new PickedUp(node.FullPath, node.IsDir, node.IsLink, cut);
        if (!cut) Clipboard.Copy(node.FullPath);
        _status = cut
            ? $"Cut {_picked.Label} — Ctrl+V in a folder of any root moves it there."
            : $"Copied {_picked.Label} and its path — Ctrl+V in a folder of any root pastes it.";
    }

    /// <summary>
    /// Ctrl+V in the tree: copy or move what was picked up into the selected
    /// folder (or the selected file's folder). Nothing is overwritten: a taken
    /// name gets a number.
    /// </summary>
    private void Paste()
    {
        if (_picked is not { } what)
        {
            _status = "Ctrl+V here pastes a file or folder picked up with Ctrl+C or Ctrl+X; text goes into the editor (Tab).";
            return;
        }
        if (_paste != null) { _status = "Still copying — Esc stops it."; return; }
        string dir = TargetDirectory();
        if (!Path.Exists(what.Path)) { _picked = null; _status = $"{what.Label} is no longer on disk."; return; }
        // A folder pasted into itself would copy its own copy without end.
        if (what is { IsDir: true, IsLink: false }
            && (Paths.IsSameOrInside(dir, what.Path) || Paths.IsSameOrInside(Paths.Real(dir), Paths.Real(what.Path))))
        {
            _status = $"{what.Label} cannot go inside itself.";
            return;
        }
        if (what.Cut && Paths.Same(Path.GetDirectoryName(what.Path), dir)) { _status = $"{what.Label} is already here."; return; }
        if (what.Cut && IsDeleteProtected(what.Path, _tree.RootPaths)) { _status = $"Protected path - never moved: {what.Path}"; return; }

        string to = FileOps.FreeName(dir, Path.GetFileName(what.Path), what.IsDir,
            alsoTaken: what.Cut ? p => DirtyTabInTheWay(what.Path, p, what.IsDir) != null : null);
        // A tab that matches its file now still does after the move, even where another disk rounds the time.
        var inSync = what.Cut
            ? _tabs.Tabs.Select(t => t.Editor).Where(e => e.FilePath is { } fp && Paths.IsSameOrInside(fp, what.Path)
                && e.CheckDisk() == TextEditor.DiskState.Same).ToHashSet()
            : new HashSet<TextEditor>();
        var progress = new FileOps.CopyProgress();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var task = Task.Run(() =>
        {
            if (what.Cut) FileOps.Relocate(what.Path, to, what.IsDir, progress, ct);
            else FileOps.Copy(what.Path, to, progress, ct);
        });
        _paste = new PasteJob(what, to, _tree.Selected?.FullPath, inSync, task, progress, cts);
        Task.WaitAny(new[] { task }, 150); // most pastes are done by then
        PollPaste();
    }

    /// <summary>
    /// A finished paste: the tree shows the result (selected, unless the
    /// selection moved on meanwhile), tabs follow a move, the status says how
    /// it went. True while a paste runs too — its progress is on screen.
    /// </summary>
    private bool PollPaste()
    {
        if (_paste is not { } job) return false;
        if (!job.Task.IsCompleted) return true;
        _paste = null;
        bool stopped = job.Cts.IsCancellationRequested;
        job.Cts.Dispose();
        var (what, p) = (job.What, job.Progress);
        bool moved = what.Cut && !Path.Exists(what.Path) && Path.Exists(job.To);
        if (Path.Exists(job.To)) _changes.Created(job.To);
        if (moved)
        {
            _picked = null;
            MoveTabs(what.Path, job.To, what.IsDir, job.InSync);
        }
        string name = Path.GetFileName(job.To);
        string into = Path.GetFileName(Path.GetDirectoryName(job.To)) + "/"
                      + (name == Path.GetFileName(what.Path) ? "" : $" as {name}");
        string problems = (p.Skipped > 0 ? $" Skipped {p.Skipped} pipe(s) or device(s)." : "")
                          + (p.Failed > 0 ? $" {p.Failed} failed: {p.FirstError}" : "");
        string error = what.Cut ? "Move error: " : "Copy error: ";
        _status = job.Task.Exception?.InnerException is { } ex ? error + ex.Message
            : moved ? $"Moved {what.Label} into {into}."
            : stopped ? $"Stopped: the copy in {into} is incomplete." + (what.Cut ? " The original stays." : "")
            : p is { Copied: 0, Failed: > 0 } ? error + p.FirstError
            : p is { Copied: 0, Skipped: > 0 } && !what.IsDir // a lone pipe or device: there is nothing to copy
                ? $"Nothing copied into {into}." + (what.Cut ? " The original stays." : "") + problems
            : what.Cut ? $"Copied {what.Label} into {into}, but the original stays:" + problems
            : $"Copied {what.Label} into {into}" + (what.IsDir ? $" ({p.Copied} files)." : ".") + problems;
        ShowInTree(job.To, alsoDir: moved ? Path.GetDirectoryName(what.Path) : null,
            select: Paths.Same(_tree.Selected?.FullPath, job.SelectedAt));
        return true;
    }

    /// <summary>The help line while a paste runs.</summary>
    private static string PasteProgress(PasteJob job) =>
        $" {(job.What.Cut ? "Moving" : "Copying")} {job.What.Label} into {Path.GetFileName(Path.GetDirectoryName(job.To))}/: "
        + $"{job.Progress.Copied} files so far · Esc in the tree stops it";

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
