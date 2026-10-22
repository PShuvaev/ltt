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

        // Arguments: any number of folders (left-panel roots) and files (opened as
        // tabs; `file:line` jumps to the line). A file inside a folder that is
        // already a root does not add its own folder as another root.
        var dirRoots = new List<string>();
        var files = new List<(string Path, int Line)>();
        foreach (var arg in args)
        {
            if (arg.StartsWith('-')) return Fail($"Unknown option: {arg}. Usage: ltt [folder…] [file[:line]…]");
            if (Directory.Exists(arg)) { dirRoots.Add(Paths.Normalize(arg)); continue; }
            if (App.ParseLocation(arg) is { } loc && File.Exists(loc.Path))
            {
                files.Add((Path.GetFullPath(loc.Path), loc.Line));
                continue;
            }
            return Fail($"Path not found: {arg}");
        }
        var roots = new List<string>();
        foreach (var r in dirRoots)
            if (!roots.Any(x => Paths.SameReal(x, r))) roots.Add(r);
        for (int i = 0; i < files.Count; i++)
        {
            // A file through a link to a root's folder is that root's file.
            if (Paths.Within(files[i].Path, roots) is { } inRoot) files[i] = (inRoot, files[i].Line);
            else roots.Add(Path.GetDirectoryName(files[i].Path)!);
        }
        if (roots.Count == 0) roots.Add(Paths.Normalize(Directory.GetCurrentDirectory()));

        if (Console.IsInputRedirected || Console.IsOutputRedirected) return Fail("The editor needs an interactive terminal.");

        // Raw input with the mouse on Unix; on Windows or on failure — plain ReadKey without the mouse.
        ITerminalInput input;
        bool raw = !OperatingSystem.IsWindows();
        try { input = raw ? new RawTerminalInput() : new ConsoleInput(); }
        catch (InvalidOperationException) { input = new ConsoleInput(); raw = false; }
        _input = input;
        if (OperatingSystem.IsWindows()) Console.TreatControlCAsInput = true; // Ctrl+C copies, never kills

        // A kill signal or a closed terminal must not take unsaved edits with it.
        var signals = new List<PosixSignalRegistration>();
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            if (OperatingSystem.IsWindows() && sig == PosixSignal.SIGINT) continue;
            try { signals.Add(PosixSignalRegistration.Create(sig, OnSignal)); }
            catch (PlatformNotSupportedException) { }
        }
        // A resize that ends at the old size still needs a full repaint: the terminal may have dropped cells.
        if (!OperatingSystem.IsWindows())
        {
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => App.Resized = true));
            // Back from a stop the runtime would put back the terminal settings it took at its start —
            // raw already — and do it again at exit, after ltt's own restore. ltt sets its own instead.
            signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGCONT, ctx =>
            {
                ctx.Cancel = true;
                App.Continued = true;
            }));
        }

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

    /// <summary>An exit before the session starts: the reason for the shell, exit code 2.</summary>
    private static int Fail(string reason)
    {
        TerminalModes.ResetKeys();
        Console.Error.WriteLine(reason);
        return 2;
    }

    private static void OnSignal(PosixSignalContext ctx)
    {
        var app = _app;
        if (app != null)
        {
            ctx.Cancel = true;
            int code = 128 + ctx.Signal switch
            {
                PosixSignal.SIGHUP => 1, PosixSignal.SIGINT => 2, PosixSignal.SIGQUIT => 3, _ => 15,
            };
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
        Write(Modes(raw));
        Interlocked.Exchange(ref _entered, 1);
    }

    // Kitty keyboard protocol, flags 1|4|8|16: disambiguate, report alternate
    // (shifted/base-layout) keys, report all keys as CSI u, report associated
    // text. Terminals without the protocol simply ignore this.
    private static string Modes(bool raw) =>
        "\x1b[?1049h\x1b[?25l" + (raw ? "\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[>29u" : "");

    /// <summary>The modes once more, back from a stop: whatever ran in the meantime may have reset them.</summary>
    public static void Reenter()
    {
        // Pop first: kitty keeps a stack of flags, and Leave takes off one entry only.
        if (Volatile.Read(ref _entered) == 1) Write((_raw ? "\x1b[<u" : "") + Modes(_raw));
    }

    public static void Leave()
    {
        if (Interlocked.Exchange(ref _entered, 0) == 0) return;
        // Also back to normal cursor keys and keypad: the runtime switches both to application mode.
        Write((_raw ? "\x1b[<u\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l" : "")
              + "\x1b[0m\x1b[?25h\x1b[?1049l\x1b[?1l\x1b>");
    }

    /// <summary>
    /// Normal cursor keys and keypad after an early exit: the runtime switches the
    /// terminal to application mode as soon as the console is set up.
    /// </summary>
    public static void ResetKeys()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!Console.IsOutputRedirected) Write("\x1b[?1l\x1b>");
        else if (!Console.IsErrorRedirected) Console.Error.Write("\x1b[?1l\x1b>");
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
    private readonly VimSyntax _vim = new(); // syntax colours
    private readonly TextEditor _emptyEditor = new();
    private TextEditor _editor => _tabs.Active ?? _emptyEditor;

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

    /// <summary>Set by SIGCONT: the terminal settings and modes again, and everything repainted.</summary>
    internal static volatile bool Continued;

    private readonly StringBuilder _searchQuery = new();
    private readonly StringBuilder _pathInput = new();
    private List<string> _pathCandidates = new();      // path completion candidates (full input lines)
    private string _pathCommon = "";                   // what every match shares, the first 50 or not
    private int _pathCandidateIndex = -1;              // candidate selected while cycling with Tab/↑↓
    private string _pathGhost = "";                    // grey ghost-text continuation after the typed text
    private int _promptCursorX;                        // where the prompt's cursor was drawn
    private List<SearchHit> _hits = new();
    private bool _hitsTruncated;                       // more lines matched than the panel holds: refining could miss some
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

    // Live search: a query that only grew refines the hits of the one before.
    private string _hitsQuery = "";  // the query the hits answer
    private bool _hitsStale;         // files changed since the hits were found
    // Full folder scans run off the UI thread; only the newest one is kept pending,
    // so only its results are ever applied.
    private (Task<SearchResult> Task, CancellationTokenSource Cts, string Query, Dictionary<TextEditor, long> Tabs)? _pendingSearch;
    // The open tabs as each scan found them (their TextEditor.LineShifts): hits in a tab edited since
    // then are where those edits moved them (HitLine).
    private Dictionary<TextEditor, long> _hitTabs = new();

    // Background folder-size counting (see FileTree.TakeUnfinishedSizing).
    private (Task Task, TreeNode Parent, FolderSizer Sizer)? _pendingSizing;
    private CancellationTokenSource? _sizingCts;

    // Open tabs are compared with the disk this often.
    private long _diskCheckAt;
    private Dictionary<string, DateTime> _linkedFolders = new(Paths.Comparer); // as CheckLinkedFolders last saw them
    private const int DiskCheckIntervalMs = 1000;

    // Tab-bar cells of the last frame — for mouse clicks.
    private readonly List<(TextEditor Tab, int X0, int X1, int CloseX)> _tabCells = new();

    private int _lastW, _lastH;
    // Geometry of the last frame — needed for the cursor and mouse hit-testing.
    private int _editorTextX, _contentTop, _statusY;
    private int _leftW, _editorX, _mainContentH = 1;
    private int _treeTop = 1, _treeContentH = 1; // the tree starts right below the title
    private int _resultsTop, _resultsRowsTop, _resultsRowCount;
    private DateTime _lastClickAt = DateTime.MinValue;
    private int _lastClickX = -1, _lastClickY = -1;
    private TreeNode? _lastClickedNode; // the tree entry under the last click (double-click check)
    private TextEditor? _dragging; // the editor a mouse drag selection is in progress in
    private int _treeWheelNumerator; // see TreeWheelSteps
    private int _resultsWheelNumerator;
    private string? _titlePath; // the path shown in the title — a click copies it

    // Watching the left panel's folders for external changes.
    private const int WatchTickMs = 200; // how often the main loop wakes up without input
    private const int SearchTickMs = 15; // … while a search runs: its results show as soon as it ends
    private FolderWatcher? _watcher;

    // What changed (git status, or since start): the tree's colours and the editor's line marks.
    private readonly Changes _changes = new();
    private int _repoScan = -1; // the tree generation last searched for repositories
    private readonly HashSet<string> _warnedUnwatchable = new(Paths.Comparer);

    // A kill signal or a hangup asks the UI thread to secure unsaved edits and stop.
    private volatile string? _emergency;
    private readonly ManualResetEventSlim _emergencyDone = new(false);
    private int _drawFailures;

    public App(IReadOnlyList<string> roots, IReadOnlyList<(string Path, int Line)> initialFiles)
    {
        _tree = new FileTree(roots);
        string? error = null; // a file that does not open says so, whichever one it is
        foreach (var (f, line) in initialFiles)
        {
            _tree.SelectPath(f);
            if (!TryOpen(f, line)) error ??= _status;
        }
        if (initialFiles.Count > 0) // keep the first of them active
        {
            _tree.SelectPath(initialFiles[0].Path);
            TryOpen(initialFiles[0].Path, 0, activateOnly: true);
        }
        if (error != null) _status = error;
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
                if (input.TryRead(_pendingSearch != null || _vim.Busy ? SearchTickMs : WatchTickMs, out var ev))
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
                if (Continued)
                {
                    Continued = false;
                    input.Resume();
                    TerminalModes.Reenter();
                    _screen.Invalidate();
                    dirty = true;
                }
                _watcher?.PollIfDue(); // roots without a live FileSystemWatcher are polled by signature
                if (ApplyExternalChanges()) dirty = true;
                if (PollSearch()) dirty = true;
                if (PollSizing()) dirty = true;
                if (PollPaste()) dirty = true;
                if (PollChanges()) dirty = true;
                if (_vim.Poll()) dirty = true;
                StartSizingIfIdle();
                if (Environment.TickCount64 - _diskCheckAt >= DiskCheckIntervalMs)
                {
                    _diskCheckAt = Environment.TickCount64;
                    if (CheckOpenTabsOnDisk()) dirty = true;
                    if (CheckLinkedFolders()) dirty = true;
                    _changes.CheckGitDirs(_tree.RootPaths);
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
            _vim.Dispose();
            _watcher.Dispose();
            _watcher = null;
            _emergencyDone.Set();
        }
    }

    /// <summary>
    /// Expanded folders reached through a symlinked folder get no watcher events: they are looked at
    /// with the disk checks, and re-read when their entries changed (the folder's time moved) — and
    /// once when first seen, for what changed between their listing and that first look.
    /// </summary>
    private bool CheckLinkedFolders()
    {
        var seen = new Dictionary<string, DateTime>(Paths.Comparer);
        var changed = new List<string>();
        foreach (var node in _tree.ExpandedThroughLinks())
        {
            DateTime t;
            try { t = Directory.GetLastWriteTimeUtc(node.IsLink ? Paths.Real(node.FullPath) : node.FullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            seen[node.FullPath] = t;
            if (!_linkedFolders.TryGetValue(node.FullPath, out var before) || before != t) changed.Add(node.FullPath);
        }
        _linkedFolders = seen;
        return changed.Count > 0 && _tree.RefreshDirs(changed);
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
            foreach (var t in _tabs.Tabs) t.ClampState();
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
            foreach (var t in _tabs.Tabs) t.ClampState();
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
        TextEditor[] editors;
        try { editors = CollectionsMarshal.AsSpan(_tabs.Tabs).ToArray(); } // the list as it is at one moment
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
                if (existing >= 0) ed = _tabs.Tabs[existing];
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
        _hitsStale = true; // results can no longer be refined: lines may have changed
        bool redraw = FollowRenames(batch.Renames);
        if (!batch.Deep && FollowByIdentity(batch.Dirs)) redraw = true;
        _changes.Changed(batch.Dirs, _tree.RootPaths);
        if (batch.Overflow) _changes.RefreshAll();
        if (batch.Dirs.Count == 0 && !batch.Overflow) return redraw;

        FuzzySearch.InvalidateFileLists();
        var before = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        string? beforeSel = _tree.Selected?.FullPath;
        if (batch.Overflow) { _tree.Refresh(); SyncWatcher(forceRecreate: batch.Restart); }
        else _tree.RefreshDirs(batch.Dirs, batch.Deep);
        var after = _tree.Visible.Select(v => v.FullPath + (v.IsDir ? "/" : "")).ToList();
        bool changed = !before.SequenceEqual(after) || beforeSel != _tree.Selected?.FullPath;
        if (changed && batch.Summary.Length > 0)
            _status = $"External change: {batch.Summary} — left panel refreshed.";
        return true; // even with the same rows: a size or a change colour may be new
    }

    /// <summary>
    /// Tabs of a file (or of files in a folder) renamed outside follow it to the new path — through
    /// every rename of the batch in turn (docs → docs2 → docs3).
    /// </summary>
    private bool FollowRenames(IReadOnlyList<(string Old, string New)> renames)
    {
        // Only real moves: "rename to a backup, write a new file" leaves the old path in place.
        var moves = renames.Where(r => !File.Exists(r.Old) && !Directory.Exists(r.Old)).ToList();
        if (moves.Count == 0) return false;
        bool any = false;
        foreach (var tab in _tabs.Tabs)
        {
            string? fp = tab.FilePath;
            if (fp == null) continue;
            string moved = fp;
            foreach (var (oldPath, newPath) in moves)
                moved = Paths.Same(moved, oldPath) ? newPath
                    : Paths.IsInside(moved, oldPath) ? newPath + moved[oldPath.Length..]
                    : moved;
            if (ReferenceEquals(moved, fp) || !File.Exists(moved)) continue;
            // Moved over a file open in another tab: that tab sees the change, this one its file gone.
            if (_tabs.IndexOfPath(moved) >= 0) continue;
            tab.RenameTo(moved);
            tab.SetBaseline(Baseline(moved));
            _status = $"{Path.GetFileName(fp)} was moved to {moved} — its tab follows.";
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A rename the watcher saw as one entry gone and another one new — FSEvents reports a rename
    /// with a write right after it so, and a move between two roots as a deletion in one and a
    /// creation in the other: a tab whose file went follows it to a changed folder where the same
    /// file is — moved itself, or inside the folder that moved (docs/x.txt under another root).
    /// macOS only, where a file's number is never handed to another file (Linux reuses inode
    /// numbers at once, and inotify reports every rename anyway).
    /// </summary>
    private bool FollowByIdentity(IReadOnlyCollection<string> dirs)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        bool any = false;
        foreach (var ed in _tabs.Tabs)
        {
            if (ed.FilePath is not { } fp || ed.MissingOnDisk || ed.DiskStamp is not { Ino: not 0 } known
                || File.Exists(fp)) continue;
            // What went: the file, or the topmost of its folders that is gone, with the file below it.
            string gone = fp;
            while (Path.GetDirectoryName(gone) is { } up && !Directory.Exists(up)) gone = up;
            string below = fp[gone.Length..];
            var disk = TextFile.Stat(Path.GetDirectoryName(gone)!)?.Dev; // numbers are per disk
            string? found = dirs.SelectMany(d => EntriesIn(d, folders: below.Length > 0)).Select(e => e + below)
                .FirstOrDefault(f => TextFile.Stat(f) is { } st && st.Ino == known.Ino && (disk == null || st.Dev == disk)
                                     && _tabs.IndexOfPath(f) < 0);
            if (found == null) continue;
            ed.RenameTo(found);
            ed.SetBaseline(Baseline(found));
            _status = $"{Path.GetFileName(fp)} was moved to {found} — its tab follows.";
            any = true;
        }
        return any;

        // No links: a link would lead to the same file under another name.
        static IEnumerable<string> EntriesIn(string dir, bool folders)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                return (folders ? info.GetDirectories().Cast<FileSystemInfo>() : info.GetFiles())
                    .Where(e => e.LinkTarget == null).Select(e => e.FullName).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// Compare each tab with its file: a clean tab takes the new text (one undo
    /// step), a dirty one keeps its edits and is marked, so saving asks first.
    /// Returns whether anything changed.
    /// </summary>
    private bool CheckOpenTabsOnDisk()
    {
        bool changed = false;
        foreach (var ed in _tabs.Tabs)
        {
            switch (ed.CheckDisk())
            {
                case TextEditor.DiskState.Missing when !ed.MissingOnDisk:
                    ed.MissingOnDisk = true;
                    _status = $"{ed.DisplayName} was deleted on disk — the tab keeps its text; saving would recreate the file.";
                    break;
                case TextEditor.DiskState.Same when ed.MissingOnDisk || ed.ChangedOnDisk:
                    // As the tab knows it again (moved away and back; readable again after a chmod): no mark.
                    ed.MissingOnDisk = ed.ChangedOnDisk = false;
                    break;
                case TextEditor.DiskState.Changed when !ed.Dirty && !ed.Unreadable: // said once, not every second
                    try
                    {
                        ed.Reload();
                        _hitsStale = true;
                        _status = $"Reloaded {ed.DisplayName}: it changed on disk (Ctrl+Z brings back the previous text).";
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                        ed.ChangedOnDisk = true;
                        _status = $"{ed.DisplayName} changed on disk but cannot be read: {e.Message}";
                    }
                    break;
                case TextEditor.DiskState.Changed when !ed.ChangedOnDisk:
                    ed.ChangedOnDisk = true;
                    ed.MissingOnDisk = false;
                    _status = $"{ed.DisplayName} changed on disk — your unsaved edits are kept; saving will ask before overwriting.";
                    break;
                default: continue; // nothing new about this tab
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Look for repositories in newly read folders and apply what git answered:
    /// each tab's lines are then marked against its file's committed text.
    /// True when the screen needs a redraw.
    /// </summary>
    private bool PollChanges()
    {
        if (_tree.Generation != _repoScan)
        {
            _repoScan = _tree.Generation;
            _changes.Discover(_tree.RootPaths.Concat(_tree.RepoFolders()));
        }
        if (!_changes.Poll()) return false;
        foreach (var t in _tabs.Tabs)
            if (t.FilePath is { } fp) t.SetBaseline(Baseline(fp));
        return true;
    }

    /// <summary>What a file's lines are marked against: its committed text, or nothing for a new file.</summary>
    private string[]? Baseline(string path) => _changes.Committed(path, isNew: _tree.Find(path)?.IsNew == true);

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
    /// false — the file cannot be opened; the status says why.
    /// </summary>
    private bool TryOpen(string path, int lineZeroBased, bool activateOnly = false, int column = -1)
    {
        _mode = UiMode.Normal;
        string full = _tree.InPanel(Path.GetFullPath(path));
        int existing = _tabs.IndexOfPath(full);
        if (existing >= 0)
        {
            _tabs.Activate(existing);
            if (!activateOnly) _editor.GoToLine(lineZeroBased, column);
            _focus = Focus.Editor;
            _status = activateOnly
                ? $"Tab: {_editor.DisplayName}"
                : $"Line {_editor.Cy + 1}."; // the one reached: a short file ends before the line asked for
            return true;
        }

        var ed = new TextEditor();
        if (_editor.ViewWidth > 0) { ed.ViewWidth = _editor.ViewWidth; ed.ViewHeight = _editor.ViewHeight; }
        try
        {
            ed.Open(full);
            ed.GoToLine(lineZeroBased, column);
            ed.SetBaseline(Baseline(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or NotSupportedException)
        {
            _status = "Error: " + ex.Message;
            return false;
        }
        _tabs.Add(ed);
        _focus = Focus.Editor;
        string format = ed.Format.Describe();
        _status = $"Opened {ed.DisplayName} ({ed.LineCount} lines{(format.Length > 0 ? ", " + format : "")}), " +
                  $"tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}. Ctrl+W closes.";
        return true;
    }

    /// <summary>Activate a tab, with the focus in the editor; the status names the tab.</summary>
    private void ActivateTab(int index)
    {
        _tabs.Activate(index);
        _focus = Focus.Editor;
        if (_tabs.Active is { } t)
            _status = $"Tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}: {t.DisplayName}{(t.Dirty ? " ●" : "")}";
    }

    private void SwitchTab(int delta)
    {
        if (_tabs.Tabs.Count == 0) { _status = "No open tabs."; return; }
        _tabs.ActivateRelative(delta);
        ActivateTab(_tabs.ActiveIndex);
    }

    private void CloseTab(TextEditor tab)
    {
        if (!tab.Dirty) { CloseNow(tab); return; }
        Ask($"Tab \"{tab.DisplayName}\" has changes!  S saves · D closes without saving · Esc cancels",
            ('s', () => SaveTab(tab, then: () => CloseNow(tab))),
            ('d', () => CloseNow(tab)));
    }

    private void CloseNow(TextEditor tab)
    {
        _tabs.Close(tab);
        if (_tabs.Tabs.Count == 0 && _focus == Focus.Editor) _focus = Focus.Tree;
        _status = $"Closed tab \"{tab.DisplayName}\"." + (_tabs.Tabs.Count > 0 ? $" Tabs left: {_tabs.Tabs.Count}." : "");
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
    private void SaveTab(TextEditor tab, Action? then)
    {
        var state = tab.CheckDisk();
        if (state == TextEditor.DiskState.Changed)
            Ask($"{tab.DisplayName} changed on disk since it was opened!  S overwrites it · R takes the disk version (Ctrl+Z brings yours back) · Esc cancels",
                ('s', () => WriteTab(tab, then)),
                ('r', () => ReloadTab(tab)));
        else if (state == TextEditor.DiskState.Missing && tab.DiskStamp != null)
            Ask($"{tab.DisplayName} was deleted on disk!  S saves it again · Esc cancels",
                ('s', () => WriteTab(tab, then)));
        else WriteTab(tab, then);
    }

    private void WriteTab(TextEditor tab, Action? then)
    {
        try
        {
            tab.Save();
            _status = $"Saved: {tab.DisplayName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status = "Save error: " + ex.Message;
            return;
        }
        RefreshRecovery();
        then?.Invoke();
    }

    private void ReloadTab(TextEditor tab)
    {
        try
        {
            tab.Reload();
            _status = $"Took the disk version of {tab.DisplayName}; Ctrl+Z brings back your edits.";
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
        var next = _tabs.Tabs.FirstOrDefault(t => t.Dirty);
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

    /// <summary>No hits, nothing to refine, no scan running.</summary>
    private void ClearSearch()
    {
        CancelSearch();
        ShowHits(SearchResult.Empty, "", new());
    }

    /// <summary>Drop any in-flight folder scan; its results will never be applied.</summary>
    private void CancelSearch()
    {
        if (_pendingSearch is { } p) { p.Cts.Cancel(); p.Cts.Dispose(); }
        _pendingSearch = null;
    }

    /// <summary>Apply a finished background scan (the pending one is always the newest).</summary>
    private bool PollSearch()
    {
        if (_pendingSearch is not { } p || !p.Task.IsCompleted) return false;
        _pendingSearch = null;
        p.Cts.Dispose();
        if (p.Task.Status != TaskStatus.RanToCompletion) return false;
        ShowHits(p.Task.Result, p.Query, p.Tabs);
        return true;
    }

    private Dictionary<TextEditor, long> TabMarks() => _tabs.Tabs.ToDictionary(t => t, t => t.LineShifts);

    /// <summary>
    /// Where a hit's line is now (0-based): edits in its tab since the scan read the file move it.
    /// A tab opened after the scan counts from its own start.
    /// </summary>
    private int HitLine(SearchHit hit)
    {
        int line = hit.LineNumber - 1;
        if (_tabs.Tabs.Find(t => Paths.Same(t.FilePath, hit.FilePath)) is not { } ed) return line;
        int now = ed.FollowLine(line, _hitTabs.GetValueOrDefault(ed));
        return now < 0 ? line : now;
    }

    /// <summary>Show a search's results from the first hit; a longer query that starts with `query` refines them.</summary>
    private void ShowHits(SearchResult result, string query, Dictionary<TextEditor, long> tabs)
    {
        _hits = result.Hits;
        _hitsTruncated = result.Truncated;
        _hitsSkipped = result.SkippedLarge;
        _hitsQuery = query;
        _hitTabs = tabs;
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
            if (t.Dirty && t.FilePath is { } path) buffers.TryAdd(path, t.Lines.ToArray());
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
        if (!Searchable(query)) { ClearSearch(); return; }

        bool canRefine = !_hitsTruncated && !_hitsStale && _hitsQuery.Length >= 2
            && query.StartsWith(_hitsQuery, StringComparison.OrdinalIgnoreCase);
        var scope = _searchScope;
        string? target = _searchTargetPath == null ? null : Path.GetFullPath(_searchTargetPath);
        var buffer = scope == SearchScope.CurrentFile && target != null && _tabs.IndexOfPath(target) is >= 0 and var t
            ? _tabs.Tabs[t].Lines : null;
        CancelSearch();
        if (canRefine || buffer != null)
        {
            // Refining ≤500 known hits and scanning one open file are instant: stay synchronous.
            ShowHits(scope != SearchScope.CurrentFile ? new SearchResult(FuzzySearch.Refine(_hits, query), false, _hitsSkipped)
                : canRefine ? new SearchResult(FuzzySearch.RefineInOrder(_hits, query), false)
                : FuzzySearch.SearchLines(target!, Path.GetFileName(target!), buffer!, query), query,
                canRefine ? _hitTabs : TabMarks());
            if (!canRefine) _hitsStale = false;
            return;
        }

        // A folder scan (or a file on disk, which may be huge) can take seconds,
        // so it runs in the background and is cancelled by the next keystroke.
        _hitsStale = false;
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var roots = _tree.RootPaths;
        var buffers = DirtyBuffers();
        var task = Task.Run(() => scope switch
        {
            SearchScope.CurrentFile => FuzzySearch.SearchFile(target!, Path.GetFileName(target!), query, ct: ct),
            SearchScope.SelectedFolder => FuzzySearch.Search(target!, query, ct: ct, buffers: buffers),
            _ => FuzzySearch.SearchMany(roots, query, ct: ct, buffers: buffers),
        });
        _pendingSearch = (task, cts, query, TabMarks());
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

    /// <summary>Open a hit's file at the match; false — it could not be opened (the status says why).</summary>
    private bool OpenHit(SearchHit hit)
    {
        _tree.SelectPath(hit.FilePath);
        // The bottom panel stays visible; the cursor lands on the match itself.
        bool opened = TryOpen(hit.FilePath, HitLine(hit), column: hit.MatchStart);
        _openedHitIndex = _hits.IndexOf(hit);
        return opened;
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
        var hit = _hits[_hitIndex];
        if (OpenHit(hit) && _mode == UiMode.Normal)
            _status = $"Hit {_hitIndex + 1}/{_hits.Count}: {hit.RelativePath}:{HitLine(hit) + 1} · Ctrl+G/Ctrl+Shift+G — next/prev";
    }
}
