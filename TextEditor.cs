using System.Text;

namespace ConsoleEditor;

/// <summary>
/// Read/edit panel for a single file.
/// Tabs are expanded to 4 spaces on open — for simple cursor arithmetic.
/// </summary>
public sealed class TextEditor
{
    public string? FilePath { get; private set; }
    public List<StringBuilder> Lines { get; private set; } = new() { new StringBuilder() };
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

    public bool HasFile => FilePath != null;
    public string DisplayName => FilePath == null ? "(no file)" : Path.GetFileName(FilePath);
    public string Extension => FilePath == null ? "" : Path.GetExtension(FilePath);
    public int LineCount => Lines.Count;
    public string LineText(int i) => Lines[i].ToString();

    public void Open(string path, int goToLine = 0)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains('\0'))
            throw new InvalidOperationException("The file looks binary; editing is disabled.");

        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Lines = raw.Select(s => new StringBuilder(s.Replace("\t", "    "))).ToList();
        if (Lines.Count == 0) Lines.Add(new StringBuilder());

        FilePath = Path.GetFullPath(path);
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
        Dirty = false;
        EnsureCursorVisible();
    }

    /// <summary>The tab's file was renamed/moved on disk — update the path; content and history stay intact.</summary>
    public void RenameTo(string newPath)
    {
        FilePath = Path.GetFullPath(newPath);
    }

    public void Save()
    {
        if (FilePath == null) throw new InvalidOperationException("No file is open.");
        File.WriteAllText(FilePath, string.Join(Environment.NewLine, ContentCopy()) + Environment.NewLine, Encoding.UTF8);
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

    private string[] ContentCopy() => Lines.Select(l => l.ToString()).ToArray();

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
            if (!Lines[i].ToString().Equals(_savedContent[i], StringComparison.Ordinal)) { Dirty = true; return; }
        Dirty = false;
    }

    private void Restore(Snapshot s)
    {
        Lines = s.Lines.Select(t => new StringBuilder(t)).ToList();
        if (Lines.Count == 0) Lines.Add(new StringBuilder());
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
        Lines[Cy].Insert(Cx, c);
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
        Lines[Cy].Insert(Cx, new string(' ', spaces));
        Cx += spaces;
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    public void InsertNewline()
    {
        BeginEdit("other");
        if (HasSelection) DeleteSelectionCore();
        var cur = Lines[Cy];
        string tail = cur.ToString(Cx, cur.Length - Cx);
        cur.Remove(Cx, cur.Length - Cx);
        // auto-indent: repeat the leading spaces of the current line
        string head = cur.ToString();
        string indent = new(head.TakeWhile(ch => ch == ' ').ToArray());
        Lines.Insert(Cy + 1, new StringBuilder(indent + tail));
        Cy++;
        Cx = indent.Length;
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
            Lines[Cy].Remove(Cx - 1, 1);
            Cx--;
        }
        else
        {
            Cx = Lines[Cy - 1].Length;
            Lines[Cy - 1].Append(Lines[Cy].ToString());
            Lines.RemoveAt(Cy);
            Cy--;
        }
        _desiredCx = Cx;
        EnsureCursorVisible();
        EndEdit();
    }

    public void Delete()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Cx >= Lines[Cy].Length && Cy >= Lines.Count - 1) return;
        BeginEdit("delete");
        if (Cx < Lines[Cy].Length)
            Lines[Cy].Remove(Cx, 1);
        else
        {
            Lines[Cy].Append(Lines[Cy + 1].ToString());
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
        string s = Lines[Cy].ToString();
        int firstNonSpace = s.Length - s.TrimStart(' ').Length;
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
        string s = Lines[Cy].ToString();
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
        string s = Lines[Cy].ToString();
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

    private bool IsBlank(int line) => Lines[line].ToString().Trim().Length == 0;

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
        if (sl == el) return Lines[sl].ToString(sc, ec - sc);
        var sb = new StringBuilder();
        sb.Append(Lines[sl].ToString(sc, Lines[sl].Length - sc));
        for (int i = sl + 1; i < el; i++) sb.Append('\n').Append(Lines[i]);
        sb.Append('\n').Append(Lines[el].ToString(0, ec));
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
            Lines[sl].Remove(sc, ec - sc);
        }
        else
        {
            string tail = Lines[el].ToString(ec, Lines[el].Length - ec);
            Lines[sl].Remove(sc, Lines[sl].Length - sc);
            Lines[sl].Append(tail);
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
        string s = Lines[Cy].ToString();
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
    /// Scroll the view with the mouse wheel — the cursor does not move. At the
    /// bottom it stops with the last line at the bottom edge, never an empty view.
    /// </summary>
    public void ScrollView(int deltaLines)
    {
        ScrollY = Math.Clamp(ScrollY + deltaLines, 0, MaxScrollY);
    }

    /// <summary>Place the cursor with a mouse click (coordinates are already in file characters).</summary>
    public void SetCursor(int line, int col)
    {
        Cy = Math.Clamp(line, 0, Lines.Count - 1);
        Cx = Math.Clamp(col, 0, Lines[Cy].Length);
        _desiredCx = Cx;
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
    }
}
