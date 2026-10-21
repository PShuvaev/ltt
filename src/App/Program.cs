using System.Runtime.InteropServices;
using System.Text;

internal enum Focus { Tree, Editor }
internal enum SearchScope { CurrentFile, SelectedFolder, AllFolders }
internal enum UiMode { Normal, SearchInput, SearchResults, Confirm, PathInput }
internal enum PathPurpose { AddRoot, CreateFile, CreateFolder, Rename, GoTo }

internal static class Program
{
    private static App? _app;
    private static ITerminalInput? _input;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.InputEncoding = Encoding.UTF8; } catch (IOException) { }

#if DEBUG
        if (args.Contains("--self-test")) // checks without a terminal; Debug builds only
            return SelfTest.Run();
#endif
        // Arguments: any number of folders (left-panel roots) and files (opened as
        // tabs; `file:line` jumps to the line). A file inside a folder that is
        // already a root does not add its own folder as another root.
        var dirRoots = new List<string>();
        var files = new List<(string Path, int Line)>();
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {arg}. Usage: ltt [folder…] [file[:line]…]");
                return 2;
            }
            if (Directory.Exists(arg)) { dirRoots.Add(Paths.Normalize(arg)); continue; }
            if (App.ParseLocation(arg) is { } loc && File.Exists(loc.Path))
            {
                files.Add((Path.GetFullPath(loc.Path), loc.Line));
                continue;
            }
            Console.Error.WriteLine($"Path not found: {arg}");
            return 2;
        }
        var roots = new List<string>();
        foreach (var r in dirRoots)
            if (!roots.Contains(r, Paths.Comparer)) roots.Add(r);
        foreach (var (f, _) in files)
        {
            string dir = Path.GetDirectoryName(f)!;
            if (!roots.Any(r => Paths.IsSameOrInside(dir, r))) roots.Add(dir);
        }
        if (roots.Count == 0) roots.Add(Paths.Normalize(Directory.GetCurrentDirectory()));

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("The editor needs an interactive terminal.");
            return 2;
        }

        // Raw input with the mouse on Unix; on Windows or on failure — plain ReadKey without the mouse.
        ITerminalInput input;
        bool raw = !OperatingSystem.IsWindows();
        try { input = raw ? new RawTerminalInput() : new ConsoleInput(); }
        catch (InvalidOperationException) { input = new ConsoleInput(); raw = false; }
        _input = input;
        if (OperatingSystem.IsWindows()) Console.TreatControlCAsInput = true; // Ctrl+C copies, never kills

        // A kill signal or a closed terminal must not take unsaved edits with it.
        var signals = new List<PosixSignalRegistration>();
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
        {
            if (OperatingSystem.IsWindows() && sig == PosixSignal.SIGINT) continue;
            try { signals.Add(PosixSignalRegistration.Create(sig, OnSignal)); }
            catch (PlatformNotSupportedException) { }
        }
        // A resize that ends at the old size still needs a full repaint: the terminal may have dropped cells.
        if (!OperatingSystem.IsWindows())
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => App.Resized = true));

        Exception? crash = null;
        TerminalModes.Enter(raw);
        try
        {
            _app = new App(roots, files);
            _app.Run(input);
        }
        catch (Exception ex)
        {
            crash = ex;
            _app?.SaveRecovery();
        }
        finally
        {
            input.Dispose(); // restore termios
            TerminalModes.Leave();
            foreach (var s in signals) s.Dispose();
        }
        if (crash != null)
        {
            string log = Recovery.LogCrash(crash);
            Say($"ltt crashed: {crash.GetType().Name}: {crash.Message}");
            if (log.Length > 0) Say($"Details: {log}");
            Say($"Unsaved edits (if any) were saved to {Recovery.Dir} and reopen on the next start.");
            return 1;
        }
        if (_app?.ExitNote is { } note) Say(note);
        return _app?.ExitCode ?? 0;
    }

    /// <summary>A line for the shell — unless the terminal is gone (hangup), which is fine too.</summary>
    private static void Say(string line)
    {
        try { Console.Error.WriteLine(line); }
        catch (IOException) { }
    }

    private static void OnSignal(PosixSignalContext ctx)
    {
        var app = _app;
        if (app != null)
        {
            ctx.Cancel = true;
            int code = 128 + ctx.Signal switch { PosixSignal.SIGHUP => 1, PosixSignal.SIGINT => 2, _ => 15 };
            if (app.RequestEmergencyExit($"signal {ctx.Signal}", code, waitMs: 2000)) return;
            // The UI thread did not answer in time: secure the edits from here.
            app.SaveRecovery();
            ctx.Cancel = false;
        }
        // Let the signal end the process, with the terminal as it was.
        _input?.Dispose();
        TerminalModes.Leave();
    }
}

