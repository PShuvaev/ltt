using System.Text;

namespace ConsoleEditor;

internal enum Focus { Tree, Editor }
internal enum UiMode { Normal, SearchInput, SearchResults, Confirm, PathInput }
internal enum PendingKind { None, CloseTab, Quit, DeleteNode }
internal enum PathPurpose { AddRoot, CreateFile, CreateFolder, Rename }

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.InputEncoding = Encoding.UTF8; } catch (IOException) { }

        // Non-interactive modes (handy for tests and scripts)
        if (args.Contains("--self-test"))
            return SelfTest.Run();
        if (args.Length >= 2 && args[0] == "--search")
        {
            var searchRoots = args.Skip(2).DefaultIfEmpty(Directory.GetCurrentDirectory()).ToList();
            var hits = FuzzySearch.SearchMany(searchRoots, args[1]);
            foreach (var h in hits)
                Console.WriteLine($"{h.RelativePath}:{h.LineNumber}: {h.LineText.Trim()}");
            Console.WriteLine($"-- found: {hits.Count}");
            return hits.Count > 0 ? 0 : 1;
        }

        // Arguments: any number of folders (left-panel roots) and files (opened as tabs).
        var roots = new List<string>();
        var initialFiles = new List<string>();
        void AddRootArg(string dir)
        {
            if (!roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) roots.Add(dir);
        }
        foreach (var arg in args)
        {
            if (arg.StartsWith('-')) continue;
            if (File.Exists(arg))
            {
                string f = Path.GetFullPath(arg);
                initialFiles.Add(f);
                AddRootArg(Path.GetDirectoryName(f)!);
            }
            else if (Directory.Exists(arg))
            {
                AddRootArg(Path.GetFullPath(arg));
            }
            else
            {
                Console.Error.WriteLine($"Path not found: {arg}");
                return 2;
            }
        }
        if (roots.Count == 0) roots.Add(Directory.GetCurrentDirectory());

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("The editor needs an interactive terminal.");
            Console.Error.WriteLine("Without a terminal, search is available: dotnet run -- --search \"query\" [folder]");
            return 2;
        }

        // Raw input with the mouse on Unix; on Windows or on failure — plain ReadKey without the mouse.
        ITerminalInput input;
        try { input = OperatingSystem.IsWindows() ? new ConsoleInput() : new RawTerminalInput(); }
        catch (InvalidOperationException) { input = new ConsoleInput(); }

        try
        {
            var app = new App(roots, initialFiles);
            Console.CursorVisible = false;
            Console.Clear();
            app.Run(input);
            return 0;
        }
        finally
        {
            input.Dispose(); // turn the mouse off and restore termios
            Console.Write("\x1b[?1006l\x1b[?1002l\x1b[?1000l");
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.Clear();
        }
    }
}

/// <summary>
/// A two-panel console editor: on the left — the file tree of the current
/// folder, on the right — an editor with syntax highlighting.
/// Shift+F / F7 / Ctrl+F — fuzzy content search across all files of the directory.
/// In the editor without a selection Shift+F types the letter F; with text
/// selected, Shift+F searches for exactly that text without overwriting it.
/// </summary>
internal sealed partial class App
{
    private readonly FileTree _tree;
    private readonly TabManager _tabs = new();
    private readonly TextEditor _emptyEditor = new();
    private TextEditor _editor => _tabs.Active?.Editor ?? _emptyEditor;

    private Focus _focus = Focus.Tree;
    private UiMode _mode = UiMode.Normal;
    private bool _running = true;
    private string _status = "Ready. Enter opens a file.";

    private readonly StringBuilder _searchQuery = new();
    private readonly StringBuilder _pathInput = new(); // path input for a new root folder
    private List<string> _pathCandidates = new();      // path completion candidates (full input lines)
    private int _pathCandidateIndex = -1;              // candidate selected while cycling with Tab/↑↓
    private string _pathGhost = "";                    // grey ghost-text continuation after the typed text
    private List<SearchHit> _hits = new();
    private int _hitIndex;
    private int _hitScroll;
    private bool _resultsVisible; // bottom panel with search results

