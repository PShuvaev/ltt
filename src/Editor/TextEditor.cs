using System.Globalization;
using System.Text;

/// <summary>
/// Read/edit panel for a single file. Lines are kept without their breaks
/// and saved back in the file's own format (see TextFile). Tabs stay tabs:
/// they are expanded only on screen. Cx is an index into the cursor line
/// that always sits on a character boundary; ScrollX and the remembered
/// column for up/down movement are display columns (screen cells, see Cells).
/// </summary>
public sealed class TextEditor
{
    public string? FilePath { get; private set; }

    /// <summary>Immutable lines: edits, the undo history and the saved state share untouched ones.</summary>
    private List<string> _lines = new() { "" };
    public IReadOnlyList<string> Lines => _lines;

    // Held while an edit, undo, redo or reload changes _lines, so a copy taken
    // from another thread (a signal handler backing the buffer up) sees whole
    // edits only. A waiting copy goes first: no new edit starts meanwhile.
    private readonly object _changeLock = new();
    private int _copiesWaiting;

    private void BeginChange()
    {
        while (Volatile.Read(ref _copiesWaiting) > 0) Thread.Yield();
        Monitor.Enter(_changeLock);
    }

    private void EndChange()
    {
        if (Monitor.IsEntered(_changeLock)) Monitor.Exit(_changeLock);
    }
    public int Cx { get; private set; }          // cursor index in the current line
    public int Cy { get; private set; }          // cursor line
    public int ScrollX { get; private set; }     // first visible display column
    public int ScrollY { get; private set; }
    public bool Dirty { get; private set; }
    public TextFormat Format { get; private set; } = TextFormat.Default;

    /// <summary>The syntax colours of this text (from vim); told of every change, in order.</summary>
    internal SyntaxColours? Colours { get; set; }

    /// <summary>Tab inserts a tab character (tab-indented files, Makefiles, Go) instead of spaces.</summary>
    public bool IndentWithTabs { get; private set; }

    /// <summary>Selection anchor; the selection spans anchor..cursor. null — no selection.</summary>
    public (int Line, int Col)? SelAnchor { get; private set; }

    /// <summary>The disk version the buffer was loaded from or last saved as (null — no file on disk).</summary>
    public (DateTime WriteUtc, long Length, long Ino, long ChangeNs)? DiskStamp { get; private set; }

    // The disk version a reload could not read (the file turned binary, say), and the one CheckDisk saw last.
    private (DateTime, long, long, long)? _unreadable, _lastSeen;

    /// <summary>The file on disk is the version a reload already failed to read: trying again is no use.</summary>
    public bool Unreadable => _unreadable != null && _unreadable == _lastSeen;

    /// <summary>The file changed on disk while this buffer had unsaved edits (they are kept).</summary>
    public bool ChangedOnDisk { get; set; }

    /// <summary>The file is gone from disk (deleted, or renamed by someone else).</summary>
    public bool MissingOnDisk { get; set; }

    // ---- Edit history ----
    // Every change to _lines is a splice: Old lines at At were replaced by New.
    // An undo step holds the splices of one edit group and the cursor around
    // them, so the history keeps only the lines that were actually touched.
    private sealed record Change(int At, string[] Old, string[] New);

    private sealed class Step
    {
        public readonly List<Change> Splices = new();
        public required string Kind;
        public (int Line, int Col) Before, After;
        public (int Line, int Col)? AnchorBefore;
    }

    private readonly List<Step> _undo = new();
    private readonly List<Step> _redo = new();
    private Step? _open; // the step that consecutive typing / Backspace / Delete keep extending
    private const int MaxUndo = 200;
    private string[] _savedContent = { "" }; // state as of open/save

    // ---- Change marks: the text against the last commit, or against the file as first opened ----
    private string[] _opened = { "" };
    private string[]? _committed;  // from git; null — compare with _opened
    private int _version;          // bumped by every change of _lines
    private byte[]? _marks;
    private int _marksVersion;

    /// <summary>The committed text to mark changed lines against (empty — a file git does not have yet); null — the file as first opened.</summary>
    public void SetBaseline(string[]? committed)
    {
        if (ReferenceEquals(committed, _committed)) return;
        _committed = committed;
        _marks = null;
    }

    /// <summary>The line's change mark (LineDiff: added, modified, lines removed next to it), computed once per edit.</summary>
    public byte ChangeMark(int line)
    {
        if (_marks == null || _marksVersion != _version)
        {
            _marks = LineDiff.Marks(_committed ?? _opened, _lines);
            _marksVersion = _version;
        }
        return line < _marks.Length ? _marks[line] : (byte)0;
    }

    /// <summary>The lines where changes start, top to bottom: a run of marked lines is one change.</summary>
    public List<int> ChangeStarts()
    {
        var starts = new List<int>();
        for (int i = 0; i < _lines.Count; i++)
            if (ChangeMark(i) != 0 && (i == 0 || ChangeMark(i - 1) == 0)) starts.Add(i);
        return starts;
    }

