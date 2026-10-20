namespace ConsoleEditor;

/// <summary>Rendering: title, panels, tab bar, results panel, status and help lines.</summary>
internal sealed partial class App
{
    // ============================ Rendering ============================

    private static string Clip(string s, int width) =>
        width <= 0 ? "" : s.Length <= width ? s : s[..width];

    private static void WriteAt(int x, int y, string text, ConsoleColor fg, ConsoleColor bg, int width)
    {
        if (width <= 0 || x < 0 || y < 0) return;
        Console.SetCursorPosition(x, y);
        Console.ForegroundColor = fg;
        Console.BackgroundColor = bg;
        string clipped = Clip(text, width);
        Console.Write(clipped);
        if (clipped.Length < width) Console.Write(new string(' ', width - clipped.Length));
    }

    /// <summary>
    /// An editor line with syntax highlighting, horizontal scrolling and
    /// selection: the [selFrom, selTo) range in line columns is drawn white
    /// on dark blue; selEol — the selection continues onto the next line.
    /// </summary>
    private static void WriteHighlighted(int x, int y, List<Token> tokens, int scrollX, int width,
        int selFrom = -1, int selTo = -1, bool selEol = false)
    {
        if (width <= 0) return;
        Console.SetCursorPosition(x, y);
        int printed = 0, col = 0, limit = scrollX + width;
        bool full = false;
        foreach (var tok in tokens)
        {
            if (full) break;
            int start = col, end = col + tok.Text.Length;
            col = end;
            if (end <= scrollX || start >= limit) continue;
            int from = Math.Max(start, scrollX), to = Math.Min(end, limit);
            var fg = Highlighter.ColorFor(tok.Kind);
            int p = from;
            while (p < to)
            {
                bool inSel = selFrom >= 0 && p >= selFrom && p < selTo;
                int segEnd = inSel ? Math.Min(to, selTo)
                    : selFrom > p ? Math.Min(to, selFrom)
                    : to;
                if (segEnd <= p) segEnd = to;
                Console.ForegroundColor = inSel ? ConsoleColor.White : fg;
                Console.BackgroundColor = inSel ? ConsoleColor.DarkBlue : ConsoleColor.Black;
                string seg = tok.Text.Substring(p - start, segEnd - p);
                Console.Write(seg);
                printed += seg.Length;
                p = segEnd;
                if (printed >= width) { full = true; break; }
            }
        }
        if (printed < width)
        {
            if (selEol) // a selected line break — one space in the selection colour
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.Write(' ');
                printed++;
            }
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.BackgroundColor = ConsoleColor.Black;
            if (printed < width) Console.Write(new string(' ', width - printed));
        }
    }

    private void Draw()
    {
        int w = Console.WindowWidth, h = Console.WindowHeight;
        if (w != _lastW || h != _lastH) { Console.Clear(); _lastW = w; _lastH = h; }
        Console.CursorVisible = false;

        if (w < 42 || h < 10)
        {
            WriteAt(0, 0, " Terminal too small (need at least 42x10).", ConsoleColor.White, ConsoleColor.DarkRed, w);
            return;
        }

        _contentTop = 2;
        _statusY = h - 2;
        int helpY = h - 1;

        // The bottom results panel takes ~30% of the height from the main panels.
        int resultsTotal = _resultsVisible ? Math.Clamp(h * 30 / 100, 4, 10) : 0;
        int contentH = _statusY - _contentTop - resultsTotal;
        if (contentH < 3)
        {
            resultsTotal = Math.Max(0, _statusY - _contentTop - 3);
            contentH = _statusY - _contentTop - resultsTotal;
        }
        _mainContentH = contentH;
        _resultsTop = _contentTop + contentH;
        _resultsRowsTop = _resultsTop + 1;
        _resultsRowCount = Math.Max(0, resultsTotal - 1);

        // Title
        // The active element of the top panel: the selected node when the tree
        // is focused, the open file in the editor, the current hit's file in search.
        string activeLabel;
        bool activeIsPath;
        bool showDirty = false;
        string activeTabLabel = "";
        if (_mode == UiMode.SearchResults && _hits.Count > 0)
        {
            activeLabel = _hits[Math.Clamp(_hitIndex, 0, _hits.Count - 1)].FilePath;
            activeIsPath = true;
        }
        else if (_focus == Focus.Tree)
        {
            activeLabel = _tree.Selected?.FullPath ?? _tree.Roots[0].FullPath;
            activeIsPath = true;
        }
        else if (_editor.HasFile)
        {
            activeLabel = _editor.FilePath!;
            activeIsPath = true;
            showDirty = _editor.Dirty;
            activeTabLabel = _tabs.Tabs.Count > 1 ? $" [tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}]" : "";
        }
        else
        {
            activeLabel = "no file open";
            activeIsPath = false;
        }
        var title = BuildTitle(activeLabel, activeIsPath, showDirty, activeTabLabel);
        _titleText = title.Text;
        _titlePathSeg = title.Path;
        if (_titleSelFor != _titleText) { _titleSelFrom = -1; _titleSelTo = -1; }
        WriteAt(0, 0, _titleText, ConsoleColor.White, ConsoleColor.DarkBlue, w);
        if (HasTitleSelection)
        {
            int from = Math.Clamp(_titleSelFrom, 0, _titleText.Length);
            int to = Math.Clamp(_titleSelTo, from, Math.Min(_titleText.Length, w));
            if (to > from)
                WriteAt(from, 0, _titleText.Substring(from, to - from),
                    ConsoleColor.DarkBlue, ConsoleColor.White, to - from);
        }

        DrawPanels(w, contentH);
        if (_resultsVisible) DrawResultsPanel(w);

        DrawStatusAndHelp(w, helpY);
        PlaceCursor(w, h);
    }

    private void PlaceCursor(int w, int h)
    {
        if (_mode is UiMode.SearchInput or UiMode.PathInput)
        {
            string label = _mode == UiMode.SearchInput ? SearchPrompt : CurrentPathPrompt();
            int typed = _mode == UiMode.SearchInput ? _searchQuery.Length : _pathInput.Length;
            int x = Math.Min(w - 1, label.Length + typed);
            Console.SetCursorPosition(Math.Max(0, x), _statusY);
            Console.CursorVisible = true;
        }
        else if (_mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasFile)
        {
            int x = _editorTextX + (_editor.Cx - _editor.ScrollX);
            int y = _contentTop + (_editor.Cy - _editor.ScrollY);
            if (x >= 0 && x < w && y >= _contentTop && y < _contentTop + _mainContentH)
            {
                Console.SetCursorPosition(x, y);
                Console.CursorVisible = true;
            }
        }
    }

    private const string SearchPrompt = " Search contents: ";
    private const string PathPrompt = " Add folder: ";

    private void DrawPanels(int w, int contentH)
    {
        int leftW = Math.Clamp(w * 32 / 100, 22, Math.Max(22, w - 24));
        int dividerX = leftW;
        int editorX = leftW + 1;
        int editorW = w - editorX;

        _leftW = leftW;
        _editorX = editorX;

        _treeTop = 1; // the tree has no header — its first row is right below the title
        _treeContentH = contentH + 1;
        _tree.ViewHeight = _treeContentH;
        _tree.EnsureVisible();
        int gutter = Math.Max(4, _editor.LineCount.ToString().Length + 1);
        _gutter = gutter;
        _editor.ViewHeight = contentH;
        _editor.ViewWidth = Math.Max(1, editorW - gutter);
        // Pull the view to the cursor only on resize, else the wheel cannot scroll away from the cursor.
        if (_editor.HasFile && (_editor.ViewWidth != _prevEditorViewW || _editor.ViewHeight != _prevEditorViewH))
            _editor.EnsureCursorVisible();
        _prevEditorViewW = _editor.ViewWidth;
        _prevEditorViewH = _editor.ViewHeight;
        _editorTextX = editorX + gutter;

        // Above the editor — the tab bar; the tree has no header.
        DrawTabBar(editorX, editorW);

        // The block-comment state must be carried from the file start to the window.
        bool inBlock = false;
        if (_editor.HasFile)
            for (int i = 0; i < _editor.ScrollY && i < _editor.LineCount; i++)
                Highlighter.Tokenize(_editor.LineText(i), _editor.Extension, ref inBlock);

        bool hasSel = _editor.HasSelection;
        var (selSL, selSC, selEL, selEC) = hasSel ? _editor.SelectionRange() : (-1, 0, -1, 0);

        // The tree also occupies the tab-bar row, so it has one more row than the editor.
        for (int tr = 0; tr < _treeContentH; tr++)
        {
            int y = _treeTop + tr;

            // Left panel: the tree
            int idx = _tree.ScrollOffset + tr;
            if (idx < _tree.Visible.Count)
            {
                var node = _tree.Visible[idx];
                string icon = node.IsDir ? (node.Expanded ? "▾ " : "▸ ") : "  ";
                string text = new string(' ', node.Depth * 2) + icon + node.Name + (node.IsDir ? "/" : "");
                bool sel = idx == _tree.SelectedIndex;
                var bg = sel ? (_focus == Focus.Tree ? ConsoleColor.DarkCyan : ConsoleColor.DarkGray) : ConsoleColor.Black;
                var fg = sel ? ConsoleColor.White : node.IsDir ? ConsoleColor.White : ConsoleColor.Gray;
                WriteAt(0, y, text, fg, bg, leftW);
            }
            else WriteAt(0, y, "", ConsoleColor.Gray, ConsoleColor.Black, leftW);

            WriteAt(dividerX, y, "│", ConsoleColor.DarkGray, ConsoleColor.Black, 1);

            // Right panel: the editor (nothing to draw on the tab-bar row)
            int r = tr - (_contentTop - _treeTop);
            if (r < 0) continue;
            if (!_editor.HasFile)
            {
                string hint = r switch
                {
                    0 => " Pick a file on the left and press Enter.",
                    1 => " Shift+F — fuzzy content search across the whole folder.",
                    3 => " ←/→ collapse and expand folders,",
                    4 => " Tab/F6 switches panels, Ctrl+Q quits.",
                    _ => "",
                };
                WriteAt(editorX, y, hint, ConsoleColor.DarkGray, ConsoleColor.Black, editorW);
            }
            else
            {
                int lineNo = _editor.ScrollY + r;
                if (lineNo < _editor.LineCount)
                {
                    bool curLine = lineNo == _editor.Cy && _focus == Focus.Editor;
                    string num = (lineNo + 1).ToString().PadLeft(gutter - 1) + " ";
                    WriteAt(editorX, y, num, curLine ? ConsoleColor.Yellow : ConsoleColor.DarkGray,
                        ConsoleColor.Black, gutter);
                    var tokens = Highlighter.Tokenize(_editor.LineText(lineNo), _editor.Extension, ref inBlock);
                    int sFrom = -1, sTo = -1;
                    bool sEol = false;
                    if (hasSel && lineNo >= selSL && lineNo <= selEL)
                    {
                        sFrom = lineNo == selSL ? selSC : 0;
                        sTo = lineNo == selEL ? selEC : _editor.LineText(lineNo).Length;
                        sEol = lineNo < selEL;
                    }
                    WriteHighlighted(_editorTextX, y, tokens, _editor.ScrollX, editorW - gutter, sFrom, sTo, sEol);
                }
                else WriteAt(editorX, y, " ~", ConsoleColor.DarkGray, ConsoleColor.Black, editorW);
            }
        }
    }

    /// <summary>
    /// Tab bar above the editor: ⚑ — pinned (no close cross),
    /// ● — has edits, × — close. On overflow the window shifts so the
    /// active tab stays visible; ‹ › at the edges mean some are hidden.
    /// </summary>
    private void DrawTabBar(int editorX, int editorW)
    {
        _tabCells.Clear();
        bool edFocused = _focus == Focus.Editor;
        if (_tabs.Tabs.Count == 0)
        {
            WriteAt(editorX, 1, " (no file)",
                edFocused ? ConsoleColor.White : ConsoleColor.DarkCyan,
                edFocused ? ConsoleColor.DarkCyan : ConsoleColor.Black, editorW);
            return;
        }
        WriteAt(editorX, 1, "", ConsoleColor.Gray, ConsoleColor.Black, editorW);

        var labels = _tabs.Tabs.Select(t =>
            " " + (t.Pinned ? "⚑ " : "") + t.Name
            + (t.Editor.Dirty ? " ●" : "") + (t.Pinned ? "" : " ×") + " ").ToList();

        int start = _tabs.ActiveIndex;
        int used = Math.Min(labels[start].Length, editorW);
        while (start > 0 && used + labels[start - 1].Length <= editorW)
        {
            start--;
            used += labels[start].Length;
        }

        int x = editorX;
        int lastDrawn = start - 1;
        for (int i = start; i < labels.Count; i++)
        {
            int remaining = editorX + editorW - x;
            if (remaining <= 0) break;
            int cellW = Math.Min(labels[i].Length, remaining);
            bool active = i == _tabs.ActiveIndex;
            var bg = active
                ? (edFocused ? ConsoleColor.DarkCyan : ConsoleColor.DarkGray)
                : ConsoleColor.Black;
            var fg = active ? ConsoleColor.White
                : _tabs.Tabs[i].Pinned ? ConsoleColor.Cyan : ConsoleColor.Gray;
            WriteAt(x, 1, labels[i], fg, bg, cellW);
            int closeRel = labels[i].IndexOf('×');
            _tabCells.Add((i, x, x + cellW - 1, closeRel >= 0 && closeRel < cellW ? x + closeRel : -1));
            x += cellW;
            lastDrawn = i;
        }
        if (start > 0) WriteAt(editorX, 1, "‹", ConsoleColor.Yellow, ConsoleColor.Black, 1);
        if (lastDrawn < labels.Count - 1)
            WriteAt(editorX + editorW - 1, 1, "›", ConsoleColor.Yellow, ConsoleColor.Black, 1);
    }

    /// <summary>Bottom panel with search results (below the tree and the editor).</summary>
    private void DrawResultsPanel(int w)
    {
        bool focused = _mode is UiMode.SearchResults or UiMode.SearchInput;
        WriteAt(0, _resultsTop,
            $" Search results for \"{_searchQuery}\": {_hitIndex + (_hits.Count > 0 ? 1 : 0)}/{_hits.Count} · Enter opens · F8/F9 next/prev · Esc hides",
            focused ? ConsoleColor.White : ConsoleColor.Magenta,
            focused ? ConsoleColor.DarkMagenta : ConsoleColor.Black, w);

        int rows = _resultsRowCount;
        if (_hitIndex < _hitScroll) _hitScroll = _hitIndex;
        if (rows > 0 && _hitIndex >= _hitScroll + rows) _hitScroll = _hitIndex - rows + 1;
        _hitScroll = Math.Clamp(_hitScroll, 0, Math.Max(0, _hits.Count - Math.Max(1, rows)));

        for (int r = 0; r < rows; r++)
        {
            int y = _resultsRowsTop + r;
            int idx = _hitScroll + r;
            if (idx >= _hits.Count)
            {
                WriteAt(0, y, "", ConsoleColor.Gray, ConsoleColor.Black, w);
                continue;
            }
            var hit = _hits[idx];
            bool sel = idx == _hitIndex;
            var bg = sel ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            string prefix = $" {hit.RelativePath}:{hit.LineNumber} ";
            int avail = Math.Max(1, w - prefix.Length);
            string snippet = hit.LineText.Trim();
            if (snippet.Length > avail)
            {
                int lead = hit.LineText.Length - hit.LineText.TrimStart().Length;
                int start = Math.Clamp(hit.MatchStart - lead - 12, 0, Math.Max(0, snippet.Length - avail));
                snippet = (start > 0 ? "…" : "") + snippet.Substring(start, Math.Min(avail - (start > 0 ? 1 : 0), snippet.Length - start));
            }
            WriteAt(0, y, prefix, sel ? ConsoleColor.White : ConsoleColor.Cyan, bg, prefix.Length);
            WriteAt(prefix.Length, y, snippet, sel ? ConsoleColor.White : ConsoleColor.Gray, bg, avail);
        }
    }

    private void DrawStatusAndHelp(int w, int helpY)
    {
        if (_mode == UiMode.PathInput)
        {
            // Typed text in white, the ghost suggestion trailing in grey.
            string left = CurrentPathPrompt() + _pathInput;
            WriteAt(0, _statusY, left, ConsoleColor.White, ConsoleColor.DarkBlue, Math.Min(w, left.Length));
            int gx = left.Length;
            if (_pathGhost.Length > 0 && gx < w)
            {
                WriteAt(gx, _statusY, _pathGhost, ConsoleColor.DarkGray, ConsoleColor.DarkBlue,
                    Math.Min(_pathGhost.Length, w - gx));
                gx += _pathGhost.Length;
            }
            if (gx < w) WriteAt(gx, _statusY, "", ConsoleColor.White, ConsoleColor.DarkBlue, w - gx);

            // The help line shows live completion candidates (the chosen one highlighted).
            if (_pathPurpose == PathPurpose.Rename)
            {
                WriteAt(0, helpY, " Enter renames · Esc cancels · type just the new name (no path); open tabs update themselves",
                    ConsoleColor.DarkGray, ConsoleColor.Black, w);
            }
            else if (_pathCandidates.Count > 0)
            {
                WriteAt(0, helpY, "", ConsoleColor.DarkGray, ConsoleColor.Black, w);
                int hx = 0;
                void Seg(string text, ConsoleColor fg, ConsoleColor bg)
                {
                    if (hx >= w) return;
                    WriteAt(hx, helpY, text, fg, bg, Math.Min(text.Length, w - hx));
                    hx += text.Length;
                }
                Seg(" Tab ▸ ", ConsoleColor.DarkGray, ConsoleColor.Black);
                for (int i = 0; i < _pathCandidates.Count && hx < w; i++)
                {
                    string cand = _pathCandidates[i];
                    string name = Path.GetFileName(cand.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (cand.EndsWith(Path.DirectorySeparatorChar) || cand.EndsWith(Path.AltDirectorySeparatorChar))
                        name += "/";
                    bool sel = i == _pathCandidateIndex;
                    Seg(name + "  ", sel ? ConsoleColor.White : ConsoleColor.Gray,
                        sel ? ConsoleColor.DarkBlue : ConsoleColor.Black);
                }
            }
            else if (_pathInput.Length > 0)
            {
                WriteAt(0, helpY, " No matches · Enter uses it as-is · Esc cancels · ~ and relative paths are supported",
                    ConsoleColor.DarkGray, ConsoleColor.Black, w);
            }
            else
            {
                string pathHelp = _pathPurpose switch
                {
                    PathPurpose.CreateFile => " Enter creates the file and opens it · Tab/↑↓ completion · a trailing / creates a folder · Esc cancels",
                    PathPurpose.CreateFolder => " Enter creates the folder · Tab/↑↓ completion · Esc cancels",
                    _ => " Enter adds the folder to the left panel · Tab/↑↓ completion · Esc cancels · a file path opens the file",
                };
                WriteAt(0, helpY, pathHelp, ConsoleColor.DarkGray, ConsoleColor.Black, w);
            }
            return;
        }
        if (_mode == UiMode.SearchInput)
        {
            string suffix = _searchQuery.ToString().Trim().Length >= 2
                ? $"   · found: {_hits.Count}"
                : "   · search starts at 2 characters";
            WriteAt(0, _statusY, SearchPrompt + _searchQuery + suffix, ConsoleColor.White, ConsoleColor.DarkBlue, w);
            WriteAt(0, helpY, " Searching live as you type · ↑↓ pick in the bottom panel · Enter opens · Esc to results · \"mngr\" finds \"manager\"",
                ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }
        if (_mode == UiMode.Confirm)
        {
            string confirmText = _pending switch
            {
                PendingKind.Quit => $" Unsaved tabs: {_tabs.DirtyCount}!  S saves all · D quits without saving · Esc cancels",
                PendingKind.CloseTab when _pendingTab != null => $" Tab \"{_pendingTab.Name}\" has changes!  S saves · D closes without saving · Esc cancels",
                PendingKind.DeleteNode => $" Delete {(_pendingDeleteIsDir ? "folder with all its contents" : "file")} \"{Path.GetFileName(_pendingDeletePath ?? "")}\" from disk forever?  D deletes · Esc cancels",
                _ => " Confirm: S — yes · D — no · Esc — cancel",
            };
            WriteAt(0, _statusY, confirmText, ConsoleColor.White, ConsoleColor.DarkRed, w);
            WriteAt(0, helpY, "", ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }

        string modeLabel = _mode == UiMode.SearchResults ? "SEARCH" : _focus == Focus.Tree ? "TREE" : "EDITOR";
        string selInfo = _editor.HasSelection ? $" · selected: {_editor.SelectionCharCount()}" : "";
        string pos = _editor.HasFile ? $" Ln {_editor.Cy + 1}/{_editor.LineCount}, Col {_editor.Cx + 1}{selInfo} " : "";
        int mainW = Math.Max(0, w - pos.Length);
        WriteAt(0, _statusY, $" [{modeLabel}] {_status}", ConsoleColor.Black, ConsoleColor.Gray, mainW);
        if (pos.Length > 0) WriteAt(mainW, _statusY, pos, ConsoleColor.Black, ConsoleColor.Gray, pos.Length);

        string help = _mode == UiMode.SearchResults
            ? " ↑↓ PgUp/PgDn or wheel to pick · Enter/double-click opens above · Esc hides the panel"
            : _focus == Focus.Tree
                ? " ↑↓ select · Enter opens · Ctrl+N new file · Ctrl+Shift+N folder · F2 rename · Shift+Del delete · Ctrl+O add root · Shift+F search"
                : " F6/Esc to tree · mouse/Shift+arrows select, double-click a word · Option+←→ by words, Option+↑↓ by paragraphs · Ctrl+C/X clipboard · Ctrl+S save · Ctrl+W/P tabs";
        if (_mode == UiMode.Normal && _resultsVisible)
            help += " · F8/F9 — search results";
        WriteAt(0, helpY, help, ConsoleColor.DarkGray, ConsoleColor.Black, w);
    }
}
