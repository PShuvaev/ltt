using System.Text;

/// <summary>
/// Read/edit panel for a single file.
/// Tabs are expanded to 4 spaces on open — for simple cursor arithmetic.
/// </summary>
public sealed class TextEditor
{
    public string? FilePath { get; private set; }

    /// <summary>
    /// Lines as immutable strings: an edit allocates only the touched line,
    /// and undo snapshots / saved-state comparisons share unchanged lines
    /// by reference instead of copying the whole text.
    /// </summary>
    public List<string> Lines { get; private set; } = new() { "" };
    public int Cx { get; private set; }          // cursor column in the current line
    public int Cy { get; private set; }          // cursor line
    public int ScrollX { get; private set; }
    public int ScrollY { get; private set; }
    public bool Dirty { get; private set; }

    /// <summary>Selection anchor; the selection spans anchor..cursor. null — no selection.</summary>
    public (int Line, int Col)? SelAnchor { get; private set; }

    // Edit history: snapshots of the state before a group of changes.
    private sealed record Snapshot(string[] Lines, int Cx, int Cy, (int Line, int Col)? Anchor);
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private const int MaxUndo = 200;
    private string _lastEditKind = "";
    private int _lastEditCx = -1, _lastEditCy = -1;
    private string[] _savedContent = { "" }; // state as of open/save

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Lower scroll bound: the last line sits at the bottom edge; the view is never empty.</summary>
    public int MaxScrollY => Math.Max(0, Lines.Count - Math.Max(1, ViewHeight));

    /// <summary>Viewport size (excluding the line-number gutter), set by rendering.</summary>
    public int ViewWidth { get; set; } = 80;
    public int ViewHeight { get; set; } = 20;

    private int _desiredCx; // column remembered while moving up/down

    // Wheel scrolling, deliberately plain: each notch is 6/5 of a line,
    // applied instantly (the remainder accumulates in _wheelNumerator, the
    // same scheme as the tree wheel). No animation, no acceleration, no
    // dependence on timing or file size — the view moves exactly as far as
    // the wheel was turned, every time, in every terminal.
    private int _wheelNumerator; // accumulated fifths of a line

    // Block-comment state checkpoints: _commentCheckpoints[k] is the
    // in-block-comment state before line (k+1)*CheckpointInterval, so rendering
    // a deep window never re-tokenizes the whole file from the top.
    private const int CheckpointInterval = 256;
    private readonly List<bool> _commentCheckpoints = new();
    private readonly List<Token> _tokenScratch = new();

    private void InvalidateCommentCheckpointsFrom(int line)
    {
        int kept = Math.Max(0, line) / CheckpointInterval;
        if (_commentCheckpoints.Count > kept)
            _commentCheckpoints.RemoveRange(kept, _commentCheckpoints.Count - kept);
    }

    /// <summary>Whether `line` starts inside a /* */ block comment (checkpoint-accelerated).</summary>
    public bool BlockCommentStateBefore(int line)
    {
        if (line <= 0) return false;
        string ext = Extension;
        int covered = line / CheckpointInterval * CheckpointInterval;
        bool state = false;
        int from = 0;
        int have = Math.Min(_commentCheckpoints.Count, covered / CheckpointInterval);
        if (have > 0) { state = _commentCheckpoints[have - 1]; from = have * CheckpointInterval; }

        while (from < covered && from < Lines.Count)
        {
            int next = Math.Min(from + CheckpointInterval, Lines.Count);
            for (int i = from; i < next; i++)
                Highlighter.TokenizeInto(_tokenScratch, Lines[i], ext, ref state);
            from = next;
            if (from % CheckpointInterval == 0 && _commentCheckpoints.Count == from / CheckpointInterval - 1)
                _commentCheckpoints.Add(state);
        }
        for (int i = from; i < line && i < Lines.Count; i++)
            Highlighter.TokenizeInto(_tokenScratch, Lines[i], ext, ref state);
        return state;
    }

    public bool HasFile => FilePath != null;
    public string DisplayName => FilePath == null ? "(no file)" : Path.GetFileName(FilePath);

    private string _extension = "";
    /// <summary>File extension, cached (read on every rendered frame).</summary>
    public string Extension => _extension;

    private bool _hasBom; // the file started with a UTF-8 BOM — preserve it, never invent one
    public int LineCount => Lines.Count;
    public string LineText(int i) => Lines[i];

