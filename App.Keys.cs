namespace ConsoleEditor;

/// <summary>Keyboard handling: search entry points and per-mode key handlers.</summary>
internal sealed partial class App
{
    // ============================ Keys ============================

    private bool IsSearchShortcut(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.F7
        || (key.Key == ConsoleKey.F && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        || (_focus == Focus.Tree && key.Key == ConsoleKey.F
            && key.Modifiers.HasFlag(ConsoleModifiers.Shift)
            && !key.Modifiers.HasFlag(ConsoleModifiers.Control));

    /// <summary>Exactly Shift+F (no Ctrl/Alt).</summary>
    internal static bool IsShiftF(ConsoleKeyInfo key) =>
        !key.Modifiers.HasFlag(ConsoleModifiers.Control)
        && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)
        && key.Key == ConsoleKey.F && key.Modifiers.HasFlag(ConsoleModifiers.Shift);

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

    private void OpenSearch(string initial = "")
    {
        _searchQuery.Clear();
        if (initial.Length > 0) _searchQuery.Append(initial);
        _liveQuery = "";
        _mode = UiMode.SearchInput;
        if (_searchQuery.Length > 0) RunLiveSearch();
    }

    /// <summary>Copy the editor selection to the system clipboard.</summary>
    private void CopySelection()
    {
        string text = _editor.SelectedText();
        if (text.Length == 0) return;
        Clipboard.Copy(text);
        _status = $"Copied to the clipboard: {text.Length} chars";
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
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
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (_mode == UiMode.Normal) TryQuit();
            else { _mode = UiMode.Normal; _pending = PendingKind.None; _pendingTab = null; _pendingDeletePath = null; _status = "Cancelled (Ctrl+C)."; }
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
        if ((key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            || key.Key == ConsoleKey.F10)
        { TryQuit(); return; }
        if ((key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            || (key.Key == ConsoleKey.F2 && _focus == Focus.Editor))
        { Save(); return; }
        // Shift+F with an editor selection searches for it instead of overwriting it with F.
        if (_focus == Focus.Editor && IsShiftF(key))
        {
            string seed = SelectionSearchSeed();
            if (seed.Length > 0)
            {
                OpenSearch(seed);
                return;
            }
        }
        if (IsSearchShortcut(key))
        {
            OpenSearch(SelectionSearchSeed());
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
        if (key.Key == ConsoleKey.F8) { JumpHit(+1); return; }
        if (key.Key == ConsoleKey.F9) { JumpHit(-1); return; }

        // Undo / Redo of the active tab
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool altMod = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        bool shiftMod = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if ((ctrl && key.Key == ConsoleKey.Z && !shiftMod)
            || (altMod && key.Key == ConsoleKey.Backspace))
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
                if (_resultsVisible) { _resultsVisible = false; _status = "Search panel hidden (F8 brings it back)."; }
                break;
            case ConsoleKey.UpArrow: _tree.MoveUp(); break;
            case ConsoleKey.DownArrow: _tree.MoveDown(); break;
            case ConsoleKey.RightArrow: _tree.ExpandOrDescend(); break;
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
            case ConsoleKey.F6:
                if (_editor.HasFile) { _focus = Focus.Editor; _status = "Editing. F6/Esc returns to the tree."; }
                else _status = "Open a file with Enter first.";
                break;
            case ConsoleKey.F5:
                _tree.Refresh();
                SyncWatcher();
                _status = "Tree refreshed.";
                break;
            case ConsoleKey.R when key.Modifiers == 0:
                _tree.Refresh();
                SyncWatcher();
                _status = "Tree refreshed.";
                break;
            case ConsoleKey.Delete:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    RequestDeleteSelected(); // Shift+Delete — delete from disk (with confirmation)
                }
                else if (_tree.Selected is { IsRoot: true } rootNode) RemoveRootUi(rootNode);
                else _status = "Delete only removes a root folder from the panel; Shift+Delete deletes from disk.";
                break;
            case ConsoleKey.F2:
                StartRename();
                break;
        }
    }

    private void HandleEditorKey(ConsoleKeyInfo key)
    {
        if (!_editor.HasFile) { _focus = Focus.Tree; return; }
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

        // Shift extends the selection; plain movement without Shift clears it.
        void Prep()
        {
            if (shift) _editor.EnsureAnchor();
            else _editor.ClearSelection();
        }

        // Option+B/F: how meta-mode terminals send Option+←/→ (Esc+b / Esc+f).
        if (alt && key.KeyChar is 'b' or 'B') { Prep(); _editor.WordLeft(); return; }
        if (alt && key.KeyChar is 'f' or 'F') { Prep(); _editor.WordRight(); return; }
        // Ctrl+X — cut the selection to the clipboard.
        if (ctrl && key.Key == ConsoleKey.X && _editor.HasSelection)
        {
            CopySelection();
            _editor.DeleteSelection();
            _status = "Cut to the clipboard.";
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.F6:
                _focus = Focus.Tree;
                _status = "File panel. Enter opens, Shift+F searches.";
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

        // Regular printable characters (including capital F — Shift+F types here!)
        if (!ctrl && !key.Modifiers.HasFlag(ConsoleModifiers.Alt)
            && key.KeyChar >= ' ' && key.KeyChar != (char)127)
            _editor.InsertChar(key.KeyChar);
    }

    private void HandleSearchInput(ConsoleKeyInfo key)
    {
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
        int page = Math.Max(1, _resultsRowCount);
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _mode = UiMode.Normal;
                _resultsVisible = false;
                _status = "Search panel hidden (F8 brings it back).";
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

        // Disk deletion: D/S/Y/Enter — delete, Esc — cancel. There is nothing to save here.
        if (_pending == PendingKind.DeleteNode)
        {
            if (key.Key is ConsoleKey.D or ConsoleKey.S or ConsoleKey.Y or ConsoleKey.Enter
                || ch is 'd' or 's' or 'y')
            {
                DoPending();
            }
            else if (key.Key == ConsoleKey.Escape)
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