    private PendingKind _pending = PendingKind.None;
    private EditorTab? _pendingTab;
    private PathPurpose _pathPurpose = PathPurpose.AddRoot;
    private string _pathBaseDir = "";          // directory a file/folder is created in
    private string? _pendingRenamePath;        // what is being renamed (F2)
    private bool _pendingRenameIsDir;
    private bool _renameFromEditor;            // F2 came from the editor — return focus there
    private string? _pendingDeletePath;        // what is being deleted from disk (Shift+Delete)
    private bool _pendingDeleteIsDir;

    // Live search: the previous query and its results, for incremental refinement.
    private string _liveQuery = "";
    private List<SearchHit> _liveSource = new();

    // Tab-bar cells of the last frame — for mouse clicks.
    private readonly List<(int Index, int X0, int X1, int CloseX)> _tabCells = new();

    private int _lastW, _lastH;
    // Geometry of the last frame — needed for the cursor and mouse hit-testing.
    private int _editorTextX, _contentTop, _statusY;
    private int _leftW, _editorX, _gutter = 4, _mainContentH = 1;
    private int _treeTop = 1, _treeContentH = 1; // the tree starts right below the title, with no header of its own
    private int _resultsTop, _resultsRowsTop, _resultsRowCount;
    private int _prevEditorViewW, _prevEditorViewH;
    private DateTime _lastClickAt = DateTime.MinValue;
    private int _lastClickX = -1, _lastClickY = -1;
    private bool _dragging; // a mouse drag selection is in progress
    private int _treeWheelNumerator; // accumulated fraction of tree scrolling (in fifths of the old step)

    // Watching the left panel's folders for external changes.
    private const int WatchTickMs = 200; // how often the main loop wakes up without input
    private FolderWatcher? _watcher;
    private readonly HashSet<string> _warnedUnwatchable = new(StringComparer.OrdinalIgnoreCase);

    // Path selection in the top line (title): text, path segments, range.
    private string _titleText = "";
    private (int Start, int Len) _titlePathSeg;
    private int? _titleAnchor;
    private int _titleSelFrom = -1, _titleSelTo = -1;
    private string _titleSelFor = ""; // the title the selection was made in
    private bool _titleDragging, _titleDragMoved;

    public App(IReadOnlyList<string> roots, IReadOnlyList<string> initialFiles)
    {
        _tree = new FileTree(roots);
        foreach (var f in initialFiles)
        {
            _tree.SelectPath(f);
            TryOpen(f, 0);
        }
        if (initialFiles.Count > 0) // keep the first of them active
        {
            _tree.SelectPath(initialFiles[0]);
            TryOpen(initialFiles[0], 0, activateOnly: true);
        }
    }

    private void ResetLiveSearch()
    {
        _liveQuery = "";
        _liveSource = new List<SearchHit>();
    }

    private void DispatchEvent(InputEvent? ev)
    {
        switch (ev)
        {
            case KeyInput k: HandleKey(k.Key); break;
            case MouseInput m: HandleMouse(m); break;
        }
    }