/// <summary>
/// The terminal modes of a session: the alternate screen (the shell's screen
/// and scrollback stay untouched and come back on exit), mouse reports,
/// bracketed paste and the kitty keyboard protocol. Leave is idempotent and
/// safe to call from a signal handler.
/// </summary>
internal static class TerminalModes
{
    private static int _entered;
    private static bool _raw;
    private static readonly Stream Stdout = Console.OpenStandardOutput();

    public static void Enter(bool raw)
    {
        _raw = raw;
        if (OperatingSystem.IsWindows()) EnableVirtualTerminal();
        // Kitty keyboard protocol, flags 1|4|8|16: disambiguate, report alternate
        // (shifted/base-layout) keys, report all keys as CSI u, report associated
        // text. Terminals without the protocol simply ignore this.
        Write("\x1b[?1049h\x1b[?25l" + (raw ? "\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[>29u" : ""));
        Interlocked.Exchange(ref _entered, 1);
    }

    public static void Leave()
    {
        if (Interlocked.Exchange(ref _entered, 0) == 0) return;
        // Also back to normal cursor keys and keypad: the runtime switches both to application mode.
        Write((_raw ? "\x1b[<u\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l" : "")
              + "\x1b[0m\x1b[?25h\x1b[?1049l\x1b[?1l\x1b>");
    }