    /// <summary>
    /// The cursor to the start of the change at `line` (see ChangeStarts). The view stays while the
    /// whole change is in it; otherwise it shows the change from its start, a few lines below the top.
    /// </summary>
    public void GoToChange(int line)
    {
        int end = line;
        while (end + 1 < _lines.Count && ChangeMark(end + 1) != 0) end++;
        bool inView = line >= ScrollY && end < ScrollY + ViewHeight;
        GoToLine(line, 0);
        if (!inView) ScrollY = Math.Clamp(line - Math.Min(3, ViewHeight / 4), 0, MaxScrollY);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Changes with every change of the text.</summary>
    public int Version => _version;

    /// <summary>Lower scroll bound: the last line sits at the bottom edge; the view is never empty.</summary>
    public int MaxScrollY => Math.Max(0, _lines.Count - Math.Max(1, ViewHeight));

    /// <summary>Viewport size (excluding the line-number gutter), set by rendering.</summary>
    public int ViewWidth { get; set; } = 80;
    public int ViewHeight { get; set; } = 20;

    private int _desiredCol; // display column remembered while moving up/down

    private int _wheelNumerator; // accumulated fifths of a line (see WheelScroll)

    public bool HasFile => FilePath != null;
    public string DisplayName => FilePath == null ? "(no file)" : Path.GetFileName(FilePath);
    public int LineCount => _lines.Count;
    public string LineText(int i) => _lines[i];

    public void Open(string path)
    {
        string full = Path.GetFullPath(path);
        var stamp = TextFile.Stamp(full); // before reading: a write landing meanwhile shows as a change
        var (lines, format) = TextFile.Read(full);
        Load(full, lines, format, stamp);
        EnsureCursorVisible();
    }

    /// <summary>A path whose file is gone (a recovered buffer): empty, marked missing on disk.</summary>
    public void OpenMissing(string path) => Load(Path.GetFullPath(path), new List<string> { "" }, TextFormat.Default, null);

    private void Load(string full, List<string> lines, TextFormat format, (DateTime, long, long, long)? stamp)
    {
        BeginChange();
        _lines = lines;
        _version++;
        EndChange();
        _savedContent = _lines.ToArray();
        _opened = _savedContent;
        FilePath = full;
        Format = format;
        IndentWithTabs = DetectTabIndent(_lines, full);
        DiskStamp = stamp;
        ChangedOnDisk = false;
        MissingOnDisk = DiskStamp == null;
        _undo.Clear();
        _redo.Clear();
        _open = null;
        SelAnchor = null;
        Cx = Cy = 0;
        _desiredCol = 0;
        ScrollX = ScrollY = 0;
        _wheelNumerator = 0;
        Dirty = false;
        Colours?.Reset();
    }

    /// <summary>Replace the whole text as one undo step (a recovered buffer); the result is unsaved.</summary>
    public void ReplaceContent(List<string> lines)
    {
        if (lines.Count == 0) lines.Add("");
        BeginEdit("other");
        Splice(0, _lines.Count, lines.ToArray());
        Cy = Cx = 0;
        EndEdit();
    }

    /// <summary>Pull the cursor, the anchor and the view back inside the text (after an internal error).</summary>
    public void ClampState()
    {
        if (_lines.Count == 0) _lines.Add("");
        _version++;
        Cy = Math.Clamp(Cy, 0, _lines.Count - 1);
        Cx = Cells.FloorBoundary(_lines[Cy], Math.Clamp(Cx, 0, _lines[Cy].Length));
        if (SelAnchor is { } a)
        {
            int al = Math.Clamp(a.Line, 0, _lines.Count - 1);
            SelAnchor = (al, Math.Clamp(a.Col, 0, _lines[al].Length));
        }
        ScrollY = Math.Clamp(ScrollY, 0, MaxScrollY);
        _open = null;
        while (Monitor.IsEntered(_changeLock)) Monitor.Exit(_changeLock); // an edit that threw never finished
        Colours?.Reset(); // vim's copy of the text may have missed the edit that threw
    }

    /// <summary>Tabs for Makefiles and Go, and for files whose indented lines mostly start with a tab.</summary>
    private static bool DetectTabIndent(List<string> lines, string path)
    {
        string name = Path.GetFileName(path);
        if (name is "Makefile" or "makefile" or "GNUmakefile" || name.EndsWith(".mk") || name.EndsWith(".go"))
            return true;
        int tabs = 0, spaces = 0;
        for (int i = 0; i < lines.Count && i < 10_000; i++)
        {
            string l = lines[i];
            if (l.AsSpan().TrimStart(" \t").IsEmpty) continue; // a blank line says nothing about indentation
            if (l.StartsWith('\t')) tabs++;
            else if (l.StartsWith("  ")) spaces++;
        }
        return tabs > spaces;
    }

    /// <summary>
    /// The file was moved: only the path changes. The known disk version stays —
    /// a move keeps time and size, so an edit made after it still shows. A copy
    /// to another disk may round the time: a buffer that was in sync with the
    /// file before (wasInSync) takes the new stamp.
    /// </summary>
    public void RenameTo(string newPath, bool wasInSync = false)
    {
        FilePath = Path.GetFullPath(newPath);
        Colours?.Renamed(); // the language may have changed
        IndentWithTabs = DetectTabIndent(_lines, FilePath); // so may the indentation: a Makefile takes tabs
        var stamp = TextFile.Stamp(FilePath);
        MissingOnDisk = stamp == null;
        if (wasInSync && stamp != null) DiskStamp = stamp;
    }

    /// <summary>Write the buffer back in the file's own format (atomically, see TextFile.Write).</summary>
    public void Save()
    {
        if (FilePath == null) throw new InvalidOperationException("No file is open.");
        TextFile.Write(FilePath, _lines, Format);
        Format = Format with { MixedNewLines = false };
        _savedContent = _lines.ToArray();
        DiskStamp = TextFile.Stamp(FilePath);
        ChangedOnDisk = MissingOnDisk = false;
        _open = null; // the next edit starts a new undo step, so undo can stop at the saved text
        Dirty = false;
    }

    /// <summary>What the file on disk is, compared with the version this buffer knows.</summary>
    public enum DiskState { Same, Changed, Missing }

    public DiskState CheckDisk()
    {
        if (FilePath == null) return DiskState.Same;
        var stamp = TextFile.Stamp(FilePath);
        _lastSeen = stamp;
        if (stamp == null) return DiskState.Missing;
        if (stamp == DiskStamp) return DiskState.Same;
        // Only the inode or its change time moved: a rewrite that kept the time and the size, or
        // just a chmod or a new link. The text says which.
        if (DiskStamp is { } known && (stamp.Value.WriteUtc, stamp.Value.Length) == (known.WriteUtc, known.Length)
            && HoldsSavedText())
        {
            DiskStamp = stamp;
            return DiskState.Same;
        }
        return DiskState.Changed;
    }

    /// <summary>Whether the file on disk holds the text this buffer was loaded from or last saved as.</summary>
    private bool HoldsSavedText()
    {
        try
        {
            var (lines, format) = TextFile.Read(FilePath!);
            return format == Format && lines.SequenceEqual(_savedContent, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>
    /// Take the file's content from disk: the lines that differ are replaced as
    /// one undo step; the cursor and the view stay on the text they were on.
    /// </summary>
    public void Reload()
    {
        if (FilePath == null) return;
        var stamp = TextFile.Stamp(FilePath); // before reading, as in Open
        List<string> lines;
        TextFormat format;
        try { (lines, format) = TextFile.Read(FilePath); }
        catch
        {
            _unreadable = stamp;
            throw;
        }
        _unreadable = null;
        int pre = 0;
        while (pre < lines.Count && pre < _lines.Count && lines[pre] == _lines[pre]) pre++;
        int suf = 0;
        while (suf < lines.Count - pre && suf < _lines.Count - pre
               && lines[lines.Count - 1 - suf] == _lines[_lines.Count - 1 - suf]) suf++;
        int oldEnd = _lines.Count - suf, newEnd = lines.Count - suf, delta = lines.Count - _lines.Count;
        var (cy, cx, sy) = (Cy, Cx, ScrollY);
        // Lines above the changed block keep their numbers, lines below it shift; inside it (an agent's
        // edit in several places at once) the diff of the block tells where the cursor's line went.
        int[] middle = new[] { cy, sy }.Where(l => l >= pre && l < oldEnd).Select(l => l - pre).ToArray();
        int[] followed = middle.Length == 0 ? middle
            : LineDiff.Follow(_lines.GetRange(pre, oldEnd - pre).ToArray(), lines.GetRange(pre, newEnd - pre), middle);
        int m = 0;
        int Moved(int line) => line < pre ? line : line >= oldEnd ? line + delta : pre + followed[m++];
        (cy, sy) = (Moved(cy), Moved(sy));
        SelAnchor = null;
        Step? step = null;
        if (pre < oldEnd || pre < lines.Count - suf)
        {
            BeginEdit("reload");
            Splice(pre, oldEnd - pre, lines.GetRange(pre, lines.Count - suf - pre).ToArray());
            step = _open;
            EndChange();
        }
        _open = null;
        Format = format;
        _savedContent = _lines.ToArray();
        DiskStamp = stamp;
        ChangedOnDisk = MissingOnDisk = false;
        Dirty = false;
        Cy = Math.Clamp(cy, 0, _lines.Count - 1);
        Cx = Cells.FloorBoundary(_lines[Cy], Math.Min(cx, _lines[Cy].Length));
        if (step != null) step.After = (Cy, Cx); // redo puts the cursor back here
        ScrollY = Math.Clamp(sy, 0, MaxScrollY);
        CursorMoved();
    }

    /// <summary>
    /// Jump to a line; with a column (a search hit — an index into the line) the
    /// cursor goes there too, mid-view when it is past the first screen.
    /// </summary>
    public void GoToLine(int lineZeroBased, int column = -1)
    {
        SelAnchor = null;
        Cy = Math.Clamp(lineZeroBased, 0, _lines.Count - 1);
        if (column < 0) UseDesiredColumn();
        else
        {
            Cx = Cells.FloorBoundary(_lines[Cy], Math.Min(column, _lines[Cy].Length));
            _desiredCol = CursorColumn;
            ScrollX = _desiredCol <= CursorRightCol ? 0 : _desiredCol - ViewWidth / 2;
        }
        EnsureCursorVisible();
    }

    // ---------- Undo / Redo ----------

    /// <summary>Replace `count` lines at `at` with `lines`, recording it in the open undo step.</summary>
    private void Splice(int at, int count, params string[] lines)
    {
        var old = count == 0 ? Array.Empty<string>() : _lines.GetRange(at, count).ToArray();
        Apply(at, count, lines);
        var steps = _open!.Splices;
        // Typing along one line: keep only that line's text from before the step.
        if (old.Length == 1 && lines.Length == 1 && steps.Count > 0
            && steps[^1] is { Old.Length: 1, New.Length: 1 } last && last.At == at)
            steps[^1] = last with { New = lines };
        else
            steps.Add(new Change(at, old, lines));
    }

    private void SetLine(int i, string text) => Splice(i, 1, text);

    // Lines inserted and deleted, oldest first (a bounded window): FollowLine moves a line number over them.
    private readonly List<(int At, int Old, int New)> _shifts = new();
    private long _shiftsDropped;

    /// <summary>How many times lines were inserted or deleted so far: a mark for FollowLine.</summary>
    public long LineShifts => _shiftsDropped + _shifts.Count;

    /// <summary>
    /// Where a line (0-based) that was there at mark `since` is now: lines below an insertion or a
    /// deletion move with it, a deleted line becomes the one that took its place. -1 — too long ago.
    /// </summary>
    public int FollowLine(int line, long since)
    {
        if (since < _shiftsDropped) return -1;
        for (int i = (int)(since - _shiftsDropped); i < _shifts.Count; i++)
        {
            var (at, old, @new) = _shifts[i];
            if (line >= at + old) line += @new - old;
            else if (line >= at) line = at + Math.Min(line - at, Math.Max(0, @new - 1));
        }
        return Math.Clamp(line, 0, _lines.Count - 1);
    }

    private void RecordShift(int at, int count, string[] lines)
    {
        // The lines a block kept at either end keep their places: Enter at a line's start pushes
        // that line down, a deleted selection that ends at a line start takes the lines above it.
        int head = 0, tail = 0, both = Math.Min(count, lines.Length);
        while (head < both && _lines[at + head] == lines[head]) head++;
        while (tail < both - head && _lines[at + count - 1 - tail] == lines[^(tail + 1)]) tail++;
        _shifts.Add((at + head, count - head - tail, lines.Length - head - tail));
        if (_shifts.Count > 8192) { _shifts.RemoveRange(0, 4096); _shiftsDropped += 4096; }
    }

    private void Apply(int at, int count, string[] lines)
    {
        Colours?.Spliced(at, count == 0 ? Array.Empty<string>() : _lines.GetRange(at, count), lines);
        if (count != lines.Length) RecordShift(at, count, lines);
        if (count == lines.Length)
            for (int i = 0; i < count; i++) _lines[at + i] = lines[i];
        else
        {
            _lines.RemoveRange(at, count);
            _lines.InsertRange(at, lines);
        }
        _version++;
    }

    /// <summary>A copy of the lines from any thread, between two edits; null — one is stuck midway.</summary>
    public string[]? SnapshotLines()
    {
        Interlocked.Increment(ref _copiesWaiting);
        try
        {
            if (!Monitor.TryEnter(_changeLock, 500)) return null;
            try { return _lines.ToArray(); }
            finally { Monitor.Exit(_changeLock); }
        }
        finally { Interlocked.Decrement(ref _copiesWaiting); }
    }

    /// <summary>
    /// Start of an edit. Consecutive typing, Backspace and Delete with the cursor
    /// moving along make one undo step; any other edit, or one that replaces a
    /// selection, is its own step.
    /// </summary>
    private void BeginEdit(string kind)
    {
        BeginChange();
        if (!HasSelection) SelAnchor = null; // an empty selection never outlives an edit
        bool coalesce = _open != null && kind != "other" && kind == _open.Kind
                        && !HasSelection && _open.After == (Cy, Cx)
                        && _undo.Count > 0 && ReferenceEquals(_undo[^1], _open);
        if (!coalesce)
        {
            _open = new Step { Kind = kind, Before = (Cy, Cx), AnchorBefore = SelAnchor };
            _undo.Add(_open);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }
        _redo.Clear();
    }

    private void EndEdit()
    {
        EndChange();
        // A join or a letter typed before a lone mark makes one character of two: the cursor goes after it.
        Cx = Cells.CeilBoundary(_lines[Cy], Cx);
        if (_open != null)
        {
            _open.After = (Cy, Cx);
            if (_open.Splices.Count == 0 && _undo.Count > 0 && ReferenceEquals(_undo[^1], _open))
            {
                _undo.RemoveAt(_undo.Count - 1); // nothing changed — nothing to undo
                _open = null;
            }
        }
        CursorMoved();
        RefreshDirty();
    }

    /// <summary>Dirty — an honest comparison with the saved state (undoing back to it is clean again).</summary>
    private void RefreshDirty() => Dirty = !_lines.SequenceEqual(_savedContent);

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        BeginChange();
        for (int i = step.Splices.Count - 1; i >= 0; i--)
        {
            var s = step.Splices[i];
            Apply(s.At, s.New.Length, s.Old);
        }
        EndChange();
        _redo.Add(step);
        PlaceAfterHistory(step.Before, step.AnchorBefore);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        BeginChange();
        foreach (var s in step.Splices) Apply(s.At, s.Old.Length, s.New);
        EndChange();
        _undo.Add(step);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        PlaceAfterHistory(step.After, null);
    }

    private void PlaceAfterHistory((int Line, int Col) at, (int Line, int Col)? anchor)
    {
        _open = null;
        Cy = Math.Clamp(at.Line, 0, _lines.Count - 1);
        Cx = Cells.FloorBoundary(_lines[Cy], at.Col);
        SelAnchor = anchor is { } a && a.Line < _lines.Count
            ? (a.Line, Cells.FloorBoundary(_lines[a.Line], a.Col))
            : null;
        CursorMoved();
        RefreshDirty();
    }

    // ---------- Editing ----------

    public void InsertChar(char c)
    {
        BeginEdit("insert");
        DeleteSelectionCore();
        SetLine(Cy, _lines[Cy].Insert(Cx, c.ToString()));
        Cx++;
        EndEdit();
    }

    /// <summary>Tab: a tab character in tab-indented files, else spaces to the next tab stop.</summary>
    public void InsertTab()
    {
        BeginEdit("other");
        DeleteSelectionCore();
        string ins = IndentWithTabs ? "\t" : new string(' ', Cells.TabWidth - CursorColumn % Cells.TabWidth);
        SetLine(Cy, _lines[Cy].Insert(Cx, ins));
        Cx += ins.Length;
        EndEdit();
    }

    public void InsertNewline()
    {
        BeginEdit("other");
        DeleteSelectionCore();
        string cur = _lines[Cy];
        // auto-indent: repeat the leading whitespace (spaces and tabs) of the current line
        int indent = 0;
        while (indent < Cx && cur[indent] is ' ' or '\t') indent++;
        Splice(Cy, 1, cur[..Cx], string.Concat(cur.AsSpan(0, indent), cur.AsSpan(Cx)));
        Cy++;
        Cx = indent;
        EndEdit();
    }

    public void Backspace()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx == 0 && Cy == 0) { SelAnchor = null; return; } // nothing to delete — and nothing for the history
        BeginEdit("backspace");
        if (Cx > 0)
        {
            int from = Cells.PrevBoundary(_lines[Cy], Cx); // a whole character: never half a surrogate pair
            SetLine(Cy, _lines[Cy].Remove(from, Cx - from));
            Cx = from;
        }
        else
        {
            int joinAt = _lines[Cy - 1].Length;
            Splice(Cy - 1, 2, string.Concat(_lines[Cy - 1], _lines[Cy]));
            Cy--;
            Cx = joinAt;
        }
        EndEdit();
    }

    private static bool IsBlankChar(char c) => c is ' ' or '\t';

    /// <summary>Option/Ctrl+Backspace: delete spaces + the word run before the cursor (same scan as WordLeft).</summary>
    public void DeleteWordBackward()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx == 0) { Backspace(); return; } // at a line start — join lines, like a regular Backspace
        string s = _lines[Cy];
        int i = Cells.FloorBoundary(s, WordStartBefore(s, Cx));
        if (i == Cx) return;
        BeginEdit("backspace"); // a run of word-deletions/backspaces is one undo step
        SetLine(Cy, s.Remove(i, Cx - i));
        Cx = i;
        EndEdit();
    }

    /// <summary>
    /// Insert pasted text (possibly multi-line) at the cursor as one undo step,
    /// replacing any selection; no auto-indent. CRLF/CR become line breaks,
    /// tabs stay, other control characters are dropped.
    /// Returns the number of inserted (normalized) characters, as the cursor steps over them.
    /// </summary>
    public int InsertText(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                clean.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c is '\n' or '\t' || !char.IsControl(c)) clean.Append(c);
        }
        if (clean.Length == 0) return 0;

        string[] parts = clean.ToString().Split('\n');
        BeginEdit("other");
        DeleteSelectionCore();
        string cur = _lines[Cy];
        int endCx = (parts.Length == 1 ? Cx : 0) + parts[^1].Length;
        parts[0] = string.Concat(cur.AsSpan(0, Cx), parts[0]);
        parts[^1] = string.Concat(parts[^1], cur.AsSpan(Cx));
        Splice(Cy, 1, parts);
        Cy += parts.Length - 1;
        Cx = endCx;
        EndEdit();
        return (int)Math.Min(Cells.CharCount(clean.ToString()), int.MaxValue);
    }

    /// <summary>Replace one exact match if it is still at this place; false when the buffer has moved on.</summary>
    public bool ReplaceAt(int line, int column, string query, string replacement)
    {
        if (query.Length == 0 || line < 0 || line >= _lines.Count || column < 0
            || column + query.Length > _lines[line].Length
            || !string.Equals(_lines[line].Substring(column, query.Length), query, StringComparison.Ordinal)) return false;
        if (!string.Equals(query, replacement, StringComparison.Ordinal))
        {
            BeginEdit("other");
            SetLine(line, string.Concat(_lines[line].AsSpan(0, column), replacement, _lines[line].AsSpan(column + query.Length)));
            Cy = line;
            Cx = column + replacement.Length;
            SelAnchor = null;
            EndEdit();
        }
        Cy = line;
        Cx = column + replacement.Length;
        SelAnchor = null;
        CursorMoved();
        return true;
    }

    /// <summary>Replace every exact match in the buffer as one undo step; the number of changed occurrences.</summary>
    public int ReplaceAll(string query, string replacement)
    {
        if (query.Length == 0 || string.Equals(query, replacement, StringComparison.Ordinal)) return 0;
        int count = 0, cy = Cy, cx = Cx;
        BeginEdit("other");
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            string old = _lines[i];
            string now = LiteralSearch.ReplaceLine(old, query, replacement, out int n);
            if (n == 0 || now == old) continue;
            if (count == 0)
            {
                cy = i;
                int last = old.LastIndexOf(query, StringComparison.Ordinal);
                cx = now.Length - (old.Length - last - query.Length);
            }
            SetLine(i, now);
            count += n;
        }
        if (count > 0) { Cy = cy; Cx = cx; SelAnchor = null; }
        EndEdit();
        return count;
    }

    public void Delete()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx >= _lines[Cy].Length && Cy >= _lines.Count - 1) { SelAnchor = null; return; }
        BeginEdit("delete");
        if (Cx < _lines[Cy].Length)
        {
            int to = Cells.NextBoundary(_lines[Cy], Cx);
            SetLine(Cy, _lines[Cy].Remove(Cx, to - Cx));
        }
        else Splice(Cy, 2, string.Concat(_lines[Cy], _lines[Cy + 1]));
        EndEdit();
    }

    /// <summary>Ctrl+K: delete the cursor line whole — with a selection, every line it touches.</summary>
    public void DeleteLine()
    {
        if (_lines.Count == 1 && _lines[0].Length == 0) { SelAnchor = null; return; } // nothing to delete
        var (sl, _, el, ec) = SelectionRange();
        int last = el > sl && ec == 0 ? el - 1 : el; // a selection that ends at a line start leaves that line
        BeginEdit("other");
        if (sl == 0 && last == _lines.Count - 1) Splice(0, _lines.Count, "");
        else Splice(sl, last - sl + 1);
        Cy = Math.Min(sl, _lines.Count - 1);
        Cx = 0;
        SelAnchor = null;
        EndEdit();
    }

    // ---------- Cursor movement ----------

    private (string? Line, int Index, int Col) _column; // the last answer: a long line is scanned once per change

    /// <summary>Display column (screen cells from the line start) of the cursor.</summary>
    public int CursorColumn
    {
        get
        {
            string line = _lines[Cy];
            if (!ReferenceEquals(_column.Line, line) || _column.Index != Cx) _column = (line, Cx, Cells.ColumnOf(line, Cx));
            return _column.Col;
        }
    }

    public void MoveLeft()
    {
        if (Cx > 0) Cx = Cells.PrevBoundary(_lines[Cy], Cx);
        else if (Cy > 0) { Cy--; Cx = _lines[Cy].Length; }
        CursorMoved();
    }

    public void MoveRight()
    {
        if (Cx < _lines[Cy].Length) Cx = Cells.NextBoundary(_lines[Cy], Cx);
        else if (Cy < _lines.Count - 1) { Cy++; Cx = 0; }
        CursorMoved();
    }

    /// <summary>The cursor was placed anew: up and down keep its column from now on, and it comes into view.</summary>
    private void CursorMoved() { _desiredCol = CursorColumn; EnsureCursorVisible(); }

    private void UseDesiredColumn() =>
        Cx = Cells.FloorBoundary(_lines[Cy], Cells.IndexAt(_lines[Cy], _desiredCol));

    public void MoveUp() { if (Cy > 0) { Cy--; UseDesiredColumn(); } EnsureCursorVisible(); }
    public void MoveDown() { if (Cy < _lines.Count - 1) { Cy++; UseDesiredColumn(); } EnsureCursorVisible(); }
    public void PageUp() { Cy = Math.Max(0, Cy - ViewHeight); UseDesiredColumn(); EnsureCursorVisible(); }
    public void PageDown() { Cy = Math.Min(_lines.Count - 1, Cy + ViewHeight); UseDesiredColumn(); EnsureCursorVisible(); }

    public void Home()
    {
        string s = _lines[Cy];
        int firstNonBlank = 0;
        while (firstNonBlank < s.Length && IsBlankChar(s[firstNonBlank])) firstNonBlank++;
        Cx = Cx == firstNonBlank ? 0 : firstNonBlank; // smart Home, like in IDEs
        CursorMoved();
    }

    public void End() { Cx = _lines[Cy].Length; CursorMoved(); }
    public void DocHome() { Cy = 0; Cx = 0; _desiredCol = 0; EnsureCursorVisible(); }
    public void DocEnd() { Cy = _lines.Count - 1; Cx = _lines[Cy].Length; CursorMoved(); }

    /// <summary>
    /// Character class for word jumps and word selection: 1 — word, 2 — space,
    /// 3 — punctuation, 0 — a combining mark, joiner or variation selector (or
    /// the second half of a surrogate pair), which goes with the character before it.
    /// </summary>
    private static int CharClass(string s, int i)
    {
        char c = s[i];
        if (char.IsLowSurrogate(c) || c == '\u200D') return 0;
        if (CharUnicodeInfo.GetUnicodeCategory(s, i) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) return 0;
        return char.IsLetterOrDigit(s, i) || c == '_' ? 1 : IsBlankChar(c) ? 2 : 3;
    }

    /// <summary>End of the run of characters of the class of the one at i.</summary>
    private static int RunEnd(string s, int i)
    {
        int cls = CharClass(s, i);
        do i++; while (i < s.Length && CharClass(s, i) is var k && (k == cls || k == 0));
        return i;
    }

    /// <summary>Start of the run of characters of the class of the one before i.</summary>
    private static int RunStart(string s, int i)
    {
        int cls = -1;
        while (i > 0)
        {
            int j = i - 1;
            while (j > 0 && CharClass(s, j) == 0) j--; // a mark is judged by its base
            int k = CharClass(s, j);
            if (cls >= 0 && k != cls) break;
            cls = k;
            i = j;
        }
        return i;
    }

    /// <summary>Start of the blank run + word run (or punctuation run) before index i.</summary>
    private static int WordStartBefore(string s, int i)
    {
        while (i > 0 && IsBlankChar(s[i - 1])) i--;
        return RunStart(s, i);
    }

    public void WordLeft()
    {
        if (Cx == 0) { MoveLeft(); return; }
        Cx = Cells.FloorBoundary(_lines[Cy], WordStartBefore(_lines[Cy], Cx));
        CursorMoved();
    }

    public void WordRight()
    {
        string s = _lines[Cy];
        if (Cx >= s.Length) { MoveRight(); return; }
        int i = Cx;
        while (i < s.Length && IsBlankChar(s[i])) i++;
        if (i < s.Length) i = RunEnd(s, i);
        int b = Cells.FloorBoundary(s, i);
        Cx = b < i ? Cells.NextBoundary(s, b) : i; // never back into the character just passed
        CursorMoved();
    }

    private bool IsBlank(int line) => _lines[line].AsSpan().Trim().Length == 0;

    /// <summary>Option+↑: to the start of the current or previous paragraph.</summary>
    public void ParagraphUp()
    {
        int y = Cy;
        if (y > 0) y--;
        while (y > 0 && IsBlank(y)) y--;
        while (y > 0 && !IsBlank(y - 1)) y--;
        Cy = y;
        UseDesiredColumn();
        EnsureCursorVisible();
    }

    /// <summary>Option+↓: to the start of the next paragraph.</summary>
    public void ParagraphDown()
    {
        int y = Cy, last = _lines.Count - 1;
        while (y < last && !IsBlank(y)) y++;
        while (y < last && IsBlank(y)) y++;
        Cy = y;
        UseDesiredColumn();
        EnsureCursorVisible();
    }

    // ---------- Selection ----------

    public bool HasSelection => SelAnchor is { } a && (a.Line != Cy || a.Col != Cx);

    /// <summary>Normalized selection range (the start is no later than the end), clamped to the text.</summary>
    public (int SL, int SC, int EL, int EC) SelectionRange()
    {
        var a = SelAnchor ?? (Cy, Cx);
        int al = Math.Clamp(a.Line, 0, _lines.Count - 1);
        int ac = Math.Clamp(a.Col, 0, _lines[al].Length);
        return al < Cy || (al == Cy && ac <= Cx)
            ? (al, ac, Cy, Cx)
            : (Cy, Cx, al, ac);
    }

    public void SetAnchorHere() => SelAnchor = (Cy, Cx);
    public void EnsureAnchor() => SelAnchor ??= (Cy, Cx);
    public void ClearSelection() => SelAnchor = null;

    public void SelectAll()
    {
        SelAnchor = (0, 0);
        Cy = _lines.Count - 1;
        Cx = _lines[Cy].Length;
        CursorMoved();
    }

    /// <summary>Collapse the selection to its start or end (arrows without Shift).</summary>
    public void CollapseSelection(bool toStart)
    {
        var (sl, sc, el, ec) = SelectionRange();
        (Cy, Cx) = toStart ? (sl, sc) : (el, ec);
        SelAnchor = null;
        CursorMoved();
    }

    private ((int, int, int, int) Range, int Version, int Count) _charCount; // drawn every frame: counted once

    /// <summary>Characters selected, as the cursor steps over them; a line break counts one.</summary>
    public int SelectionCharCount()
    {
        if (!HasSelection) return 0;
        var range = SelectionRange();
        if (_charCount.Range == range && _charCount.Version == _version) return _charCount.Count;
        var (sl, sc, el, ec) = range;
        long n = sl == el
            ? Cells.CharCount(_lines[sl].AsSpan(sc, ec - sc))
            : Cells.CharCount(_lines[sl].AsSpan(sc)) + 1 + Cells.CharCount(_lines[el].AsSpan(0, ec));
        for (int i = sl + 1; i < el; i++) n += Cells.CharCount(_lines[i]) + 1;
        _charCount = (range, _version, (int)Math.Min(n, int.MaxValue));
        return _charCount.Count;
    }

    public string SelectedText()
    {
        if (!HasSelection) return "";
        var (sl, sc, el, ec) = SelectionRange();
        if (sl == el) return _lines[sl][sc..ec];
        var sb = new StringBuilder();
        sb.Append(_lines[sl].AsSpan(sc));
        for (int i = sl + 1; i < el; i++) sb.Append('\n').Append(_lines[i]);
        sb.Append('\n').Append(_lines[el].AsSpan(0, ec));
        return sb.ToString();
    }

    public void DeleteSelection()
    {
        if (!HasSelection) { SelAnchor = null; return; }
        BeginEdit("other");
        DeleteSelectionCore();
        EndEdit();
    }

    /// <summary>Deletion without its own history step — for compound edits (replace-by-typing).</summary>
    private void DeleteSelectionCore()
    {
        if (!HasSelection) { SelAnchor = null; return; }
        var (sl, sc, el, ec) = SelectionRange();
        Splice(sl, el - sl + 1, string.Concat(_lines[sl].AsSpan(0, sc), _lines[el].AsSpan(ec)));
        Cy = sl;
        Cx = sc;
        SelAnchor = null;
    }

    /// <summary>Select the word (or run of spaces/punctuation) under the cursor — double-click.</summary>
    public void SelectWord()
    {
        string s = _lines[Cy];
        if (s.Length == 0) return;
        int i = Cells.FloorBoundary(s, Math.Min(Cx, s.Length - 1));
        int b = RunEnd(s, i);
        SelAnchor = (Cy, Cells.FloorBoundary(s, RunStart(s, Cells.NextBoundary(s, i))));
        Cx = Cells.FloorBoundary(s, b) is var e && e < b ? Cells.NextBoundary(s, e) : b;
        CursorMoved();
    }

    /// <summary>Scroll the view; the cursor does not move.</summary>
    public void ScrollView(int deltaLines)
    {
        ScrollY = Math.Clamp(ScrollY + deltaLines, 0, MaxScrollY);
        _wheelNumerator = 0;
    }

    /// <summary>One wheel notch: 6/5 of a line at once, the remainder accumulating (5 notches = 6 lines).</summary>
    public void WheelScroll(bool down)
    {
        _wheelNumerator += down ? 6 : -6;
        int steps = _wheelNumerator / 5; // toward zero — the remainder keeps its sign
        _wheelNumerator -= steps * 5;
        if (steps != 0) ScrollY = Math.Clamp(ScrollY + steps, 0, MaxScrollY);
        // At either end, drop the leftover so reversing responds at once.
        if ((ScrollY == 0 && _wheelNumerator < 0) || (ScrollY == MaxScrollY && _wheelNumerator > 0))
            _wheelNumerator = 0;
    }

    /// <summary>
    /// One sideways notch (a swipe, a tilt wheel, Shift+wheel): 3 columns, the
    /// cursor stays. Right stops once the longest line in view ends at the edge.
    /// </summary>
    public void WheelSideways(bool right)
    {
        const int step = 3;
        int longest = 0;
        int end = Math.Min(_lines.Count, ScrollY + Math.Max(1, ViewHeight));
        for (int i = ScrollY; i < end; i++) longest = Math.Max(longest, Cells.WidthOf(_lines[i]));
        int max = Math.Max(0, longest - ViewWidth);
        ScrollX = right
            ? Math.Min(ScrollX + step, Math.Max(ScrollX, max))
            : Math.Max(0, Math.Min(ScrollX, max) - step);
    }

    /// <summary>Place the cursor at a line and an index into it (clamped to a character boundary).</summary>
    public void SetCursor(int line, int col)
    {
        Cy = Math.Clamp(line, 0, _lines.Count - 1);
        Cx = Cells.FloorBoundary(_lines[Cy], Math.Clamp(col, 0, _lines[Cy].Length));
        _desiredCol = CursorColumn;
        _wheelNumerator = 0;
    }

    /// <summary>Place the cursor with a mouse click: the character drawn at a display column.</summary>
    public void SetCursorAtCell(int line, int cellCol)
    {
        int y = Math.Clamp(line, 0, _lines.Count - 1);
        string s = _lines[y];
        // A scrolled view starts with the ‹ marker: the text begins after it,
        // and a character cut by that edge is not on screen. The last column is
        // the › marker's: a click there lands on the character before it.
        int first = ScrollX > 0 ? ScrollX + 1 : 0;
        if (ViewWidth > 0) cellCol = Math.Min(cellCol, ScrollX + CursorRightCol);
        int i = Cells.FloorBoundary(s, Cells.IndexAt(s, Math.Max(cellCol, first)));
        if (i < s.Length && Cells.ColumnOf(s, i) < first) i = Cells.NextBoundary(s, i);
        SetCursor(y, i);
    }

    public void EnsureCursorVisible()
    {
        if (ViewHeight > 0)
        {
            if (Cy < ScrollY) ScrollY = Cy;
            if (Cy >= ScrollY + ViewHeight) ScrollY = Cy - ViewHeight + 1;
        }
        if (ViewWidth > 0)
        {
            // The edge cells hold the ‹ › cut markers, so the cursor keeps off them.
            // Once it is off screen, the view returns to column 0 if the cursor fits
            // on the first screen, else puts it at the right edge with the text before it in view.
            int col = CursorColumn;
            bool onScreen = col >= (ScrollX > 0 ? ScrollX + 1 : 0) && col <= ScrollX + CursorRightCol;
            if (!onScreen) ScrollX = Math.Max(0, col - CursorRightCol);
            // A wide character at the cursor shows whole: its right half never goes under the ›.
            if (col + CursorCells - 1 > ScrollX + CursorRightCol) ScrollX = col + CursorCells - 1 - CursorRightCol;
        }
        ScrollY = Math.Clamp(ScrollY, 0, MaxScrollY);
        ScrollX = Math.Max(0, ScrollX);
        _wheelNumerator = 0; // cursor-driven scrolling drops any leftover wheel fraction
    }

    /// <summary>The rightmost view column the cursor takes: the last one is for the › marker.</summary>
    private int CursorRightCol => Math.Max(0, ViewWidth - 2);

    /// <summary>Cells of the character at the cursor: 2 for a wide one, else 1 (a tab, the line end).</summary>
    public int CursorCells =>
        Cx < _lines[Cy].Length && _lines[Cy][Cx] != '\t' && Cells.WidthAt(_lines[Cy], Cx, 0, out _) == 2 ? 2 : 1;
}
