using System.Text;

/// <summary>Rendering: title, panels, tab bar, results panel, status and help lines.</summary>
internal sealed partial class App
{
    // ============================ Rendering ============================

    private readonly List<Token> _tokens = new(); // reusable token scratch for the visible lines

    private static string Clip(string s, int width) =>
        width <= 0 ? "" : s.Length <= width ? s : s[..width];

    // Last colours actually sent to the terminal; setting a colour emits an
    // escape sequence every time, so unchanged colours are not re-sent.
    private static ConsoleColor _lastFg = (ConsoleColor)(-1), _lastBg = (ConsoleColor)(-1);
    private static readonly string Spaces = new(' ', 512);

    // ---- Frame output -----------------------------------------------------
    // On Unix the whole frame is built as one ANSI string and written to
    // stdout in a single write. A frame used to be hundreds of tiny Console
    // calls (cursor moves, colour sets, per-token writes) whose cost and
    // jitter capped the frame rate and made the scroll glide visibly steppy.
    // On Windows (legacy console) the classic Console API path is kept.
    private static readonly bool UseAnsiFrame = !OperatingSystem.IsWindows();
    private static readonly StringBuilder Frame = new(1 << 16);
    private static readonly Stream Stdout = Console.OpenStandardOutput();
    private static char[] _frameChars = new char[1 << 16];
    private static byte[] _frameBytes = new byte[1 << 17];

    // ConsoleColor (enum order) -> ANSI foreground code; background is +10.
    private static readonly int[] FgAnsi =
        { 30, 34, 32, 36, 31, 35, 33, 37, 90, 94, 92, 96, 91, 95, 93, 97 };

    private static void BeginFrame()
    {
        // DEC 2026 synchronized output: supporting terminals present the
        // whole frame atomically (no tearing mid-scroll); others ignore it.
        if (UseAnsiFrame) { Frame.Clear(); Frame.Append("\x1b[?2026h\x1b[?25l"); }
        else Console.CursorVisible = false;
    }

    private static void EndFrame()
    {
        if (!UseAnsiFrame) return;
        Frame.Append("\x1b[?2026l");
        int len = Frame.Length;
        if (_frameChars.Length < len) _frameChars = new char[len * 2];
        Frame.CopyTo(0, _frameChars, 0, len);
        int maxBytes = Encoding.UTF8.GetMaxByteCount(len);
        if (_frameBytes.Length < maxBytes) _frameBytes = new byte[maxBytes * 2];
        int n = Encoding.UTF8.GetBytes(_frameChars.AsSpan(0, len), _frameBytes);
        Stdout.Write(_frameBytes, 0, n);
        Stdout.Flush();
    }

    private static void MoveTo(int x, int y)
    {
        if (UseAnsiFrame) Frame.Append("\x1b[").Append(y + 1).Append(';').Append(x + 1).Append('H');
        else Console.SetCursorPosition(x, y);
    }

    private static void ShowCursor()
    {
        if (UseAnsiFrame) Frame.Append("\x1b[?25h");
        else Console.CursorVisible = true;
    }

    private static void SetColors(ConsoleColor fg, ConsoleColor bg)
    {
        if (UseAnsiFrame)
        {
            if (_lastFg == fg && _lastBg == bg) return;
            _lastFg = fg;
            _lastBg = bg;
            Frame.Append("\x1b[").Append(FgAnsi[(int)fg & 15]).Append(';')
                 .Append(FgAnsi[(int)bg & 15] + 10).Append('m');
            return;
        }
        if (_lastFg != fg) { Console.ForegroundColor = fg; _lastFg = fg; }
        if (_lastBg != bg) { Console.BackgroundColor = bg; _lastBg = bg; }
    }

    private static void Out(ReadOnlySpan<char> s)
    {
        if (UseAnsiFrame) Frame.Append(s);
        else Console.Out.Write(s);
    }

    private static void Out(char c)
    {
        if (UseAnsiFrame) Frame.Append(c);
        else Console.Write(c);
    }

    private static void WriteSpaces(int count)
    {
        while (count > 0)
        {
            int n = Math.Min(count, Spaces.Length);
            Out(Spaces.AsSpan(0, n));
            count -= n;
        }
    }

    private static void WriteAt(int x, int y, string text, ConsoleColor fg, ConsoleColor bg, int width)
    {
        if (width <= 0 || x < 0 || y < 0) return;
        MoveTo(x, y);
        SetColors(fg, bg);
        var span = text.AsSpan(0, Math.Min(text.Length, width));
        Out(span);
        if (span.Length < width) WriteSpaces(width - span.Length);
    }