    private static void Write(string s)
    {
        try
        {
            Stdout.Write(Encoding.UTF8.GetBytes(s));
            Stdout.Flush();
        }
        catch (IOException) { /* the terminal is gone */ }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>Windows consoles interpret escape sequences only when asked to.</summary>
    private static void EnableVirtualTerminal()
    {
        IntPtr handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (GetConsoleMode(handle, out uint mode)) SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
}

/// <summary>A two-panel console editor: the folder tree on the left, tabs of open files on the right.</summary>
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

    /// <summary>A line for the shell after the session (e.g. where unsaved edits went).</summary>
    public string? ExitNote { get; private set; }

    /// <summary>The process exit code: 128 + the signal number when one ended the session.</summary>
    public int ExitCode { get; private set; }

    /// <summary>Set by SIGWINCH: repaint everything on the next tick.</summary>
    internal static volatile bool Resized;

    private readonly StringBuilder _searchQuery = new();
    private readonly StringBuilder _pathInput = new();
    private List<string> _pathCandidates = new();      // path completion candidates (full input lines)
    private int _pathCandidateIndex = -1;              // candidate selected while cycling with Tab/↑↓
    private string _pathGhost = "";                    // grey ghost-text continuation after the typed text
    private int _promptCursorX;                        // where the prompt's cursor was drawn
    private List<SearchHit> _hits = new();
    private bool _hitsTruncated;                       // more lines matched than the panel holds
    private int _hitsSkipped;                          // files too big for a folder search
    private SearchScope _searchScope = SearchScope.AllFolders;
    private string? _searchTargetPath; // file/folder while the search scope is not AllFolders
    private int _hitIndex;
    private int _hitScroll;
    private int _openedHitIndex = -1;  // the hit last opened, so Ctrl+G moves on from it
    private bool _resultsVisible;      // bottom panel with search results

    /// <summary>A question in the status line: a key per answer (lower-case letters); Esc cancels.</summary>
    private sealed record Confirmation(string Question, (char Key, Action Act)[] Choices);
    private Confirmation? _confirm;

    private PathPurpose _pathPurpose = PathPurpose.AddRoot;
    private string _pathBaseDir = "";          // directory relative paths in a prompt start from
    private string? _pendingRenamePath;        // what is being renamed (Ctrl+R)
    private bool _pendingRenameIsDir;
    private bool _renameFromEditor;            // rename came from the editor — return focus there

    // Live search: the previous query and its results, for incremental refinement.
    private string _liveQuery = "";
    private List<SearchHit> _liveSource = new();
    private bool _liveTruncated;   // _liveSource is capped: refining it could miss matches
    private int _liveSkipped;      // files the scan left out for their size
    private bool _liveSourceStale; // files changed since _liveSource was scanned
    // Full folder scans run off the UI thread; a generation counter makes sure
    // only the newest scan's results are ever applied.
    private (Task<SearchResult> Task, int Gen, string Query)? _pendingSearch;
    private CancellationTokenSource? _searchCts;
    private int _searchGen;

    // Background folder-size counting (see FileTree.TakeUnfinishedSizing).
    private (Task Task, TreeNode Parent, FolderSizer Sizer)? _pendingSizing;
    private CancellationTokenSource? _sizingCts;

    // Open tabs are compared with the disk this often.
    private long _diskCheckAt;
    private const int DiskCheckIntervalMs = 1000;

    // Tab-bar cells of the last frame — for mouse clicks.
    private readonly List<(EditorTab Tab, int X0, int X1, int CloseX)> _tabCells = new();

    private int _lastW, _lastH;
    // Geometry of the last frame — needed for the cursor and mouse hit-testing.
    private int _editorTextX, _contentTop, _statusY;
    private int _leftW, _editorX, _gutter = 4, _mainContentH = 1;
    private int _treeTop = 1, _treeContentH = 1; // the tree starts right below the title
    private int _resultsTop, _resultsRowsTop, _resultsRowCount;
    private DateTime _lastClickAt = DateTime.MinValue;
    private int _lastClickX = -1, _lastClickY = -1;
    private TreeNode? _lastClickedNode; // the tree entry under the last click (double-click check)
    private bool _dragging; // a mouse drag selection is in progress
    private int _treeWheelNumerator; // see TreeWheelSteps
    private int _resultsWheelNumerator;
    private string? _titlePath; // the path shown in the title — a click copies it

    // Watching the left panel's folders for external changes.
    private const int WatchTickMs = 200; // how often the main loop wakes up without input
    private FolderWatcher? _watcher;
    private readonly HashSet<string> _warnedUnwatchable = new(Paths.Comparer);

    // A kill signal or a hangup asks the UI thread to secure unsaved edits and stop.
    private volatile string? _emergency;
    private readonly ManualResetEventSlim _emergencyDone = new(false);
    private int _drawFailures;

    public App(IReadOnlyList<string> roots, IReadOnlyList<(string Path, int Line)> initialFiles)
    {
        _tree = new FileTree(roots);
        foreach (var (f, line) in initialFiles)
        {
            _tree.SelectPath(f);
            TryOpen(f, line);
        }
        if (initialFiles.Count > 0) // keep the first of them active
        {
            _tree.SelectPath(initialFiles[0].Path);
            TryOpen(initialFiles[0].Path, 0, activateOnly: true);
        }
        RestoreRecovered();
    }

    // ============================ Main loop ============================

    public void Run(ITerminalInput input)
    {
        _watcher = new FolderWatcher();
        SyncWatcher();
        try
        {
            SafeDraw();
            while (_running)
            {
                bool dirty = false;
                if (input.TryRead(WatchTickMs, out var ev))
                {
                    SafeDispatch(ev);
                    dirty = true;
                    // Pump whatever else is already queued (trackpads deliver
                    // dozens of wheel events per frame) so a single redraw
                    // serves the whole burst instead of lagging behind input.
                    for (int pumped = 0; pumped < 64 && _running && _emergency == null
                         && input.TryRead(0, out var extra); pumped++)
                        SafeDispatch(extra);
                }
                if (_emergency != null) { EmergencyStop(); break; }
                if (Console.WindowWidth != _lastW || Console.WindowHeight != _lastH)
                    dirty = true; // the terminal was resized while we waited for input
                if (Resized)
                {
                    Resized = false;
                    _screen.Invalidate();
                    dirty = true;
                }
                _watcher?.PollIfDue(); // roots without a live FileSystemWatcher are polled by signature
                if (ApplyExternalChanges()) dirty = true;
                if (PollSearch()) dirty = true;
                if (PollSizing()) dirty = true;
                if (PollPaste()) dirty = true;
                StartSizingIfIdle();
                if (Environment.TickCount64 - _diskCheckAt >= DiskCheckIntervalMs)
                {
                    _diskCheckAt = Environment.TickCount64;
                    if (CheckOpenTabsOnDisk()) dirty = true;
                }
                if (dirty && _running) SafeDraw();
            }
            if (_emergency == null) Recovery.Clear(); // quit normally: nothing left to recover
        }
        finally
        {
            CancelSearch();
            CancelSizing();
            _paste?.Cts.Cancel();
            _watcher.Dispose();
            _watcher = null;
            _emergencyDone.Set();
        }
    }

    private void SafeDispatch(InputEvent? ev)
    {
        try
        {
            switch (ev)
            {
                case KeyInput k: HandleKey(k.Key); break;
                case MouseInput m: HandleMouse(m); break;
                case PasteInput p: HandlePaste(p.Text); break;
                case HangupInput: _emergency = "the terminal closed"; ExitCode = 129; break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A bug in one handler must not take the session down: back up the
            // unsaved edits, put the state back in range and carry on.
            int saved = SaveRecovery();
            string log = Recovery.LogCrash(ex);
            foreach (var t in _tabs.Tabs) t.Editor.ClampState();
            _mode = UiMode.Normal;
            _confirm = null;
            _status = $"Internal error ({ex.GetType().Name}: {ex.Message})"
                      + (saved > 0 ? $"; unsaved edits backed up to {Recovery.Dir}" : "")
                      + (log.Length > 0 ? $"; details in {log}" : "");
        }
    }

    private void SafeDraw()
    {
        try
        {
            Draw();
            _drawFailures = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ++_drawFailures < 3)
        {
            foreach (var t in _tabs.Tabs) t.Editor.ClampState();
            _screen.Invalidate();
            _status = $"Internal drawing error ({ex.GetType().Name}: {ex.Message}); details in {Recovery.LogCrash(ex)}";
        }
    }

    /// <summary>
    /// Back up every dirty tab to the recovery folder; returns how many. Safe from
    /// a signal handler too: each buffer is copied between two edits.
    /// </summary>
    public int SaveRecovery()
    {
        List<TextEditor> editors;
        try { editors = _tabs.Tabs.Select(t => t.Editor).ToList(); }
        catch (Exception) { return 0; } // the tab list changed under the copy
        return Recovery.Save(editors);
    }

    /// <summary>After a save, a discard or a reload: an earlier backup must not bring old edits back.</summary>
    private void RefreshRecovery()
    {
        if (Recovery.HasFiles) SaveRecovery();
    }

    /// <summary>From a signal handler: ask the UI thread to save and stop; false — it did not finish in time.</summary>
    public bool RequestEmergencyExit(string reason, int exitCode, int waitMs)
    {
        ExitCode = exitCode;
        _emergency = reason;
        return _emergencyDone.Wait(waitMs);
    }

    private void EmergencyStop()
    {
        int saved = SaveRecovery();
        if (saved > 0)
            ExitNote = $"ltt stopped ({_emergency}): {saved} unsaved tab(s) saved to {Recovery.Dir}; they reopen on the next start.";
        _running = false;
    }

    /// <summary>Reopen buffers saved by an interrupted session as unsaved tabs.</summary>
    private void RestoreRecovered()
    {
        var names = new List<string>();
        foreach (var (file, path, lines) in Recovery.Load())
        {
            try
            {
                int existing = _tabs.IndexOfPath(Path.GetFullPath(path));
                TextEditor ed;
                if (existing >= 0) ed = _tabs.Tabs[existing].Editor;
                else
                {
                    ed = new TextEditor();
                    try { ed.Open(path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                        ed.OpenMissing(path);
                    }
                }
                ed.ReplaceContent(lines);
                if (ed.Dirty)
                {
                    if (existing < 0) _tabs.Add(ed);
                    names.Add(ed.DisplayName);
                }
                Recovery.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (names.Count == 0) return;
        _focus = Focus.Editor;
        _status = $"Restored unsaved edits from an interrupted session: {string.Join(", ", names)} — Ctrl+S saves, Ctrl+W discards.";
    }

    /// <summary>Watchers must match the panel roots; warn once about unwatchable ones.</summary>
    private void SyncWatcher(bool forceRecreate = false)
    {
        if (_watcher == null) return;
        _watcher.SyncRoots(_tree.RootPaths, forceRecreate);
        foreach (string bad in _watcher.UnwatchableRoots)
            if (_warnedUnwatchable.Add(bad))
                _status = $"Could not watch folder {bad} for changes.";
    }

    /// <summary>
    /// A batch of external changes is due: the folders that changed are
    /// re-read and counted again, renamed files carry their tabs along, a
    /// running search is left alone (its results are only marked stale). Our
    /// own file operations echo here too, so the status changes only when the
    /// visible rows or the selection did. Returns true when the screen needs a redraw.
    /// </summary>
    private bool ApplyExternalChanges()
    {
        if (_watcher == null || !_watcher.TryConsumeDue(out var batch)) return false;
        _diskCheckAt = 0; // compare open tabs with the disk right away
        _liveSourceStale = true; // results can no longer be refined: lines may have changed
        bool redraw = FollowRenames(batch.Renames);
        if (batch.Dirs.Count == 0 && !batch.Overflow) return redraw;

        FuzzySearch.InvalidateFileLists();
        var before = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        string? beforeSel = _tree.Selected?.FullPath;
        if (batch.Overflow) _tree.Refresh();
        else _tree.RefreshDirs(batch.Dirs, batch.Deep);
        if (batch.Overflow) SyncWatcher(forceRecreate: batch.Restart);
        var after = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        bool changed = !before.SequenceEqual(after) || beforeSel != _tree.Selected?.FullPath;
        if (changed && batch.Summary.Length > 0)
            _status = $"External change: {batch.Summary} — left panel refreshed.";
        return changed || redraw;
    }

    /// <summary>Tabs of a file (or of files in a folder) renamed outside follow it to the new path.</summary>
    private bool FollowRenames(IReadOnlyList<(string Old, string New)> renames)
    {
        bool any = false;
        foreach (var (oldPath, newPath) in renames)
        {
            // Only a real move: "rename to a backup, write a new file" leaves the old path in place.
            if (File.Exists(oldPath) || Directory.Exists(oldPath)) continue;
            foreach (var tab in _tabs.Tabs)
            {
                string? fp = tab.Editor.FilePath;
                if (fp == null) continue;
                string? moved = Paths.Same(fp, oldPath) ? newPath
                    : Paths.IsInside(fp, oldPath) ? newPath + fp[oldPath.Length..]
                    : null;
                if (moved == null || !File.Exists(moved)) continue;
                // Moved over a file open in another tab: that tab sees the change, this one its file gone.
                if (_tabs.IndexOfPath(moved) >= 0) continue;
                tab.Editor.RenameTo(moved);
                _status = $"{Path.GetFileName(fp)} was moved to {moved} — its tab follows.";
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// Compare each tab with its file: a clean tab takes the new text (one undo
    /// step), a dirty one keeps its edits and is marked, so saving asks first.
    /// Returns whether anything changed.
    /// </summary>
    private bool CheckOpenTabsOnDisk()
    {
        bool changed = false;
        foreach (var tab in _tabs.Tabs)
        {
            var ed = tab.Editor;
            switch (ed.CheckDisk())
            {
                case TextEditor.DiskState.Missing when !ed.MissingOnDisk:
                    ed.MissingOnDisk = true;
                    _status = $"{ed.DisplayName} was deleted on disk — the tab keeps its text; saving would recreate the file.";
                    changed = true;
                    break;
                case TextEditor.DiskState.Same when ed.MissingOnDisk:
                    ed.MissingOnDisk = false;
                    changed = true;
                    break;
                case TextEditor.DiskState.Changed when !ed.Dirty:
                    try
                    {
                        ed.Reload();
                        _liveSourceStale = true;
                        _status = $"Reloaded {ed.DisplayName}: it changed on disk (Ctrl+Z brings back the previous text).";
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                        ed.ChangedOnDisk = true;
                        _status = $"{ed.DisplayName} changed on disk but cannot be read: {e.Message}";
                    }
                    changed = true;
                    break;
                case TextEditor.DiskState.Changed when !ed.ChangedOnDisk:
                    ed.ChangedOnDisk = true;
                    ed.MissingOnDisk = false;
                    _status = $"{ed.DisplayName} changed on disk — your unsaved edits are kept; saving will ask before overwriting.";
                    changed = true;
                    break;
            }
        }
        return changed;
    }

    /// <summary>Start counting folder sizes in the background when a load left them approximate.</summary>
    private void StartSizingIfIdle()
    {
        if (_pendingSizing != null) return;
        if (_tree.TakeUnfinishedSizing() is not { } job) return;
        _sizingCts?.Dispose();
        _sizingCts = new CancellationTokenSource();
        var ct = _sizingCts.Token;
        var sizer = job.Sizer;
        _pendingSizing = (Task.Run(() => sizer.Step(int.MaxValue, ct: ct)), job.Parent, sizer);
    }

    /// <summary>Apply counted folder sizes (re-sorts). True when the screen needs a redraw.</summary>
    private bool PollSizing()
    {
        if (_pendingSizing is not { } p || !p.Task.IsCompleted) return false;
        _pendingSizing = null;
        bool ok = p.Task.Status == TaskStatus.RanToCompletion;
        if (ok) _tree.CompleteSizing(p.Parent, p.Sizer);
        p.Sizer.Dispose();
        return ok;
    }

    private void CancelSizing()
    {
        _sizingCts?.Cancel();
        if (_pendingSizing is { } p) p.Task.ContinueWith(_ => p.Sizer.Dispose(), TaskScheduler.Default);
        _pendingSizing = null;
    }

    // ============================ Confirmations ============================

    /// <summary>Ask a question in the status line; each choice is a letter and what it does.</summary>
    private void Ask(string question, params (char Key, Action Act)[] choices)
    {
        _confirm = new Confirmation(question, choices);
        _mode = UiMode.Confirm;
    }

    // ============================ Open / save ============================

    /// <summary>
    /// Open a file in a tab, or activate its tab (activateOnly — without jumping
    /// to the line). A column (a search hit's index into the line) places the cursor.
    /// </summary>
    private void TryOpen(string path, int lineZeroBased, bool activateOnly = false, int column = -1)
    {
        string full = Path.GetFullPath(path);
        int existing = _tabs.IndexOfPath(full);
        if (existing >= 0)
        {
            _tabs.Activate(existing);
            if (!activateOnly) _editor.GoToLine(lineZeroBased, column);
            _mode = UiMode.Normal;
            _focus = Focus.Editor;
            _status = activateOnly
                ? $"Tab: {_editor.DisplayName}"
                : $"Line {lineZeroBased + 1}.";
            return;
        }

        var ed = new TextEditor();
        if (_editor.ViewWidth > 0) { ed.ViewWidth = _editor.ViewWidth; ed.ViewHeight = _editor.ViewHeight; }
        try
        {
            ed.Open(full, lineZeroBased);
            if (column >= 0) ed.GoToLine(lineZeroBased, column);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or NotSupportedException)
        {
            _mode = UiMode.Normal;
            _status = "Error: " + ex.Message;
            return;
        }
        _tabs.Add(ed);
        _mode = UiMode.Normal;
        _focus = Focus.Editor;
        string format = ed.Format.Describe();
        _status = $"Opened {ed.DisplayName} ({ed.LineCount} lines{(format.Length > 0 ? ", " + format : "")}), " +
                  $"tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}. Ctrl+W closes.";
    }

    private void ActivateTab(int index, bool announce = false)
    {
        _tabs.Activate(index);
        if (announce && _tabs.Active is { } t)
            _status = $"Tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}: {t.Name}{(t.Editor.Dirty ? " ●" : "")}";
    }

    private void SwitchTab(int delta)
    {
        if (_tabs.Tabs.Count == 0) { _status = "No open tabs."; return; }
        _tabs.ActivateRelative(delta);
        _focus = Focus.Editor;
        ActivateTab(_tabs.ActiveIndex, announce: true);
    }

    private void CloseTab(EditorTab tab)
    {
        if (!tab.Editor.Dirty) { CloseNow(tab); return; }
        Ask($"Tab \"{tab.Name}\" has changes!  S saves · D closes without saving · Esc cancels",
            ('s', () => SaveTab(tab, then: () => CloseNow(tab))),
            ('d', () => CloseNow(tab)));
    }

    private void CloseNow(EditorTab tab)
    {
        _tabs.Close(tab);
        if (_tabs.Tabs.Count == 0 && _focus == Focus.Editor) _focus = Focus.Tree;
        _status = $"Closed tab \"{tab.Name}\"." + (_tabs.Tabs.Count > 0 ? $" Tabs left: {_tabs.Tabs.Count}." : "");
        RefreshRecovery();
    }

    private void Save()
    {
        if (_tabs.Active is not { } tab) { _status = "No file is open."; return; }
        SaveTab(tab, then: null);
    }

    /// <summary>
    /// Save a tab, but never over a newer version on disk without asking: the
    /// file may have been changed, or deleted, by an agent since it was opened.
    /// `then` runs after a successful save.
    /// </summary>
    private void SaveTab(EditorTab tab, Action? then)
    {
        var ed = tab.Editor;
        var state = ed.CheckDisk();
        if (state == TextEditor.DiskState.Changed)
            Ask($"{tab.Name} changed on disk since it was opened!  S overwrites it · R takes the disk version (Ctrl+Z brings yours back) · Esc cancels",
                ('s', () => WriteTab(tab, then)),
                ('r', () => ReloadTab(tab)));
        else if (state == TextEditor.DiskState.Missing && ed.DiskStamp != null)
            Ask($"{tab.Name} was deleted on disk!  S saves it again · Esc cancels",
                ('s', () => WriteTab(tab, then)));
        else WriteTab(tab, then);
    }

    private void WriteTab(EditorTab tab, Action? then)
    {
        try
        {
            tab.Editor.Save();
            _status = $"Saved: {tab.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status = "Save error: " + ex.Message;
            return;
        }
        RefreshRecovery();
        then?.Invoke();
    }

    private void ReloadTab(EditorTab tab)
    {
        try
        {
            tab.Editor.Reload();
            _status = $"Took the disk version of {tab.Name}; Ctrl+Z brings back your edits.";
            RefreshRecovery();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _status = "Reload error: " + ex.Message;
        }
    }

    /// <summary>Save every dirty tab in turn (each may ask about the disk), then run `done`.</summary>
    private void SaveAllThen(Action done)
    {
        var next = _tabs.Tabs.FirstOrDefault(t => t.Editor.Dirty);
        if (next == null) { done(); return; }
        SaveTab(next, then: () => SaveAllThen(done));
    }

    private void TryQuit(bool stopCopy = false)
    {
        if (_paste != null && !stopCopy)
        {
            Ask("Files are still being copied!  Q stops the copy and quits · Esc keeps it going",
                ('q', () => TryQuit(stopCopy: true)));
            return;
        }
        if (!_tabs.AnyDirty) { _running = false; return; }
        Ask($"Unsaved tabs: {_tabs.DirtyCount}!  S saves all · D quits without saving · Esc cancels",
            ('s', () => SaveAllThen(() => _running = false)),
            ('d', () => _running = false));
    }

    // ============================ Search ============================

    private void ResetLiveSearch()
    {
        CancelSearch();
        _liveQuery = "";
        _liveSource = new List<SearchHit>();
        _liveTruncated = false;
        _liveSkipped = 0;
    }

    /// <summary>Drop any in-flight folder scan; its results will never be applied.</summary>
    private void CancelSearch()
    {
        _searchGen++;
        _pendingSearch = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    /// <summary>Apply a finished background scan, if it is still the current one.</summary>
    private bool PollSearch()
    {
        if (_pendingSearch is not { } p || !p.Task.IsCompleted) return false;
        _pendingSearch = null;
        _searchCts?.Dispose();
        _searchCts = null;
        if (p.Gen != _searchGen || p.Task.Status != TaskStatus.RanToCompletion) return false;
        SetLive(p.Task.Result, p.Query);
        ApplyHits();
        return true;
    }

    private void SetLive(SearchResult result, string query)
    {
        _liveSource = result.Hits;
        _liveTruncated = result.Truncated;
        _liveSkipped = result.SkippedLarge;
        _liveQuery = query;
    }

    private void ApplyHits()
    {
        _hits = _liveSource;
        _hitsTruncated = _liveTruncated;
        _hitsSkipped = _liveSkipped;
        _hitIndex = 0;
        _hitScroll = 0;
        _openedHitIndex = -1;
        _resultsVisible = _hits.Count > 0;
    }

    /// <summary>Snapshots of tabs with unsaved edits: a folder search reads them instead of the disk.</summary>
    private Dictionary<string, string[]> DirtyBuffers()
    {
        var buffers = new Dictionary<string, string[]>(Paths.Comparer);
        foreach (var t in _tabs.Tabs)
            if (t.Editor.Dirty && t.Editor.FilePath is { } path) buffers.TryAdd(path, t.Editor.Lines.ToArray());
        return buffers;
    }

    /// <summary>
    /// Live search while typing, from two characters on: a query that only
    /// grew refines the previous results when they were complete; anything
    /// else is a new search.
    /// </summary>
    private void RunLiveSearch()
    {
        string query = _searchQuery.ToString().Trim();
        if (query.Length < 2)
        {
            ResetLiveSearch();
            _hits = new List<SearchHit>();
            _hitsTruncated = false;
            _hitIndex = 0;
            _hitScroll = 0;
            _resultsVisible = false;
            return;
        }

        bool canRefine = !_liveTruncated && !_liveSourceStale && _liveQuery.Length >= 2
            && query.StartsWith(_liveQuery, StringComparison.OrdinalIgnoreCase);
        var scope = _searchScope;
        string? target = _searchTargetPath == null ? null : Path.GetFullPath(_searchTargetPath);
        var buffer = scope == SearchScope.CurrentFile && target != null && _tabs.IndexOfPath(target) is >= 0 and var t
            ? _tabs.Tabs[t].Editor.Lines : null;
        CancelSearch();
        if (canRefine || buffer != null)
        {
            // Refining ≤500 known hits and scanning one open file are instant: stay synchronous.
            SetLive(scope != SearchScope.CurrentFile ? new SearchResult(FuzzySearch.Refine(_liveSource, query), false, _liveSkipped)
                : canRefine ? new SearchResult(FuzzySearch.RefineInOrder(_liveSource, query), false)
                : FuzzySearch.SearchLines(target!, Path.GetFileName(target!), buffer!, query), query);
            if (!canRefine) _liveSourceStale = false;
            ApplyHits();
            return;
        }

        // A folder scan (or a file on disk, which may be huge) can take seconds,
        // so it runs in the background and is cancelled by the next keystroke.
        _liveSourceStale = false;
        int gen = _searchGen;
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var roots = _tree.RootPaths;
        var buffers = DirtyBuffers();
        var task = Task.Run(() => scope switch
        {
            _ when target == null && scope != SearchScope.AllFolders => SearchResult.Empty,
            SearchScope.CurrentFile => FuzzySearch.SearchFile(target!, Path.GetFileName(target!), query, ct: ct),
            SearchScope.SelectedFolder => FuzzySearch.Search(target!, query, ct: ct, buffers: buffers),
            _ => FuzzySearch.SearchMany(roots, query, ct: ct, buffers: buffers),
        });
        _pendingSearch = (task, gen, query);
    }

    /// <summary>
    /// The target Ctrl+F applies to from here: in the tree — inside the
    /// selected file, or recursively under the selected folder/root;
    /// in the editor — the active tab's file.
    /// </summary>
    private (SearchScope Scope, string Path)? ResolveScopedSearchTarget()
    {
        if (_focus == Focus.Tree && _tree.Selected is { } node)
            return node.IsDir
                ? (SearchScope.SelectedFolder, node.FullPath)
                : (SearchScope.CurrentFile, node.FullPath);
        return _editor.FilePath is { } file
            ? (SearchScope.CurrentFile, file)
            : null;
    }

    private void OpenHit(SearchHit hit)
    {
        _tree.SelectPath(hit.FilePath);
        // The bottom panel stays visible; the cursor lands on the match itself.
        TryOpen(hit.FilePath, hit.LineNumber - 1, column: hit.MatchStart);
        _openedHitIndex = _hits.IndexOf(hit);
    }

    /// <summary>
    /// Ctrl+G / Ctrl+Shift+G: jump to the next/previous hit right from the
    /// editor. The first press after a search opens the current hit itself.
    /// </summary>
    private void JumpHit(int delta)
    {
        if (_hits.Count == 0) { _status = "Run a search first (Ctrl+F)."; return; }
        _resultsVisible = true;
        if (_openedHitIndex == _hitIndex)
            _hitIndex = Math.Clamp(_hitIndex + delta, 0, _hits.Count - 1);
        OpenHit(_hits[_hitIndex]);
        if (_mode == UiMode.Normal)
            _status = $"Hit {_hitIndex + 1}/{_hits.Count}: {_hits[_hitIndex].RelativePath}:{_hits[_hitIndex].LineNumber} · Ctrl+G/Ctrl+Shift+G — next/prev";
    }
}
