/// <summary>Keyboard handling: search entry points and per-mode key handlers.</summary>
internal sealed partial class App
{
    private static bool Ctrl(ConsoleKeyInfo key) =>
        key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt);

    internal static bool IsCtrlKey(ConsoleKeyInfo key, ConsoleKey expected) => key.Key == expected && Ctrl(key);

    internal static bool IsCtrlF(ConsoleKeyInfo key) => IsCtrlKey(key, ConsoleKey.F);

    /// <summary>Ctrl+G jumps to the next hit; Ctrl+Shift+G to the previous one.</summary>
    internal static int SearchJumpDelta(ConsoleKeyInfo key) =>
        IsCtrlKey(key, ConsoleKey.G)
            ? key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : +1
            : 0;

    /// <summary>A plain typed character (no Ctrl/Alt, no control character), or '\0'.</summary>
    internal static char Typed(ConsoleKeyInfo key) =>
        !char.IsControl(key.KeyChar)
        && !key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)
            ? key.KeyChar
            : '\0';

    /// <summary>Turn selected text into a search query: spaces instead of line breaks, trimmed, at most 120 characters.</summary>
    internal static string NormalizeSearchSeed(string text)
    {
        string cleaned = string.Join(" ",
            text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        int end = 0;
        for (int n = 0; n < 120 && end < cleaned.Length; n++) end = Cells.NextBoundary(cleaned, end); // whole characters
        return cleaned[..end];
    }

    /// <summary>A search starts at two characters (an emoji is one), spaces around the query aside.</summary>
    private static bool Searchable(string query) => Cells.CharCount(query.AsSpan().Trim()) >= 2;

    private string SelectionSearchSeed() =>
        _focus == Focus.Editor && _editor.HasSelection
            ? NormalizeSearchSeed(_editor.SelectedText())
            : "";

    private void ApplySearchScope(SearchScope scope, string? targetPath)
    {
        ResetLiveSearch();
        _searchScope = scope;
        _searchTargetPath = targetPath;
        _hits = new List<SearchHit>();
        _hitsTruncated = false;
        _hitIndex = 0;
        _hitScroll = 0;
        _resultsVisible = false;
    }

    private void OpenSearch(string initial, SearchScope scope, string? targetPath = null)
    {
        if (scope != SearchScope.AllFolders && targetPath == null)
        {
            _status = "Open or select a file/folder first. Ctrl+F+F searches all folders.";
            return;
        }

        ApplySearchScope(scope, targetPath);
        _searchQuery.Clear();
        if (initial.Length > 0) _searchQuery.Append(initial);
        _mode = UiMode.SearchInput;
        if (_searchQuery.Length > 0) RunLiveSearch();
    }

    /// <summary>Ctrl+F: search from the focused target (selected file/folder or open file).</summary>
    private void OpenScopedSearch(string initial)
    {
        if (ResolveScopedSearchTarget() is { } target)
            OpenSearch(initial, target.Scope, target.Path);
        else
            _status = "Open or select a file/folder first. Ctrl+F+F searches all folders.";
    }

    /// <summary>Second Ctrl+F: widen to all folders (or narrow back to the focused target).</summary>
    private void ToggleSearchScope()
    {
        if (_searchScope != SearchScope.AllFolders)
            ApplySearchScope(SearchScope.AllFolders, null);
        else if (ResolveScopedSearchTarget() is { } target)
            ApplySearchScope(target.Scope, target.Path);
        else
            return; // stay on all folders; the prompt shows the active scope

        RunLiveSearch();
    }

    private void StartRenameForCurrentFocus()
    {
        if (_focus == Focus.Editor && _editor.HasFile)
            StartRenamePath(_editor.FilePath!, isDir: false);
        else if (_tree.Selected is { } node)
            StartRenamePath(node.FullPath, node.IsDir);
    }

    /// <summary>Copy the editor selection to the system clipboard.</summary>
    private void CopySelection()
    {
        string text = _editor.SelectedText();
        if (text.Length == 0) return;
        Clipboard.Copy(text);
        _status = $"Copied to the clipboard: {Cells.CharCount(text)} chars";
    }

    /// <summary>Ctrl+V: paste the system clipboard into the editor at the cursor.</summary>
    private void PasteFromClipboard()
    {
        string? text = Clipboard.TryPaste();
        if (string.IsNullOrEmpty(text))
        {
            _status = "Clipboard is empty or cannot be read here — the terminal's own paste still works.";
            return;
        }
        int inserted = _editor.InsertText(text);
        _status = inserted > 0 ? $"Pasted: {inserted} chars" : "Nothing pasteable on the clipboard.";
    }

    /// <summary>
    /// A paste through the terminal (bracketed paste): one edit, no auto-indent; a prompt takes one
    /// line (a path prompt without NULs, which no path can hold).
    /// </summary>
    private void HandlePaste(string text)
    {
        string oneLine = string.Join(" ", text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)).Trim();
        switch (_mode)
        {
            case UiMode.SearchInput:
                _searchQuery.Append(oneLine.Replace('\t', ' '));
                RunLiveSearch();
                return;
            case UiMode.PathInput:
                _pathInput.Append(oneLine.Replace("\0", ""));
                UpdatePathCompletions();
                return;
            case UiMode.Normal when _focus == Focus.Editor && _editor.HasFile:
                int n = _editor.InsertText(text);
                _status = n > 0 ? $"Pasted: {n} chars" : "Nothing pasteable.";
                return;
            default:
                _status = "Pasting goes into the editor: open a file and press Tab to get there.";
                return;
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        // In raw mode Ctrl+C arrives as a byte, not a signal — handle it ourselves.
        // It copies an editor selection or a tree entry, or cancels a prompt; it never
        // quits the app (quitting is Ctrl+Q, so a stray Ctrl+C can't throw work away).
        if (IsCtrlKey(key, ConsoleKey.C))
        {
            if (_mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasSelection) CopySelection();
            else if (_mode != UiMode.Normal)
            {
                if (_mode is UiMode.SearchInput or UiMode.SearchResults)
                {
                    CancelSearch(); // a scan still running must not pop the panel up later
                    _resultsVisible = false;
                }
                _mode = UiMode.Normal;
                _confirm = null;
                _status = "Cancelled (Ctrl+C).";
            }
            else if (_focus == Focus.Tree) PickUp(cut: false);
            else _status = "Ctrl+C copies a selection. Ctrl+Q quits.";
            return;
        }

        switch (_mode)
        {
            case UiMode.SearchInput: HandleSearchInput(key); return;
            case UiMode.PathInput: HandlePathInput(key); return;
            case UiMode.Confirm: HandleConfirm(key); return;
            case UiMode.SearchResults:
                if (HandleSearchResults(key)) return;
                break; // the shortcuts of the normal mode work over the results too
        }

        if (HandleGlobalKey(key)) return;
        if (_mode != UiMode.Normal) return;
        if (_focus == Focus.Tree) HandleTreeKey(key);
        else HandleEditorKey(key);
    }

    /// <summary>The shortcuts that work everywhere outside prompts. True — the key was one of them.</summary>
    private bool HandleGlobalKey(ConsoleKeyInfo key)
    {
        // The key, not the character: Alt+1 on AZERTY types '&' (and kitty reports the key).
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) && !key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && key.Key is >= ConsoleKey.D1 and <= ConsoleKey.D9)
        {
            int i = key.Key - ConsoleKey.D1;
            if (i < _tabs.Tabs.Count) { ActivateTab(i, announce: true); _focus = Focus.Editor; _mode = UiMode.Normal; }
            return true;
        }
        if (!Ctrl(key)) return false;
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        switch (key.Key)
        {
            case ConsoleKey.Q: TryQuit(); return true;
            case ConsoleKey.S: Save(); return true;
            case ConsoleKey.R: StartRenameForCurrentFocus(); return true;
            // Ctrl+F — inside the selected file/folder (tree) or the open file (editor).
            // A second Ctrl+F in the input widens the same query to all folders.
            case ConsoleKey.F: OpenScopedSearch(SelectionSearchSeed()); return true;
            case ConsoleKey.O: OpenPathInput(PathPurpose.AddRoot); return true;
            // Ctrl+N — new file; Ctrl+Shift+N — new folder (or a trailing / in the file prompt).
            case ConsoleKey.N: OpenPathInput(shift ? PathPurpose.CreateFolder : PathPurpose.CreateFile); return true;
            case ConsoleKey.L: OpenPathInput(PathPurpose.GoTo); return true;
            case ConsoleKey.G: JumpHit(shift ? -1 : +1); return true;
            case ConsoleKey.Z when !shift:
                if (_editor.CanUndo) { _editor.Undo(); _focus = Focus.Editor; _status = "Undone. Ctrl+Y redoes it."; }
                else _status = "Nothing to undo.";
                return true;
            case ConsoleKey.Y:
            case ConsoleKey.Z:
                if (_editor.CanRedo) { _editor.Redo(); _focus = Focus.Editor; _status = "Redone. Ctrl+Z undoes it."; }
                else _status = "Nothing to redo.";
                return true;
            case ConsoleKey.W:
                if (_tabs.Active is { } at) CloseTab(at);
                else _status = "No open tabs.";
                return true;
            case ConsoleKey.PageDown: SwitchTab(+1); return true;
            case ConsoleKey.PageUp: SwitchTab(-1); return true;
        }
        return false;
    }

    private void HandlePathInput(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = UiMode.Normal;
                _status = "Cancelled.";
                return;
            case ConsoleKey.Enter:
                switch (_pathPurpose)
                {
                    case PathPurpose.AddRoot: AddRootFromInput(); break;
                    case PathPurpose.CreateFile: CreateFromInput(makeFolderRequested: false); break;
                    case PathPurpose.CreateFolder: CreateFromInput(makeFolderRequested: true); break;
                    case PathPurpose.Rename: RenameFromInput(); break;
                    case PathPurpose.GoTo: GoToFromInput(); break;
                }
                return;
            case ConsoleKey.Backspace:
                if (_pathInput.Length > 0)
                {
                    string s = _pathInput.ToString();
                    _pathInput.Remove(Cells.PrevBoundary(s, s.Length), s.Length - Cells.PrevBoundary(s, s.Length));
                }
                UpdatePathCompletions();
                return;
            case ConsoleKey.Tab:
                CompletePath();
                return;
            case ConsoleKey.DownArrow:
                CycleCandidate(+1);
                return;
            case ConsoleKey.UpArrow:
                CycleCandidate(-1);
                return;
            case ConsoleKey.RightArrow: // accept the suggestion, spelled as on disk
                if (_pathGhost.Length > 0 && _pathCandidates.Count > 0) SetPathInput(_pathCandidates[0]);
                return;
            case ConsoleKey.U when Ctrl(key):
                _pathInput.Clear();
                UpdatePathCompletions();
                return;
        }
        if (Typed(key) is var c and not '\0')
        {
            _pathInput.Append(c);
            UpdatePathCompletions();
        }
    }

    private void HandleTreeKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape: // stops a paste, forgets what was picked up, hides the results — in that order
                if (_paste != null) { _paste.Cts.Cancel(); _status = "Stopping the copy…"; }
                else if (_picked != null) { _status = $"{_picked.Label} is no longer picked up."; _picked = null; }
                else if (_resultsVisible) { _resultsVisible = false; _status = "Search panel hidden (Ctrl+G brings it back)."; }
                break;
            case ConsoleKey.X when Ctrl(key): PickUp(cut: true); break;
            case ConsoleKey.V when Ctrl(key): Paste(); break;
            case ConsoleKey.UpArrow: _tree.MoveUp(); break;
            case ConsoleKey.DownArrow: _tree.MoveDown(); break;
            case ConsoleKey.RightArrow:
                // Right on a file opens it (like Enter); on a folder it expands/descends.
                if (_tree.Selected is { IsDir: false } fileToOpen)
                    TryOpen(fileToOpen.FullPath, 0, activateOnly: true);
                else
                    _tree.ExpandOrDescend();
                break;
            case ConsoleKey.LeftArrow: _tree.CollapseOrAscend(); break;
            case ConsoleKey.PageUp: _tree.PageUp(); break;
            case ConsoleKey.PageDown: _tree.PageDown(); break;
            case ConsoleKey.Home: _tree.Home(); break;
            case ConsoleKey.End: _tree.End(); break;
            case ConsoleKey.Enter:
                if (!_tree.ToggleSelected() && _tree.Selected is { IsDir: false } file)
                    TryOpen(file.FullPath, 0, activateOnly: true);
                break;
            case ConsoleKey.Tab:
                if (_editor.HasFile) { _focus = Focus.Editor; _status = "Editing. Esc returns to the tree."; }
                else _status = "Open a file with Enter first.";
                break;
            case ConsoleKey.Delete:
                if (Ctrl(key)) RequestDeleteSelected(); // Ctrl+Delete — delete from disk (with confirmation)
                else if (key.Modifiers == 0 && _tree.Selected is { IsRoot: true } rootNode) RemoveRootUi(rootNode);
                else _status = "Delete only removes a root folder from the panel; Ctrl+Delete deletes from disk.";
                break;
        }
    }

    private void HandleEditorKey(ConsoleKeyInfo key)
    {
        if (!_editor.HasFile) { _focus = Focus.Tree; return; }
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

        // Ctrl+Shift+arrows is not a combination in this editor — ignore it.
        if (ctrl && shift && key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow
                or ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            return;

        // Shift extends the selection; plain movement without Shift clears it.
        void Prep()
        {
            if (shift) _editor.EnsureAnchor();
            else _editor.ClearSelection();
        }

        // Option+B/F: how meta-mode terminals send Option+←/→ (Esc+b / Esc+f).
        if (alt && !ctrl && key.Key == ConsoleKey.B) { Prep(); _editor.WordLeft(); return; }
        if (alt && !ctrl && key.Key == ConsoleKey.F) { Prep(); _editor.WordRight(); return; }
        if (ctrl && !alt)
        {
            switch (key.Key)
            {
                case ConsoleKey.A: _editor.SelectAll(); _status = $"Selected all: {_editor.SelectionCharCount()} chars."; return;
                case ConsoleKey.X when _editor.HasSelection: // cut the selection to the clipboard
                    CopySelection();
                    _editor.DeleteSelection();
                    _status = "Cut to the clipboard.";
                    return;
                case ConsoleKey.V: PasteFromClipboard(); return;
            }
        }
        // Option/Ctrl+Backspace — delete the word before the cursor.
        // (Legacy terminals send plain Backspace for Ctrl+Backspace; Alt arrives as Esc+DEL or CSI u.)
        if ((alt || ctrl) && key.Key == ConsoleKey.Backspace) { _editor.DeleteWordBackward(); return; }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = Focus.Tree;
                _status = "File panel. Enter opens; Ctrl+F searches the selection, Ctrl+F+F all folders.";
                return;
            case ConsoleKey.LeftArrow:
                if (_editor.HasSelection && !shift && !alt && !ctrl) { _editor.CollapseSelection(toStart: true); return; }
                Prep();
                if (alt || ctrl) _editor.WordLeft(); else _editor.MoveLeft();
                return;
            case ConsoleKey.RightArrow:
                if (_editor.HasSelection && !shift && !alt && !ctrl) { _editor.CollapseSelection(toStart: false); return; }
                Prep();
                if (alt || ctrl) _editor.WordRight(); else _editor.MoveRight();
                return;
            case ConsoleKey.UpArrow:
                Prep();
                if (alt) _editor.ParagraphUp(); else _editor.MoveUp();
                return;
            case ConsoleKey.DownArrow:
                Prep();
                if (alt) _editor.ParagraphDown(); else _editor.MoveDown();
                return;
            case ConsoleKey.PageUp: Prep(); _editor.PageUp(); return;
            case ConsoleKey.PageDown: Prep(); _editor.PageDown(); return;
            case ConsoleKey.Home: Prep(); if (ctrl) _editor.DocHome(); else _editor.Home(); return;
            case ConsoleKey.End: Prep(); if (ctrl) _editor.DocEnd(); else _editor.End(); return;
            case ConsoleKey.Enter: _editor.InsertNewline(); return;
            case ConsoleKey.Backspace: _editor.Backspace(); return;
            case ConsoleKey.Delete: _editor.Delete(); return;
            case ConsoleKey.Tab: _editor.InsertTab(); return;
        }

        if (Typed(key) is var c and not '\0') _editor.InsertChar(c);
    }

    /// <summary>↑↓ PgUp/PgDn Home/End move through the hits (typing and results alike); false — another key.</summary>
    private bool MoveHit(ConsoleKey key, bool homeEnd)
    {
        int page = Math.Max(1, _resultsRowCount), last = Math.Max(0, _hits.Count - 1);
        int? to = key switch
        {
            ConsoleKey.UpArrow => _hitIndex - 1,
            ConsoleKey.DownArrow => _hitIndex + 1,
            ConsoleKey.PageUp => _hitIndex - page,
            ConsoleKey.PageDown => _hitIndex + page,
            ConsoleKey.Home when homeEnd => 0,
            ConsoleKey.End when homeEnd => last,
            _ => null,
        };
        if (to is not { } target) return false;
        _hitIndex = Math.Clamp(target, 0, last);
        return true;
    }

    private void HandleSearchInput(ConsoleKeyInfo key)
    {
        PollSearch(); // a scan that has just finished counts for this key (Esc, Enter)
        if (IsCtrlF(key)) { ToggleSearchScope(); return; }
        if (MoveHit(key.Key, homeEnd: false)) return;

        switch (key.Key)
        {
            case ConsoleKey.Escape: // to the results below; a second Esc hides the panel
                if (_hits.Count > 0)
                {
                    _mode = UiMode.SearchResults;
                    _status = $"Found: {_hits.Count}{(_hitsTruncated ? "+" : "")}. Enter opens above, Esc hides the panel.";
                }
                else
                {
                    CancelSearch(); // a scan still running must not pop the panel up later
                    _mode = UiMode.Normal;
                    _resultsVisible = false;
                    _status = "Search cancelled.";
                }
                return;
            case ConsoleKey.Enter: // open the current hit (the search already ran while typing)
                if (Searchable(_searchQuery.ToString()) && _hits.Count > 0)
                    OpenHit(_hits[_hitIndex]);
                return;
            case ConsoleKey.Backspace:
                if (_searchQuery.Length > 0)
                {
                    string s = _searchQuery.ToString();
                    int from = Cells.PrevBoundary(s, s.Length);
                    _searchQuery.Remove(from, s.Length - from);
                }
                RunLiveSearch();
                return;
            case ConsoleKey.U when Ctrl(key):
                _searchQuery.Clear();
                RunLiveSearch();
                return;
        }
        if (Typed(key) is var c and not '\0')
        {
            _searchQuery.Append(c);
            RunLiveSearch();
        }
    }

    /// <summary>Keys of the focused results panel; false — not one of them (it then works as usual).</summary>
    private bool HandleSearchResults(ConsoleKeyInfo key)
    {
        if (IsCtrlF(key))
        {
            ToggleSearchScope();
            _mode = UiMode.SearchInput;
            return true;
        }

        if (MoveHit(key.Key, homeEnd: true)) return true;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = UiMode.Normal;
                _resultsVisible = false;
                _status = "Search panel hidden (Ctrl+G brings it back).";
                return true;
            case ConsoleKey.Enter:
                if (_hits.Count > 0) OpenHit(_hits[_hitIndex]);
                return true;
        }
        return false;
    }

    // The Russian ЙЦУКЕН layout by physical key, so a confirmation can be answered without switching layouts.
    private const string CyrillicKeys = "йцукенгшщзфывапролдячсмить";
    private const string LatinKeys = "qwertyuiopasdfghjklzxcvbnm";

    /// <summary>The answer letter of a key: the typed Latin letter, else the physical key's letter.</summary>
    private static char AnswerLetter(ConsoleKeyInfo key)
    {
        char ch = char.ToLowerInvariant(key.KeyChar);
        if (ch is >= 'a' and <= 'z') return ch;
        int cyr = CyrillicKeys.IndexOf(ch);
        if (cyr >= 0) return LatinKeys[cyr];
        return key.Key is >= ConsoleKey.A and <= ConsoleKey.Z ? (char)('a' + (key.Key - ConsoleKey.A)) : '\0';
    }

    private void HandleConfirm(ConsoleKeyInfo key)
    {
        var confirm = _confirm;
        if (confirm == null || key.Key == ConsoleKey.Escape)
        {
            _confirm = null;
            _mode = UiMode.Normal;
            _status = "Cancelled.";
            return;
        }
        char answer = AnswerLetter(key);
        foreach (var (k, act) in confirm.Choices)
        {
            if (k != answer) continue;
            _confirm = null;
            _mode = UiMode.Normal;
            act(); // may ask the next question
            return;
        }
        // Any other key (Enter included) leaves the question standing: no destructive default.
    }
}