    /// <summary>
    /// An editor line with syntax highlighting, horizontal scrolling and
    /// selection: the [selFrom, selTo) range in line columns is drawn white
    /// on dark blue; selEol — the selection continues onto the next line.
    /// </summary>
    private static void WriteHighlighted(int x, int y, ReadOnlySpan<char> line, List<Token> tokens,
        int scrollX, int width, int selFrom = -1, int selTo = -1, bool selEol = false)
    {
        if (width <= 0) return;
        MoveTo(x, y);
        int printed = 0, limit = scrollX + width;
        foreach (var tok in tokens)
        {
            int start = tok.Start, end = tok.Start + tok.Length;
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
                SetColors(inSel ? ConsoleColor.White : fg,
                    inSel ? ConsoleColor.DarkBlue : ConsoleColor.Black);
                Out(line[p..segEnd]);
                printed += segEnd - p;
                p = segEnd;
            }
            if (printed >= width) break;
        }
        if (printed < width)
        {
            if (selEol) // a selected line break — one space in the selection colour
            {
                SetColors(ConsoleColor.White, ConsoleColor.DarkBlue);
                Out(' ');
                printed++;
            }
            SetColors(ConsoleColor.Gray, ConsoleColor.Black);
            if (printed < width) WriteSpaces(width - printed);
        }
    }

    private void Draw()
    {
        BeginFrame();
        int w = Console.WindowWidth, h = Console.WindowHeight;
        if (w != _lastW || h != _lastH)
        {
            if (UseAnsiFrame) Frame.Append("\x1b[H\x1b[2J\x1b[3J");
            else Console.Clear();
            _lastW = w; _lastH = h;
        }

        if (w < 42 || h < 10)
        {
            WriteAt(0, 0, " Terminal too small (need at least 42x10).", ConsoleColor.White, ConsoleColor.DarkRed, w);
            EndFrame();
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
        EndFrame();
    }

    private void PlaceCursor(int w, int h)
    {
        if (_mode is UiMode.SearchInput or UiMode.PathInput)
        {
            string label = _mode == UiMode.SearchInput ? CurrentSearchPrompt() : CurrentPathPrompt();
            int typed = _mode == UiMode.SearchInput ? _searchQuery.Length : _pathInput.Length;
            int x = Math.Min(w - 1, label.Length + typed);
            MoveTo(Math.Max(0, x), _statusY);
            ShowCursor();
        }
        else if (_mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasFile)
        {
            int x = _editorTextX + (_editor.Cx - _editor.ScrollX);
            int y = _contentTop + (_editor.Cy - _editor.ScrollY);
            if (x >= 0 && x < w && y >= _contentTop && y < _contentTop + _mainContentH)
            {
                MoveTo(x, y);
                ShowCursor();
            }
        }
    }

    private string CurrentSearchPrompt() => _searchScope switch
    {
        SearchScope.CurrentFile => " Search file: ",
        SearchScope.SelectedFolder => " Search folder: ",
        _ => " Search all folders: ",
    };
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

        // Block-comment state at the window top, from editor checkpoints
        // (never a re-tokenization of everything above the window).
        bool inBlock = _editor.HasFile && _editor.BlockCommentStateBefore(_editor.ScrollY);
        string ext = _editor.Extension;

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
                var fg = sel ? ConsoleColor.White
                    : node.Ignored ? ConsoleColor.DarkGray // git-ignored entries stay in the background
                    : node.IsDir ? ConsoleColor.White : ConsoleColor.Gray;
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
                    1 => " Ctrl+F — search here; Ctrl+F+F — search all folders.",
                    3 => " ←/→ collapse and expand folders,",
                    4 => " Tab switches panels, Ctrl+Q quits.",
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
                    string lineText = _editor.LineText(lineNo);
                    Highlighter.TokenizeInto(_tokens, lineText, ext, ref inBlock);
                    int sFrom = -1, sTo = -1;
                    bool sEol = false;
                    if (hasSel && lineNo >= selSL && lineNo <= selEL)
                    {
                        sFrom = lineNo == selSL ? selSC : 0;
                        sTo = lineNo == selEL ? selEC : lineText.Length;
                        sEol = lineNo < selEL;
                    }
                    WriteHighlighted(_editorTextX, y, lineText, _tokens, _editor.ScrollX, editorW - gutter, sFrom, sTo, sEol);
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
        string targetName = _searchTargetPath == null
            ? ""
            : Path.GetFileName(_searchTargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (targetName.Length == 0) targetName = _searchTargetPath ?? "";
        string scopeLabel = _searchScope switch
        {
            SearchScope.CurrentFile => $"file {targetName}",
            SearchScope.SelectedFolder => $"folder {targetName}",
            _ => "all folders",
        };
        WriteAt(0, _resultsTop,
            $" Search results ({scopeLabel}) for \"{_searchQuery}\": {_hitIndex + (_hits.Count > 0 ? 1 : 0)}/{_hits.Count} · Enter opens · Ctrl+G next · Esc hides",
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
            string suffix = _pendingSearch is { } ps && !ps.Task.IsCompleted
                ? "   · searching…"
                : _searchQuery.ToString().Trim().Length >= 2
                    ? $"   · found: {_hits.Count}"
                    : "   · search starts at 2 characters";
            WriteAt(0, _statusY, CurrentSearchPrompt() + _searchQuery + suffix, ConsoleColor.White, ConsoleColor.DarkBlue, w);
            string scopeHelp = _searchScope switch
            {
                SearchScope.CurrentFile =>
                    " Searching this file live · Ctrl+F again widens to all folders · ↑↓ pick · Enter jumps · Esc to results",
                SearchScope.SelectedFolder =>
                    " Searching this folder live · Ctrl+F again widens to all folders · ↑↓ pick · Enter opens · Esc to results",
                _ =>
                    " Searching all folders live · Ctrl+F again narrows to the selection · ↑↓ pick · Enter opens · Esc to results",
            };
            WriteAt(0, helpY, scopeHelp, ConsoleColor.DarkGray, ConsoleColor.Black, w);
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
                ? " ↑↓ select · Enter opens · Ctrl+N new file · Ctrl+Shift+N folder · Ctrl+R+R rename · Ctrl+Del delete · Ctrl+F search here · Ctrl+F+F all folders · Ctrl+O add root"
                : " Esc to tree · Ctrl+F find in file · Ctrl+F+F find everywhere · Ctrl+R+R rename · mouse/Shift+arrows select · Option+←→ by words · Ctrl+C/X/V clipboard · Ctrl+S save · Ctrl+W/P tabs";
        if (_mode == UiMode.Normal && _resultsVisible)
            help += " · Ctrl+G — next hit";
        WriteAt(0, helpY, help, ConsoleColor.DarkGray, ConsoleColor.Black, w);
    }
}
