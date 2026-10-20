/// <summary>Keyboard handling: search entry points and per-mode key handlers.</summary>
internal sealed partial class App
{
    // ============================ Keys ============================

    internal static bool IsCtrlKey(ConsoleKeyInfo key, ConsoleKey expected) =>
        key.Key == expected
        && key.Modifiers.HasFlag(ConsoleModifiers.Control)
        && !key.Modifiers.HasFlag(ConsoleModifiers.Alt);

    internal static bool IsCtrlF(ConsoleKeyInfo key) => IsCtrlKey(key, ConsoleKey.F);
    internal static bool IsCtrlR(ConsoleKeyInfo key) => IsCtrlKey(key, ConsoleKey.R);

    /// <summary>Ctrl+G jumps to the next hit; Ctrl+Shift+G to the previous one.</summary>
    internal static int SearchJumpDelta(ConsoleKeyInfo key) =>
        IsCtrlKey(key, ConsoleKey.G)
            ? key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : +1
            : 0;

    /// <summary>Turn selected text into a search query: spaces instead of line breaks, trimmed.</summary>
    internal static string NormalizeSearchSeed(string text)
    {
        string cleaned = string.Join(" ",
            text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private string SelectionSearchSeed() =>
        _focus == Focus.Editor && _editor.HasSelection
            ? NormalizeSearchSeed(_editor.SelectedText())
            : "";

    private void ApplySearchScope(SearchScope scope, string? targetPath, bool targetIsDir)
    {
        CancelSearch();
        _searchScope = scope;
        _searchTargetPath = targetPath;
        _searchTargetIsDir = targetIsDir;
        _liveQuery = "";
        _liveSource = new List<SearchHit>();
        _hits = new List<SearchHit>();
        _hitIndex = 0;
        _hitScroll = 0;
        _resultsVisible = false;
    }

    private void OpenSearch(string initial, SearchScope scope, string? targetPath = null, bool targetIsDir = false)
    {
        if (scope != SearchScope.AllFolders && targetPath == null)
        {
            _status = "Open or select a file/folder first. Ctrl+F+F searches all folders.";
            return;
        }

        ApplySearchScope(scope, targetPath, targetIsDir);
        _searchQuery.Clear();
        if (initial.Length > 0) _searchQuery.Append(initial);
        _mode = UiMode.SearchInput;
        if (_searchQuery.Length > 0) RunLiveSearch();
    }

    /// <summary>Ctrl+F: search from the focused target (selected file/folder or open file).</summary>
    private void OpenScopedSearch(string initial)
    {
        if (ResolveScopedSearchTarget() is { } target)
            OpenSearch(initial, target.Scope, target.Path, target.IsDir);
        else
            _status = "Open or select a file/folder first. Ctrl+F+F searches all folders.";
    }

    /// <summary>Second Ctrl+F: widen to all folders (or narrow back to the focused target).</summary>
    private void ToggleSearchScope()
    {
        if (_searchScope != SearchScope.AllFolders)
            ApplySearchScope(SearchScope.AllFolders, null, false);
        else if (ResolveScopedSearchTarget() is { } target)
            ApplySearchScope(target.Scope, target.Path, target.IsDir);
        else
            return; // stay on all folders; the prompt shows the active scope

        RunLiveSearch();
    }

    private void ArmRenameChord()
    {
        _renameChordArmed = true;
        _renameChordExpiresAtMs = Environment.TickCount64 + 1200;
        _status = "Ctrl+R+R: press Ctrl+R again to rename.";
    }

    /// <summary>Complete Ctrl+R+R only on a second Ctrl+R press.</summary>
    private bool TryCompleteRenameChord(ConsoleKeyInfo key)
    {
        if (!_renameChordArmed) return false;
        _renameChordArmed = false;
        if (_mode != UiMode.Normal || Environment.TickCount64 > _renameChordExpiresAtMs)
            return false;
        if (!IsCtrlR(key))
            return false;

        StartRenameForCurrentFocus();
        return true;
    }

    private void StartRenameForCurrentFocus()
    {
        if (_focus == Focus.Editor && _editor.HasFile)
            StartRenamePath(_editor.FilePath!, isDir: false);
        else
            StartRename();
    }

    /// <summary>Copy the editor selection to the system clipboard.</summary>
    private void CopySelection()
    {
        string text = _editor.SelectedText();
        if (text.Length == 0) return;
        Clipboard.Copy(text);
        _status = $"Copied to the clipboard: {text.Length} chars";
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

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (TryCompleteRenameChord(key)) return;

        // Ctrl+C with a title path selected — copy the path.
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && _mode == UiMode.Normal && HasTitleSelection)
        {
            CopyTitleSelection();
            return;
        }
        // Ctrl+C with an editor selection — copy, not quit.
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && _mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasSelection)
        {
            CopySelection();
            return;
        }
        // Any other key press clears the path selection.
        _titleSelFrom = -1;
        _titleSelTo = -1;

        // In raw mode Ctrl+C arrives as a byte, not a signal — handle it ourselves.
        // It copies (handled above) or cancels a prompt; it never quits the app
        // (quitting is Ctrl+Q, so a stray Ctrl+C can't throw work away).
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (_mode != UiMode.Normal)
            {
                _mode = UiMode.Normal; _pending = PendingKind.None; _pendingTab = null; _pendingDeletePath = null;
                _status = "Cancelled (Ctrl+C).";
            }
            else _status = "Ctrl+C copies a selection. Ctrl+Q quits.";
            return;
        }