    public void Run(ITerminalInput input)
    {
        _watcher = new FolderWatcher();
        SyncWatcher();
        try
        {
            Draw();
            while (_running)
            {
                bool dirty = false;
                if (input.TryRead(WatchTickMs, out var ev))
                {
                    DispatchEvent(ev);
                    dirty = true;
                    // Pump whatever else is already queued (trackpads deliver
                    // dozens of wheel events per frame) so a single redraw
                    // serves the whole burst instead of lagging behind input.
                    for (int pumped = 0; pumped < 64 && _running
                         && input.TryRead(0, out var extra); pumped++)
                        DispatchEvent(extra);
                }
                if (Console.WindowWidth != _lastW || Console.WindowHeight != _lastH)
                    dirty = true; // the terminal was resized while we waited for input
                _watcher?.PollIfDue(); // roots without a live FileSystemWatcher are polled by signature
                if (ApplyExternalChanges()) dirty = true;
                if (dirty) Draw();
            }
        }
        finally
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Watchers must match the panel roots; warn once about unwatchable ones.</summary>
    private void SyncWatcher(bool forceRecreate = false)
    {
        if (_watcher == null) return;
        _watcher.SyncRoots(_tree.RootPaths, forceRecreate);
        foreach (string bad in _watcher.UnwatchableRoots)
            if (_warnedUnwatchable.Add(bad))
                _status = $"Could not watch folder {bad} for changes — refresh it manually (F5 in the tree).";
    }

    /// <summary>
    /// A batch of external changes is due (debounce expired) — re-read the
    /// tree from disk. We create/rename/delete files ourselves too, and their
    /// "echo" arrives here as well; so the status is only changed when the
    /// visible tree rows or the selection actually changed. Returns true
    /// when the screen needs a redraw.
    /// </summary>
    private bool ApplyExternalChanges()
    {
        if (_watcher == null || !_watcher.TryConsumeDue(out string summary, out bool overflow))
            return false;

        var before = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        string? beforeSel = _tree.Selected?.FullPath;
        ResetLiveSearch();
        _tree.Refresh();
        SyncWatcher(forceRecreate: overflow);
        var after = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        bool changed = !before.SequenceEqual(after) || beforeSel != _tree.Selected?.FullPath;
        if (changed)
            _status = $"External change: {summary} — left panel refreshed.";
        return changed;
    }

    // ============================ Open / save ============================

    /// <summary>
    /// Open a file in a tab: an already-open one is just activated
    /// (activateOnly — without jumping to a line, for Enter in the tree),
    /// a new one gets a tab. Dirty neighbouring tabs do not interfere.
    /// </summary>
    private void TryOpen(string path, int lineZeroBased, bool activateOnly = false)
    {
        string full = Path.GetFullPath(path);
        int existing = _tabs.IndexOfPath(full);
        if (existing >= 0)
        {
            _tabs.Activate(existing);
            if (!activateOnly) _editor.GoToLine(lineZeroBased);
            _mode = UiMode.Normal;
            _focus = Focus.Editor;
            _status = activateOnly
                ? $"Tab: {_editor.DisplayName}"
                : $"Line {lineZeroBased + 1}.";
            return;
        }

        var ed = new TextEditor();
        if (_prevEditorViewW > 0) { ed.ViewWidth = _prevEditorViewW; ed.ViewHeight = _prevEditorViewH; }
        try
        {
            ed.Open(full, lineZeroBased);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _mode = UiMode.Normal;
            _status = "Error: " + ex.Message;
            return;
        }
        _tabs.Add(ed);
        _mode = UiMode.Normal;
        _focus = Focus.Editor;
        _status = $"Opened {ed.DisplayName} ({ed.LineCount} lines), tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}. Ctrl+P pins, Ctrl+W closes.";
    }

    private void ActivateTab(int index, bool announce = false)
    {
        _tabs.Activate(index);
        if (announce && _tabs.Active is { } t)
            _status = $"Tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}: {t.Name}{(t.Pinned ? " ⚑ pinned" : "")}{(t.Editor.Dirty ? " ●" : "")}";
    }

    private void SwitchTab(int delta)
    {
        if (_tabs.Tabs.Count == 0) { _status = "No open tabs."; return; }
        _tabs.ActivateRelative(delta);
        _focus = Focus.Editor;
        ActivateTab(_tabs.ActiveIndex, announce: true);
    }

    private void TogglePin(EditorTab tab)
    {
        _tabs.TogglePin(tab);
        _status = tab.Pinned
            ? $"Tab \"{tab.Name}\" is pinned ⚑ — Ctrl+W will not close it. Ctrl+P or right-click the tab to unpin."
            : $"Tab \"{tab.Name}\" unpinned.";
    }

    private void CloseTab(EditorTab tab)
    {
        switch (_tabs.RequestClose(tab))
        {
            case CloseResult.IsPinned:
                _status = $"Tab \"{tab.Name}\" is pinned ⚑ — unpin it first (Ctrl+P).";
                break;
            case CloseResult.IsDirty:
                _pending = PendingKind.CloseTab;
                _pendingTab = tab;
                _mode = UiMode.Confirm;
                break;
            case CloseResult.Closed:
                AfterTabClosed(tab.Name);
                break;
        }
    }

    private void AfterTabClosed(string name)
    {
        if (_tabs.Tabs.Count == 0 && _focus == Focus.Editor) _focus = Focus.Tree;
        _status = $"Closed tab \"{name}\"." + (_tabs.Tabs.Count > 0 ? $" Tabs left: {_tabs.Tabs.Count}." : "");
    }

    private void Save()
    {
        if (!_editor.HasFile) { _status = "No file is open."; return; }
        try
        {
            _editor.Save();
            _status = $"Saved: {_editor.DisplayName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Save error: " + ex.Message;
        }
    }

    private void TryQuit()
    {
        if (_tabs.AnyDirty)
        {
            _pending = PendingKind.Quit;
            _mode = UiMode.Confirm;
        }
        else _running = false;
    }

    /// <summary>Save whatever the confirmation is about. false — a write error occurred.</summary>
    private bool SaveForConfirm()
    {
        try
        {
            if (_pending == PendingKind.Quit)
            {
                foreach (var t in _tabs.Tabs.Where(t => t.Editor.Dirty)) t.Editor.Save();
                return !_tabs.AnyDirty;
            }
            if (_pending == PendingKind.CloseTab && _pendingTab != null)
            {
                _pendingTab.Editor.Save();
                return !_pendingTab.Editor.Dirty;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status = "Save error: " + ex.Message;
            return false;
        }
    }

    private void DoPending()
    {
        var kind = _pending;
        _pending = PendingKind.None;
        _mode = UiMode.Normal;
        switch (kind)
        {
            case PendingKind.CloseTab when _pendingTab != null:
                string name = _pendingTab.Name;
                _tabs.ForceClose(_pendingTab);
                _pendingTab = null;
                AfterTabClosed(name);
                break;
            case PendingKind.Quit:
                _running = false;
                break;
            case PendingKind.DeleteNode:
                ExecuteDeletePending();
                break;
        }
    }

    /// <summary>
    /// Live search while typing: works from two characters and updates the
    /// bottom panel on every keystroke. If the query only grew (a prefix of
    /// it was the previous query), previous results are just refined without
    /// re-scanning the disk; on erase or mid-query edits — a full new search.
    /// </summary>
    private void RunLiveSearch()
    {
        string query = _searchQuery.ToString().Trim();
        if (query.Length < 2)
        {
            _hits = new List<SearchHit>();
            _liveSource = new List<SearchHit>();
            _liveQuery = "";
            _hitIndex = 0;
            _hitScroll = 0;
            _resultsVisible = false;
            return;
        }

        if (_liveQuery.Length >= 2 && query.StartsWith(_liveQuery, StringComparison.OrdinalIgnoreCase))
            _liveSource = FuzzySearch.Refine(_liveSource, query);
        else
            _liveSource = FuzzySearch.SearchMany(_tree.RootPaths, query);

        _liveQuery = query;
        _hits = _liveSource;
        _hitIndex = 0;
        _hitScroll = 0;
        _resultsVisible = _hits.Count > 0;
    }

    private void OpenHit(SearchHit hit)
    {
        _tree.SelectPath(hit.FilePath);
        TryOpen(hit.FilePath, hit.LineNumber - 1); // the bottom panel stays visible
    }

    /// <summary>F8/F9: jump to the next/previous hit right from the editor.</summary>
    private void JumpHit(int delta)
    {
        if (_hits.Count == 0) { _status = "Run a search first (Shift+F)."; return; }
        _resultsVisible = true;
        _hitIndex = Math.Clamp(_hitIndex + delta, 0, _hits.Count - 1);
        OpenHit(_hits[_hitIndex]);
        if (_mode == UiMode.Normal)
            _status = $"Hit {_hitIndex + 1}/{_hits.Count}: {_hits[_hitIndex].RelativePath}:{_hits[_hitIndex].LineNumber} · F8/F9 — next/prev";
    }
}