    public void Open(string path, int goToLine = 0)
    {
        // Streamed line-by-line read: no single whole-file string in memory.
        var lines = new List<string>();
        bool endsWithBreak = false;
        using (var fs = File.OpenRead(path))
        {
            _hasBom = false;
            if (fs.Length >= 3)
            {
                Span<byte> head = stackalloc byte[3];
                fs.ReadExactly(head);
                _hasBom = head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                fs.Position = 0;
            }
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                endsWithBreak = fs.ReadByte() is '\n' or '\r';
                fs.Position = 0;
            }
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains('\0'))
                    throw new InvalidOperationException("The file looks binary; editing is disabled.");
                lines.Add(line.Contains('\t') ? line.Replace("\t", "    ") : line);
            }
        }
        // A trailing line break ends the last line; keep the empty line after it.
        if (lines.Count == 0 || endsWithBreak) lines.Add("");
        Lines = lines;
        _commentCheckpoints.Clear();

        FilePath = Path.GetFullPath(path);
        _extension = Path.GetExtension(FilePath);
        SelAnchor = null;
        _savedContent = ContentCopy();
        _undo.Clear();
        _redo.Clear();
        _lastEditKind = "";
        Cx = 0;
        Cy = Math.Clamp(goToLine, 0, Lines.Count - 1);
        _desiredCx = 0;
        ScrollX = 0;
        ScrollY = 0;
        _wheelNumerator = 0;
        Dirty = false;
        EnsureCursorVisible();
    }

    /// <summary>The tab's file was renamed/moved on disk — update the path; content and history stay intact.</summary>
    public void RenameTo(string newPath)
    {
        FilePath = Path.GetFullPath(newPath);
        _extension = Path.GetExtension(FilePath);
        _commentCheckpoints.Clear(); // the extension (language) may have changed
    }

    public void Save()
    {
        if (FilePath == null) throw new InvalidOperationException("No file is open.");
        // Encoding.UTF8 would always emit a BOM; write exactly the BOM-ness the file had.
        using (var writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(_hasBom), bufferSize: 65536) { NewLine = Environment.NewLine })
            foreach (var line in Lines) writer.WriteLine(line);
        _savedContent = ContentCopy();
        Dirty = false;
    }

    public void GoToLine(int lineZeroBased)
    {
        SelAnchor = null;
        Cy = Math.Clamp(lineZeroBased, 0, Lines.Count - 1);
        UseDesiredColumn();
        EnsureCursorVisible();
    }

    // ---------- Undo / Redo ----------

    /// <summary>Reference copy only: lines are immutable, unchanged ones are shared.</summary>
    private string[] ContentCopy() => Lines.ToArray();

    private Snapshot Snap() => new(ContentCopy(), Cx, Cy, SelAnchor);

    private void PushUndo()
    {
        _undo.Add(Snap());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
    }

    /// <summary>
    /// Start of an edit. Consecutive typing, Backspace and Delete are grouped
    /// into one undo step while the cursor moves continuously;
    /// every other edit is its own step.
    /// </summary>
    private void BeginEdit(string kind)
    {
        int affected = HasSelection ? SelectionRange().SL : Cy;
        InvalidateCommentCheckpointsFrom(Math.Max(0, affected - 1));
        bool coalesce = kind == _lastEditKind && kind != "other"
                        && Cx == _lastEditCx && Cy == _lastEditCy;
        if (!coalesce) PushUndo();
        _redo.Clear();
        _lastEditKind = kind;
    }

    private void EndEdit()
    {
        _lastEditCx = Cx;
        _lastEditCy = Cy;
        RefreshDirty();
    }

    /// <summary>Dirty — an honest comparison with the saved state (undoing back to it is clean again).</summary>
    private void RefreshDirty()
    {
        if (Lines.Count != _savedContent.Length) { Dirty = true; return; }
        for (int i = 0; i < Lines.Count; i++)
            if (!string.Equals(Lines[i], _savedContent[i], StringComparison.Ordinal)) { Dirty = true; return; }
        Dirty = false;
    }

    private void Restore(Snapshot s)
    {
        Lines = new List<string>(s.Lines);
        if (Lines.Count == 0) Lines.Add("");
        _commentCheckpoints.Clear(); // content was replaced wholesale
        Cy = Math.Clamp(s.Cy, 0, Lines.Count - 1);
        Cx = Math.Clamp(s.Cx, 0, Lines[Cy].Length);
        _desiredCx = Cx;
        SelAnchor = s.Anchor is { } a && a.Line < Lines.Count
            ? (a.Line, Math.Clamp(a.Col, 0, Lines[a.Line].Length))
            : null;
        _lastEditKind = "";
        EnsureCursorVisible();
        RefreshDirty();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Snap());
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        PushUndo();
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(s);
    }

    // ---------- Editing ----------

    public void InsertChar(char c)
    {
        BeginEdit("insert");
        if (HasSelection) DeleteSelectionCore();
        string line = Lines[Cy];
        Lines[Cy] = string.Create(line.Length + 1, (line, Cx, c), static (dst, s) =>
        {
            s.line.AsSpan(0, s.Cx).CopyTo(dst);
            dst[s.Cx] = s.c;
            s.line.AsSpan(s.Cx).CopyTo(dst[(s.Cx + 1)..]);
        });
        Cx++;
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    public void InsertTab()
    {
        BeginEdit("other");
        if (HasSelection) DeleteSelectionCore();
        int spaces = 4 - (Cx % 4);
        string line = Lines[Cy];
        Lines[Cy] = string.Create(line.Length + spaces, (line, Cx, spaces), static (dst, s) =>
        {
            s.line.AsSpan(0, s.Cx).CopyTo(dst);
            dst.Slice(s.Cx, s.spaces).Fill(' ');
            s.line.AsSpan(s.Cx).CopyTo(dst[(s.Cx + s.spaces)..]);
        });
        Cx += spaces;
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    public void InsertNewline()
    {
        BeginEdit("other");
        if (HasSelection) DeleteSelectionCore();
        string cur = Lines[Cy];
        string head = cur[..Cx];
        // auto-indent: repeat the leading spaces of the current line
        int indentLen = 0;
        while (indentLen < head.Length && head[indentLen] == ' ') indentLen++;
        Lines[Cy] = head;
        Lines.Insert(Cy + 1, string.Concat(head.AsSpan(0, indentLen), cur.AsSpan(Cx)));
        Cy++;
        Cx = indentLen;
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    public void Backspace()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx == 0 && Cy == 0) return; // nothing to delete — and nothing for the history
        BeginEdit("backspace");
        if (Cx > 0)
        {
            Lines[Cy] = Lines[Cy].Remove(Cx - 1, 1);
            Cx--;
        }
        else
        {
            Cx = Lines[Cy - 1].Length;
            Lines[Cy - 1] = string.Concat(Lines[Cy - 1], Lines[Cy]);
            Lines.RemoveAt(Cy);
            Cy--;
        }
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    /// <summary>Option/Ctrl+Backspace: delete spaces + the word run before the cursor (same scan as WordLeft).</summary>
    public void DeleteWordBackward()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx == 0) { Backspace(); return; } // at a line start — join lines, like a regular Backspace
        string s = Lines[Cy];
        int i = Cx;
        while (i > 0 && s[i - 1] == ' ') i--;
        if (i > 0)
        {
            int cls = CharClass(s[i - 1]);
            while (i > 0 && CharClass(s[i - 1]) == cls) i--;
        }
        if (i == Cx) return;
        BeginEdit("backspace"); // a run of word-deletions/backspaces is one undo step
        Lines[Cy] = s.Remove(i, Cx - i);
        Cx = i;
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    /// <summary>
    /// Insert pasted text (possibly multi-line) at the cursor as one undo step,
    /// replacing any selection. Normalized like file opening: CRLF/CR become LF,
    /// tabs become 4 spaces, other control characters are dropped.
    /// Returns the number of inserted (normalized) characters.
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
            else if (c == '\t') clean.Append("    ");
            else if (c == '\n' || !char.IsControl(c)) clean.Append(c);
        }
        if (clean.Length == 0) return 0;

        BeginEdit("other");
        if (HasSelection) DeleteSelectionCore();
        string cur = Lines[Cy];
        string head = cur[..Cx], tail = cur[Cx..];
        string[] parts = clean.ToString().Split('\n');
        if (parts.Length == 1)
        {
            Lines[Cy] = string.Concat(head, parts[0], tail);
            Cx += parts[0].Length;
        }
        else
        {
            Lines[Cy] = string.Concat(head, parts[0]);
            var extra = new string[parts.Length - 1];
            for (int i = 1; i < parts.Length - 1; i++) extra[i - 1] = parts[i];
            extra[^1] = string.Concat(parts[^1], tail);
            Lines.InsertRange(Cy + 1, extra);
            Cy += parts.Length - 1;
            Cx = parts[^1].Length;
        }
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
        return clean.Length;
    }

    public void Delete()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx >= Lines[Cy].Length && Cy >= Lines.Count - 1) return;
        BeginEdit("delete");
        if (Cx < Lines[Cy].Length)
            Lines[Cy] = Lines[Cy].Remove(Cx, 1);
        else
        {
            Lines[Cy] = string.Concat(Lines[Cy], Lines[Cy + 1]);
            Lines.RemoveAt(Cy + 1);
        }
        EnsureCursorVisible();
        EndEdit();
    }

    // ---------- Cursor movement ----------

    public void MoveLeft()
    {
        if (Cx > 0) Cx--;
        else if (Cy > 0) { Cy--; Cx = Lines[Cy].Length; }
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    public void MoveRight()
    {
        if (Cx < Lines[Cy].Length) Cx++;
        else if (Cy < Lines.Count - 1) { Cy++; Cx = 0; }
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    private void UseDesiredColumn() => Cx = Math.Min(_desiredCx, Lines[Cy].Length);

    public void MoveUp() { if (Cy > 0) { Cy--; UseDesiredColumn(); } EnsureCursorVisible(); }
    public void MoveDown() { if (Cy < Lines.Count - 1) { Cy++; UseDesiredColumn(); } EnsureCursorVisible(); }
    public void PageUp() { Cy = Math.Max(0, Cy - ViewHeight); UseDesiredColumn(); EnsureCursorVisible(); }
    public void PageDown() { Cy = Math.Min(Lines.Count - 1, Cy + ViewHeight); UseDesiredColumn(); EnsureCursorVisible(); }

    public void Home()
    {
        string s = Lines[Cy];
        int firstNonSpace = 0;
        while (firstNonSpace < s.Length && s[firstNonSpace] == ' ') firstNonSpace++;
        Cx = Cx == firstNonSpace ? 0 : firstNonSpace; // smart Home, like in IDEs
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    public void End() { Cx = Lines[Cy].Length; _desiredCx = Cx; EnsureCursorVisible(); }
    public void DocHome() { Cy = 0; Cx = 0; _desiredCx = 0; EnsureCursorVisible(); }
    public void DocEnd() { Cy = Lines.Count - 1; Cx = Lines[Cy].Length; _desiredCx = Cx; EnsureCursorVisible(); }

    // Character class for word jumps and word selection: 1 — word, 2 — space, 3 — punctuation.
    private static int CharClass(char c) => char.IsLetterOrDigit(c) || c == '_' ? 1 : c == ' ' ? 2 : 3;

    public void WordLeft()
    {
        if (Cx == 0) { MoveLeft(); return; }
        string s = Lines[Cy];
        int i = Cx;
        while (i > 0 && s[i - 1] == ' ') i--;
        if (i > 0)
        {
            int cls = CharClass(s[i - 1]);
            while (i > 0 && CharClass(s[i - 1]) == cls) i--;
        }
        Cx = i;
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    public void WordRight()
    {
        string s = Lines[Cy];
        if (Cx >= s.Length) { MoveRight(); return; }
        int i = Cx;
        while (i < s.Length && s[i] == ' ') i++;
        if (i < s.Length)
        {
            int cls = CharClass(s[i]);
            while (i < s.Length && CharClass(s[i]) == cls) i++;
        }
        Cx = i;
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    private bool IsBlank(int line) => Lines[line].AsSpan().Trim().Length == 0;

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
        int y = Cy, last = Lines.Count - 1;
        while (y < last && !IsBlank(y)) y++;
        while (y < last && IsBlank(y)) y++;
        Cy = y;
        UseDesiredColumn();
        EnsureCursorVisible();
    }

    // ---------- Selection ----------

    public bool HasSelection => SelAnchor is { } a && (a.Line != Cy || a.Col != Cx);

    /// <summary>Normalized selection range: the start is no later than the end.</summary>
    public (int SL, int SC, int EL, int EC) SelectionRange()
    {
        var a = SelAnchor ?? (Cy, Cx);
        return a.Line < Cy || (a.Line == Cy && a.Col <= Cx)
            ? (a.Line, a.Col, Cy, Cx)
            : (Cy, Cx, a.Line, a.Col);
    }

    public void SetAnchorHere() => SelAnchor = (Cy, Cx);
    public void EnsureAnchor() => SelAnchor ??= (Cy, Cx);
    public void ClearSelection() => SelAnchor = null;

    /// <summary>Collapse the selection to its start or end (arrows without Shift).</summary>
    public void CollapseSelection(bool toStart)
    {
        var (sl, sc, el, ec) = SelectionRange();
        (Cy, Cx) = toStart ? (sl, sc) : (el, ec);
        _desiredCx = Cx;
        SelAnchor = null;
        EnsureCursorVisible();
    }

    public int SelectionCharCount()
    {
        if (!HasSelection) return 0;
        var (sl, sc, el, ec) = SelectionRange();
        if (sl == el) return ec - sc;
        int n = Lines[sl].Length - sc + 1 + ec; // +1 for the line break
        for (int i = sl + 1; i < el; i++) n += Lines[i].Length + 1;
        return n;
    }

    public string SelectedText()
    {
        if (!HasSelection) return "";
        var (sl, sc, el, ec) = SelectionRange();
        if (sl == el) return Lines[sl][sc..ec];
        var sb = new StringBuilder();
        sb.Append(Lines[sl].AsSpan(sc));
        for (int i = sl + 1; i < el; i++) sb.Append('\n').Append(Lines[i]);
        sb.Append('\n').Append(Lines[el].AsSpan(0, ec));
        return sb.ToString();
    }

    public void DeleteSelection()
    {
        if (!HasSelection) return;
        BeginEdit("other");
        DeleteSelectionCore();
        EndEdit();
    }

    /// <summary>Deletion without its own history step — for compound edits (replace-by-typing).</summary>
    private void DeleteSelectionCore()
    {
        if (!HasSelection) return;
        var (sl, sc, el, ec) = SelectionRange();
        if (sl == el)
        {
            Lines[sl] = Lines[sl].Remove(sc, ec - sc);
        }
        else
        {
            Lines[sl] = string.Concat(Lines[sl].AsSpan(0, sc), Lines[el].AsSpan(ec));
            Lines.RemoveRange(sl + 1, el - sl);
        }
        Cy = sl;
        Cx = sc;
        _desiredCx = sc;
        SelAnchor = null;
        EnsureCursorVisible();
    }

    /// <summary>Select the word (or run of spaces/punctuation) under the cursor — double-click.</summary>
    public void SelectWord()
    {
        string s = Lines[Cy];
        if (s.Length == 0) return;
        int i = Math.Min(Cx, s.Length - 1);
        int cls = CharClass(s[i]);
        int a = i, b = i + 1;
        while (a > 0 && CharClass(s[a - 1]) == cls) a--;
        while (b < s.Length && CharClass(s[b]) == cls) b++;
        SelAnchor = (Cy, a);
        Cx = b;
        _desiredCx = Cx;
        EnsureCursorVisible();
    }

    /// <summary>
    /// Scroll the view instantly — the cursor does not move. At the bottom it
    /// stops with the last line at the bottom edge, never an empty view.
    /// </summary>
    public void ScrollView(int deltaLines)
    {
        ScrollY = Math.Clamp(ScrollY + deltaLines, 0, MaxScrollY);
        _wheelNumerator = 0;
    }

    /// <summary>
    /// One mouse-wheel notch: 6/5 of a line, applied instantly; the remainder
    /// accumulates (5 notches = exactly 6 lines). No animation, no acceleration.
    /// </summary>
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

    /// <summary>Place the cursor with a mouse click (coordinates are already in file characters).</summary>
    public void SetCursor(int line, int col)
    {
        Cy = Math.Clamp(line, 0, Lines.Count - 1);
        Cx = Math.Clamp(col, 0, Lines[Cy].Length);
        _desiredCx = Cx;
        _wheelNumerator = 0;
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
            if (Cx < ScrollX) ScrollX = Cx;
            if (Cx >= ScrollX + ViewWidth) ScrollX = Cx - ViewWidth + 1;
        }
        ScrollY = Math.Clamp(ScrollY, 0, MaxScrollY);
        ScrollX = Math.Max(0, ScrollX);
        _wheelNumerator = 0; // cursor-driven scrolling drops any leftover wheel fraction
    }
}