        switch (_mode)
        {
            case UiMode.SearchInput: HandleSearchInput(key); return;
            case UiMode.PathInput: HandlePathInput(key); return;
            case UiMode.SearchResults: HandleSearchResults(key); return;
            case UiMode.Confirm: HandleConfirm(key); return;
        }

        // Global keys of the normal mode
        if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        { TryQuit(); return; }
        if (key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        { Save(); return; }
        // Rename: Ctrl+R+R.
        if (IsCtrlR(key)) { ArmRenameChord(); return; }
        // Ctrl+F — inside the selected file/folder (tree) or the open file (editor).
        // A second Ctrl+F in the input widens the same query to all folders.
        if (IsCtrlF(key))
        {
            OpenScopedSearch(SelectionSearchSeed());
            return;
        }
        if (key.Key == ConsoleKey.O && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            OpenPathInput(PathPurpose.AddRoot);
            return;
        }
        if (key.Key == ConsoleKey.N && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            // Ctrl+N — new file; Ctrl+Shift+N — new folder (or a trailing / in the file prompt).
            OpenPathInput(key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                ? PathPurpose.CreateFolder
                : PathPurpose.CreateFile);
            return;
        }
        int searchJump = SearchJumpDelta(key);
        if (searchJump != 0) { JumpHit(searchJump); return; }

        // Undo / Redo of the active tab
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool shiftMod = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (ctrl && key.Key == ConsoleKey.Z && !shiftMod)
        {
            if (_editor.CanUndo) { _editor.Undo(); _focus = Focus.Editor; _status = "Undone. Ctrl+Y redoes it."; }
            else _status = "Nothing to undo.";
            return;
        }
        if ((ctrl && key.Key == ConsoleKey.Y) || (ctrl && shiftMod && key.Key == ConsoleKey.Z))
        {
            if (_editor.CanRedo) { _editor.Redo(); _focus = Focus.Editor; _status = "Redone. Ctrl+Z undoes it."; }
            else _status = "Nothing to redo.";
            return;
        }

        // Tabs
        if (key.Key == ConsoleKey.W && ctrl)
        {
            if (_tabs.Active is { } at) CloseTab(at);
            else _status = "No open tabs.";
            return;
        }
        if (key.Key == ConsoleKey.P && ctrl)
        {
            if (_tabs.Active is { } at) TogglePin(at);
            else _status = "No open tab to pin.";
            return;
        }
        if (key.Key == ConsoleKey.PageDown && ctrl) { SwitchTab(+1); return; }
        if (key.Key == ConsoleKey.PageUp && ctrl) { SwitchTab(-1); return; }
        if (key.Modifiers.HasFlag(ConsoleModifiers.Alt) && key.KeyChar is >= '1' and <= '9')
        {
            int i = key.KeyChar - '1';
            if (i < _tabs.Tabs.Count) { ActivateTab(i, announce: true); _focus = Focus.Editor; }
            return;
        }

        if (_focus == Focus.Tree) HandleTreeKey(key);
        else HandleEditorKey(key);
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
                }
                return;
            case ConsoleKey.Backspace:
                if (_pathInput.Length > 0) _pathInput.Remove(_pathInput.Length - 1, 1);
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
            case ConsoleKey.RightArrow: // accept the whole ghost suggestion
                if (_pathGhost.Length > 0) SetPathInput(_pathInput.ToString() + _pathGhost);
                return;
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _pathInput.Clear();
                UpdatePathCompletions();
                return;
        }
        if (key.KeyChar >= ' ' && key.KeyChar != (char)127
            && !key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            _pathInput.Append(key.KeyChar);
            UpdatePathCompletions();
        }
    }

    private void HandleTreeKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                if (_resultsVisible) { _resultsVisible = false; _status = "Search panel hidden (Ctrl+G brings it back)."; }
                break;
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
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control)
                    && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    RequestDeleteSelected(); // Ctrl+Delete — delete from disk (with confirmation)
                }
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
        if (alt && key.KeyChar is 'b') { Prep(); _editor.WordLeft(); return; }
        if (alt && key.KeyChar is 'f') { Prep(); _editor.WordRight(); return; }
        // Ctrl+X — cut the selection to the clipboard.
        if (ctrl && key.Key == ConsoleKey.X && _editor.HasSelection)
        {
            CopySelection();
            _editor.DeleteSelection();
            _status = "Cut to the clipboard.";
            return;
        }
        // Ctrl+V — paste the system clipboard (multi-line, one undo step).
        if (ctrl && key.Key == ConsoleKey.V) { PasteFromClipboard(); return; }
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

        // Regular printable characters.
        if (!ctrl && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)
            && key.KeyChar >= ' ' && key.KeyChar != (char)127)
            _editor.InsertChar(key.KeyChar);
    }

    private void HandleSearchInput(ConsoleKeyInfo key)
    {
        if (IsCtrlF(key)) { ToggleSearchScope(); return; }

        int page = Math.Max(1, _resultsRowCount);
        switch (key.Key)
        {
            case ConsoleKey.Escape: // to the results below; a second Esc hides the panel
                if (_hits.Count > 0)
                {
                    _mode = UiMode.SearchResults;
                    _status = $"Found: {_hits.Count}. Enter opens above, Esc hides the panel.";
                }
                else
                {
                    _mode = UiMode.Normal;
                    _resultsVisible = false;
                    _status = "Search cancelled.";
                }
                return;
            case ConsoleKey.Enter: // open the current hit (the search already ran while typing)
                if (_searchQuery.ToString().Trim().Length >= 2 && _hits.Count > 0)
                    OpenHit(_hits[_hitIndex]);
                return;
            case ConsoleKey.Backspace:
                if (_searchQuery.Length > 0) _searchQuery.Remove(_searchQuery.Length - 1, 1);
                RunLiveSearch();
                return;
            case ConsoleKey.UpArrow:
                if (_hitIndex > 0) _hitIndex--;
                return;
            case ConsoleKey.DownArrow:
                if (_hitIndex < _hits.Count - 1) _hitIndex++;
                return;
            case ConsoleKey.PageUp:
                _hitIndex = Math.Max(0, _hitIndex - page);
                return;
            case ConsoleKey.PageDown:
                _hitIndex = Math.Min(Math.Max(0, _hits.Count - 1), _hitIndex + page);
                return;
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                _searchQuery.Clear();
                RunLiveSearch();
                return;
        }
        if (key.KeyChar >= ' ' && key.KeyChar != (char)127
            && !key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            _searchQuery.Append(key.KeyChar);
            RunLiveSearch();
        }
    }

    private void HandleSearchResults(ConsoleKeyInfo key)
    {
        if (IsCtrlF(key))
        {
            ToggleSearchScope();
            _mode = UiMode.SearchInput;
            return;
        }

        int page = Math.Max(1, _resultsRowCount);
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = UiMode.Normal;
                _resultsVisible = false;
                _status = "Search panel hidden (Ctrl+G brings it back).";
                break;
            case ConsoleKey.UpArrow:
                if (_hitIndex > 0) _hitIndex--;
                break;
            case ConsoleKey.DownArrow:
                if (_hitIndex < _hits.Count - 1) _hitIndex++;
                break;
            case ConsoleKey.PageUp:
                _hitIndex = Math.Max(0, _hitIndex - page);
                break;
            case ConsoleKey.PageDown:
                _hitIndex = Math.Min(Math.Max(0, _hits.Count - 1), _hitIndex + page);
                break;
            case ConsoleKey.Home: _hitIndex = 0; break;
            case ConsoleKey.End: _hitIndex = Math.Max(0, _hits.Count - 1); break;
            case ConsoleKey.Enter:
                if (_hits.Count > 0) OpenHit(_hits[_hitIndex]);
                break;
        }
    }

    private void HandleConfirm(ConsoleKeyInfo key)
    {
        // Accept both the physical key (ConsoleKey) and the typed character.
        char ch = char.ToLowerInvariant(key.KeyChar);

        // Disk deletion is irreversible, so only an explicit D/Y deletes —
        // Enter and Esc cancel (no destructive default on the easiest key).
        if (_pending == PendingKind.DeleteNode)
        {
            if (key.Key is ConsoleKey.D or ConsoleKey.Y || ch is 'd' or 'y')
            {
                DoPending();
            }
            else if (key.Key is ConsoleKey.Escape or ConsoleKey.Enter)
            {
                _pending = PendingKind.None;
                _pendingDeletePath = null;
                _mode = UiMode.Normal;
                _status = "Deletion cancelled.";
            }
            return;
        }
        if (key.Key == ConsoleKey.S || ch is 's') // save and continue
        {
            if (SaveForConfirm()) DoPending();
            else { _pending = PendingKind.None; _pendingTab = null; _mode = UiMode.Normal; }
        }
        else if (key.Key is ConsoleKey.D or ConsoleKey.N || ch is 'd' or 'n') // do not save
        {
            DoPending();
        }
        else if (key.Key == ConsoleKey.Escape)
        {
            _pending = PendingKind.None;
            _pendingTab = null;
            _mode = UiMode.Normal;
            _status = "Cancelled.";
        }
    }

    // ============================ Mouse ============================
}
