/// <summary>Rendering: title, panels, tab bar, results panel, status and help lines.</summary>
internal sealed partial class App
{
    private readonly Screen _screen = new();
    private readonly List<Token> _tokens = new(); // reusable token scratch for the visible lines

    private void WriteAt(int x, int y, string text, ConsoleColor fg, ConsoleColor bg, int width) =>
        _screen.Put(x, y, text, fg, bg, width);

    /// <summary>Compact size at two significant digits: `99`, `510`, `1.5K`, `980K`, `4.2G`;
    /// a leading `≥` marks a lower bound.</summary>
    internal static string FormatSize(long bytes, bool exact)
    {
        if (bytes == 0) return exact ? "0" : "…";
        double v = bytes;
        int u = -1; // -1 = bytes, then K M G T P E
        while (v >= 1024 && u < 5) { v /= 1024; u++; }
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);
        double r = Math.Round(v / mag) * mag; // two significant digits
        string num = r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); // a whole number has no point
        if (num == "1000" && u < 5) { num = "1"; u++; } // 999 B rounds to 1K, 1000K to 1M
        string core = u < 0 ? num : num + "KMGTPE"[u];
        return exact ? core : "≥" + core;
    }

    /// <summary>The end of s that fits in `cells` cells, led by … when something was cut.</summary>
    internal static string FitTail(string s, int cells)
    {
        if (cells <= 0) return "";
        if (Cells.WidthOf(s) <= cells) return s;
        int i = s.Length, used = 1; // the … takes one cell
        while (i > 0)
        {
            int p = Cells.PrevBoundary(s, i);
            int cw = Cells.WidthOf(s.AsSpan(p, i - p));
            if (used + cw > cells) break;
            used += cw;
            i = p;
        }
        return "…" + s[i..];
    }

    /// <summary>
    /// The title line: the path of the active element (the selected node in the
    /// tree, the open file in the editor, the current hit's file in search).
    /// A path too long for the line loses its beginning, never the file name.
    /// </summary>
    internal static string BuildTitle(string activeLabel, bool dirty, string tabLabel, int width)
    {
        activeLabel = Tilde(activeLabel);
        const string prefix = " ltt · ";
        string tail = (dirty ? " ●" : "") + tabLabel;
        int room = width - Cells.WidthOf(prefix) - Cells.WidthOf(tail);
        return prefix + FitTail(activeLabel, Math.Max(1, room)) + tail;
    }

    /// <summary>A path inside the home folder written from ~, as shells show it (not on Windows).</summary>
    internal static string Tilde(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !OperatingSystem.IsWindows() && home.Length > 1 && Paths.IsSameOrInside(path, home)
            ? "~" + path[Path.TrimEndingDirectorySeparator(home).Length..]
            : path;
    }

    private static void WriteEdgeMarker(Screen screen, int x, int y, char marker, bool selected) =>
        screen.Put(x, y, marker.ToString(), selected ? ConsoleColor.White : ConsoleColor.DarkGray,
            selected ? ConsoleColor.DarkBlue : ConsoleColor.Black, 1);

    private static ConsoleColor ColorFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => ConsoleColor.Cyan,
        TokenKind.Str => ConsoleColor.Yellow,
        TokenKind.Comment => ConsoleColor.DarkGreen,
        TokenKind.Number => ConsoleColor.Magenta,
        TokenKind.Preproc => ConsoleColor.DarkCyan,
        TokenKind.Added => ConsoleColor.Green, // the colours of the gutter's change marks
        TokenKind.Changed => ConsoleColor.Yellow,
        TokenKind.Removed => ConsoleColor.Red,
        _ => ConsoleColor.Gray,
    };

    /// <summary>
    /// An editor line drawn from display column scrollX in `width` cells, with
    /// syntax colours and the selection [selFrom, selTo) (indexes into the line)
    /// white on dark blue; selEol — the selected line break shows as one cell.
    /// Text cut off at an edge is flagged in that edge cell with a dim ‹ or ›.
    /// The cells after the text stay as a new frame has them (Screen.Begin).
    /// </summary>
    internal static void WriteHighlighted(Screen screen, int x, int y, string line, List<Token> tokens,
        int scrollX, int width, int selFrom = -1, int selTo = -1, bool selEol = false)
    {
        if (width <= 0) return;
        int end = x + width, sx = x;
        int i = Cells.IndexAt(line, scrollX), first = i;
        int col = Cells.ColumnOf(line, i);
        bool Selected(int at) => selFrom >= 0 && at >= selFrom && at < selTo;
        bool cut = false, lastSel = false;
        if (col < scrollX && i < line.Length) // a tab or wide character cut by the left edge
        {
            int w = Cells.WidthAt(line, i, col, out int len);
            int part = Math.Min(col + w - scrollX, width);
            bool sel = Selected(i);
            screen.Put(sx, y, "", ConsoleColor.Gray, sel ? ConsoleColor.DarkBlue : ConsoleColor.Black, part);
            sx += part;
            col += w;
            i += len;
        }
        int t = 0;
        while (t < tokens.Count && tokens[t].Start + tokens[t].Length <= i) t++;
        while (i < line.Length && sx < end && t < tokens.Count)
        {
            var tok = tokens[t];
            int runEnd = tok.Start + tok.Length;
            bool sel = Selected(i);
            if (selFrom >= 0) runEnd = sel ? Math.Min(runEnd, selTo) : i < selFrom ? Math.Min(runEnd, selFrom) : runEnd;
            if (runEnd <= i) { t++; continue; }
            int used = screen.Put(sx, y, line.AsSpan(i, runEnd - i),
                sel ? ConsoleColor.White : ColorFor(tok.Kind),
                sel ? ConsoleColor.DarkBlue : ConsoleColor.Black,
                end - sx, out int consumed, tabOrigin: col, pad: false, joinBefore: sx > x);
            sx += used;
            col += used;
            lastSel = sel;
            if (consumed < runEnd - i) { cut = true; break; } // the edge cut this run
            i = runEnd;
            if (i >= tok.Start + tok.Length) t++;
        }
        if (sx < end && selEol && i >= line.Length) // a selected line break — one cell in the selection colour
            sx += screen.Put(sx, y, " ", ConsoleColor.White, ConsoleColor.DarkBlue, 1);

        // A marker keeps the selection colours of the cell it covers; a line hidden
        // entirely to the left has its selected line break drawn in that cell.
        if (scrollX > 0 && line.Length > 0)
            WriteEdgeMarker(screen, x, y, '‹', first < line.Length ? Selected(first) : selEol);
        if (cut || (i < line.Length && sx >= end))
            WriteEdgeMarker(screen, x + width - 1, y, '›', lastSel);
    }

    private void Draw()
    {
        int w = Console.WindowWidth, h = Console.WindowHeight;
        _screen.Begin(w, h);

        if (w < 42 || h < 10)
        {
            WriteAt(0, 0, " Terminal too small (need at least 42x10).", ConsoleColor.White, ConsoleColor.DarkRed, w);
            _screen.Flush();
            return;
        }

        _contentTop = 2;
        _statusY = h - 2 - (_mode == UiMode.SearchInput && _replaceMode ? 1 : 0);
        int helpY = h - 1;

        // The bottom results panel takes ~30% of the height from the main panels, which keep 3 rows.
        int resultsTotal = _resultsVisible ? Math.Min(Math.Clamp(h * 30 / 100, 4, 10), Math.Max(0, _statusY - _contentTop - 3)) : 0;
        int contentH = _statusY - _contentTop - resultsTotal;
        _mainContentH = contentH;
        _resultsTop = _contentTop + contentH;
        _resultsRowCount = Math.Max(0, resultsTotal - 1);

        // The title (see BuildTitle): the picked hit's file, the tree's selection, or the open file with its ● and tab.
        bool onHit = _mode == UiMode.SearchResults && _hits.Count > 0;
        bool onFile = !onHit && _focus == Focus.Editor && _editor.HasFile;
        _titlePath = onHit ? _hits[Math.Clamp(_hitIndex, 0, _hits.Count - 1)].FilePath
            : _focus == Focus.Tree ? _tree.Selected?.FullPath ?? _tree.Roots[0].FullPath
            : _editor.FilePath; // null: no file open
        string tabNo = onFile && _tabs.Tabs.Count > 1 ? $" [tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}]" : "";
        WriteAt(0, 0, BuildTitle(_titlePath ?? "no file open", onFile && _editor.Dirty, tabNo, w),
            ConsoleColor.White, ConsoleColor.DarkBlue, w);

        DrawPanels(w, contentH);
        if (_resultsVisible) DrawResultsPanel(w);

        DrawStatusAndHelp(w, helpY);
        PlaceCursor();
        _screen.Flush();
    }

    /// <summary>The cursor at the editor's edit point; a prompt places its own (DrawPrompt).</summary>
    private void PlaceCursor()
    {
        if (_mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasFile)
        {
            int col = _editor.CursorColumn - _editor.ScrollX, viewW = _editor.ViewWidth;
            int y = _contentTop + (_editor.Cy - _editor.ScrollY);
            // Sideways scrolling (the wheel) can leave the cursor out of view on either side, or
            // its character under a ‹ › marker, which hides it: the cursor is not drawn then.
            bool underMarker = (col == 0 && _editor.ScrollX > 0)
                || (col + _editor.CursorCells - 1 >= viewW - 1
                    && Cells.WidthOf(_editor.LineText(_editor.Cy)) > _editor.ScrollX + viewW);
            if (col >= 0 && col < viewW && !underMarker && y >= _contentTop && y < _contentTop + _mainContentH)
                _screen.Cursor = (_editorTextX + col, y);
        }
    }

    /// <summary>What the search prompt, the results header and the help line say about the scope.</summary>
    private (string Prompt, string Header, string Help) ScopeTexts()
    {
        string target = _searchTargetPath == null
            ? ""
            : Path.GetFileName(_searchTargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (target.Length == 0) target = _searchTargetPath ?? "";
        string toggle = _exactSearch ? "Alt+E fuzzy" : "Alt+E exact";
        return _searchScope switch
        {
            SearchScope.CurrentFile => (" Search file: ", $"file {target}",
                $" Searching this file live · {toggle} · Ctrl+F again widens to all folders · ↑↓ pick · Enter jumps · Esc to results · Alt+R replace · Alt+D folder"),
            SearchScope.SelectedFolder => (" Search folder: ", $"folder {target}",
                $" Searching this folder live · {toggle} · Ctrl+F again widens to all folders · ↑↓ pick · Enter opens · Esc to results · Alt+R replace · Alt+D folder"),
            _ => (" Search all folders: ", "all folders",
                $" Searching all folders live · {toggle} · Ctrl+F again narrows to the selection · ↑↓ pick · Enter opens · Esc to results · Alt+R replace · Alt+D folder"),
        };
    }

    private void DrawPanels(int w, int contentH)
    {
        int leftW = _leftW = Math.Clamp(w * 32 / 100, 22, Math.Max(22, w - 24)); // the divider stands at leftW
        int editorX = _editorX = leftW + 1;
        int editorW = w - editorX;

        _treeTop = 1;
        _treeContentH = contentH + 1;
        _tree.ViewHeight = _treeContentH;
        _tree.EnsureVisible();
        int gutter = Math.Max(4, _editor.LineCount.ToString().Length + 1);
        int viewW = Math.Max(1, editorW - gutter);
        // Pull the view to the cursor only when this tab's view size changed, else
        // the wheel cannot scroll away from the cursor. The size is per tab, so a
        // tab that was hidden while the layout changed catches up when shown.
        bool resized = _editor.ViewWidth != viewW || _editor.ViewHeight != contentH;
        _editor.ViewWidth = viewW;
        _editor.ViewHeight = contentH;
        if (resized && _editor.HasFile) _editor.EnsureCursorVisible();
        _editorTextX = editorX + gutter;

        DrawTabBar(editorX, editorW);

        bool hasSel = _editor.HasSelection;
        var (selSL, selSC, selEL, selEC) = hasSel ? _editor.SelectionRange() : (-1, 0, -1, 0);
        var colours = _editor.HasFile ? _editor.Colours ?? _vim.Attach(_editor) : null;
        colours?.Prepare(_editor.ScrollY, contentH, _editor.ScrollX, viewW);

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
                    : _picked is { } picked && Paths.Same(picked.Path, node.FullPath) ? ConsoleColor.Magenta // Ctrl+C / Ctrl+X
                    : _changes.Of(node) switch
                    {
                        Changes.Kind.Added => ConsoleColor.Green,
                        Changes.Kind.Modified => ConsoleColor.Yellow,
                        _ => node.Ignored ? ConsoleColor.DarkGray // git-ignored entries stay in the background
                            : node.IsDir ? ConsoleColor.White : ConsoleColor.Gray,
                    };
                // Size hugs the right edge in a dim colour; roots have no size of their own.
                const int sizeColW = 8;
                string size = node.IsRoot ? "" : FormatSize(node.Size, node.SizeExact);
                WriteAt(0, y, text, fg, bg, leftW - sizeColW);
                WriteAt(leftW - sizeColW, y, size.PadLeft(sizeColW),
                    sel ? ConsoleColor.Gray : ConsoleColor.DarkGray, bg, sizeColW);
            }

            WriteAt(leftW, y, "│", ConsoleColor.DarkGray, ConsoleColor.Black, 1);

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
                    // The gutter's last cell marks a change against the last commit (or the file as opened).
                    byte mark = _editor.ChangeMark(lineNo);
                    if (mark != 0)
                        WriteAt(editorX + gutter - 1, y,
                            (mark & (LineDiff.Added | LineDiff.Modified)) != 0 ? "▎" : (mark & LineDiff.RemovedBelow) != 0 ? "▁" : "▔",
                            (mark & LineDiff.Added) != 0 ? ConsoleColor.Green : (mark & LineDiff.Modified) != 0 ? ConsoleColor.Yellow : ConsoleColor.Red,
                            ConsoleColor.Black, 1);
                    string lineText = _editor.LineText(lineNo);
                    // The colours come from vim (see SyntaxColours); a very long line (minified code, a log) has none.
                    if (lineText.Length > VimSyntax.MaxLine || colours == null || !colours.Fill(lineNo, lineText, _tokens))
                    {
                        _tokens.Clear();
                        _tokens.Add(new Token(0, lineText.Length, TokenKind.Normal));
                    }
                    int sFrom = -1, sTo = -1;
                    bool sEol = false;
                    if (hasSel && lineNo >= selSL && lineNo <= selEL)
                    {
                        sFrom = lineNo == selSL ? selSC : 0;
                        sTo = lineNo == selEL ? selEC : lineText.Length;
                        sEol = lineNo < selEL;
                    }
                    WriteHighlighted(_screen, _editorTextX, y, lineText, _tokens, _editor.ScrollX, viewW, sFrom, sTo, sEol);
                }
                else WriteAt(editorX, y, " ~", ConsoleColor.DarkGray, ConsoleColor.Black, editorW);
            }
        }
    }

    /// <summary>
    /// The tab label: ⚑ — pinned (no close cross), ● — unsaved edits, ⚠ — changed
    /// on disk under unsaved edits, ✗ — gone from disk, × — close. Tabs whose
    /// files share a name also show the parent folder.
    /// </summary>
    private static string TabLabel(TextEditor ed, bool ambiguous, bool pinned)
    {
        string name = ed.DisplayName;
        if (ambiguous && ed.FilePath != null)
            name += " · " + Path.GetFileName(Path.GetDirectoryName(ed.FilePath) ?? "");
        return " " + (pinned ? "⚑ " : "") + name + (ed.Dirty ? " ●" : "")
               + (ed.MissingOnDisk ? " ✗" : ed.ChangedOnDisk ? " ⚠" : "") + (pinned ? " " : " × ");
    }

    /// <summary>
    /// Tab bar above the editor. On overflow the window shifts so the
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

        var nameCounts = _tabs.Tabs.GroupBy(t => t.DisplayName).ToDictionary(g => g.Key, g => g.Count());
        var labels = _tabs.Tabs.Select(t => TabLabel(t, nameCounts[t.DisplayName] > 1, _tabs.IsPinned(t))).ToList();
        var widths = labels.Select(l => Cells.WidthOf(l)).ToList();

        int start = _tabs.ActiveIndex;
        int used = Math.Min(widths[start], editorW);
        while (start > 0 && used + widths[start - 1] <= editorW)
        {
            start--;
            used += widths[start];
        }

        int x = editorX;
        for (int i = start; i < labels.Count; i++)
        {
            int remaining = editorX + editorW - x;
            if (remaining <= 0) break;
            int cellW = Math.Min(widths[i], remaining);
            bool active = i == _tabs.ActiveIndex, pinned = _tabs.IsPinned(_tabs.Tabs[i]);
            var bg = active
                ? (edFocused ? ConsoleColor.DarkCyan : ConsoleColor.DarkGray)
                : ConsoleColor.Black;
            var fg = active ? ConsoleColor.White : pinned ? ConsoleColor.Cyan : ConsoleColor.Gray;
            WriteAt(x, 1, labels[i], fg, bg, cellW);
            int closeRel = widths[i] - 2; // an unpinned label ends with " × "
            _tabCells.Add((_tabs.Tabs[i], x, x + cellW - 1, !pinned && closeRel < cellW ? x + closeRel : -1));
            x += cellW;
        }
        if (start > 0) WriteAt(editorX, 1, "‹", ConsoleColor.Yellow, ConsoleColor.Black, 1);
        if (start + _tabCells.Count < labels.Count) // tabs past the right edge
            WriteAt(editorX + editorW - 1, 1, "›", ConsoleColor.Yellow, ConsoleColor.Black, 1);
    }

    /// <summary>Bottom panel with search results (below the tree and the editor).</summary>
    private void DrawResultsPanel(int w)
    {
        bool focused = _mode is UiMode.SearchResults or UiMode.SearchInput;
        string count = $"{_hitIndex + (_hits.Count > 0 ? 1 : 0)}/{_hits.Count}{(_hitsTruncated ? "+" : "")}"
                       + (_hitsSkipped > 0 ? $" ({_hitsSkipped} files over 8 MB skipped)" : "");
        string header = !_replaceMode
            ? $" {(_exactSearch ? "Exact search" : "Search")} results ({ScopeTexts().Header}) for \"{_searchQuery}\": {count} · Enter opens · Ctrl+G next · Esc hides"
            : $" Replace in {ScopeTexts().Header} for \"{_searchQuery}\": {count} · Alt+A all · Esc hides";
        WriteAt(0, _resultsTop, header, focused ? ConsoleColor.White : ConsoleColor.Magenta,
            focused ? ConsoleColor.DarkMagenta : ConsoleColor.Black, w);

        int rows = _resultsRowCount;
        if (_hitIndex < _hitScroll) _hitScroll = _hitIndex;
        if (rows > 0 && _hitIndex >= _hitScroll + rows) _hitScroll = _hitIndex - rows + 1;
        _hitScroll = Math.Clamp(_hitScroll, 0, Math.Max(0, _hits.Count - Math.Max(1, rows)));

        for (int r = 0; r < rows; r++)
        {
            int y = _resultsTop + 1 + r; // the rows below the header
            int idx = _hitScroll + r;
            if (idx >= _hits.Count) break; // the rows after the last hit stay blank
            var hit = _hits[idx];
            bool sel = idx == _hitIndex;
            var bg = sel ? ConsoleColor.DarkCyan : ConsoleColor.Black;
            string prefix = $" {hit.RelativePath}:{HitLine(hit) + 1} ";
            int prefixW = Math.Min(w, _screen.Put(0, y, prefix, sel ? ConsoleColor.White : ConsoleColor.Cyan, bg,
                w, out _, pad: false));
            int avail = Math.Max(1, w - prefixW);
            // The snippet starts a little before the match when the whole line does not fit.
            string snippet = hit.LineText.Trim();
            int lead = hit.LineText.Length - hit.LineText.TrimStart().Length;
            if (Cells.WidthOf(snippet) > avail)
            {
                int start = Math.Clamp(hit.MatchStart - lead - 12, 0, snippet.Length);
                start = Cells.FloorBoundary(snippet, start);
                if (start > 0) snippet = "…" + snippet[start..];
            }
            WriteAt(prefixW, y, snippet, sel ? ConsoleColor.White : ConsoleColor.Gray, bg, w - prefixW);
        }
    }

    /// <summary>Text at x in row y up to the line end w, not padded; x moves on past it.</summary>
    private void PutRun(ref int x, int y, string text, ConsoleColor fg, ConsoleColor bg, int w, bool joinBefore = false)
    {
        if (x < w) x += _screen.Put(x, y, text, fg, bg, w - x, out _, pad: false, joinBefore: joinBefore);
    }

    /// <summary>The input line of a prompt, the cursor right after the typed text. The text scrolls to keep its end in view.</summary>
    private void DrawPrompt(int w, string label, string input, string ghost, string suffix, int row = -1, bool active = true)
    {
        int y = row >= 0 ? row : _statusY;
        int room = Math.Max(1, w - Cells.WidthOf(label) - 1 - Cells.WidthOf(suffix)); // one cell for the cursor
        int x = 0;
        PutRun(ref x, y, label + FitTail(input, room), ConsoleColor.White, ConsoleColor.DarkBlue, w);
        if (active) _screen.Cursor = (Math.Min(w - 1, x), y);
        PutRun(ref x, y, ghost, ConsoleColor.DarkGray, ConsoleColor.DarkBlue, w, joinBefore: true); // the ghost goes on from the typed text
        PutRun(ref x, y, suffix, ConsoleColor.White, ConsoleColor.DarkBlue, w);
        if (x < w) WriteAt(x, y, "", ConsoleColor.White, ConsoleColor.DarkBlue, w - x);
    }

    private void DrawStatusAndHelp(int w, int helpY)
    {
        if (_mode == UiMode.PathInput)
        {
            // Typed text in white, the ghost suggestion trailing in grey.
            DrawPrompt(w, CurrentPathPrompt(), _pathInput.ToString(), _pathGhost, "");

            // The help line shows live completion candidates (the chosen one highlighted).
            if (_pathPurpose != PathPurpose.Rename && _pathCandidates.Count > 0)
            {
                WriteAt(0, helpY, "", ConsoleColor.DarkGray, ConsoleColor.Black, w);
                int hx = 0;
                PutRun(ref hx, helpY, " Tab ▸ ", ConsoleColor.DarkGray, ConsoleColor.Black, w);
                for (int i = 0; i < _pathCandidates.Count && hx < w; i++)
                {
                    string cand = _pathCandidates[i];
                    string name = Path.GetFileName(cand.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                  + (Path.EndsInDirectorySeparator(cand) ? "/" : "");
                    bool sel = i == _pathCandidateIndex;
                    PutRun(ref hx, helpY, name, sel ? ConsoleColor.White : ConsoleColor.Gray,
                        sel ? ConsoleColor.DarkBlue : ConsoleColor.Black, w);
                    PutRun(ref hx, helpY, "  ", ConsoleColor.Gray, ConsoleColor.Black, w);
                }
            }
            else WriteAt(0, helpY, _pathPurpose switch
            {
                PathPurpose.Rename => " Enter renames · Esc cancels · type just the new name (no path); open tabs update themselves",
                _ when _pathInput.Length > 0 => " No matches · Enter uses it as-is · Esc cancels · ~ and relative paths are supported",
                PathPurpose.CreateFile => " Enter creates the file and opens it · Tab/↑↓ completion · a trailing / creates a folder · Esc cancels",
                PathPurpose.CreateFolder => " Enter creates the folder · Tab/↑↓ completion · Esc cancels",
                PathPurpose.GoTo => " Enter jumps: 120 — a line, file.cs:120 — a file at a line · Esc cancels",
                PathPurpose.SearchFolder => " Enter searches this folder · Tab/↑↓ completion · Esc returns to search",
                _ => " Enter adds the folder to the left panel · Tab/↑↓ completion · Esc cancels · a file path (file:line too) opens the file",
            }, ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }
        if (_mode == UiMode.SearchInput)
        {
            string suffix = _pendingSearch != null // until its results are applied, not merely ready
                ? "   · searching…"
                : Searchable(_searchQuery.ToString())
                    ? $"   · found: {_hits.Count}{(_hitsTruncated ? "+" : "")}"
                    : ExactSearch ? "   · exact text" : "   · search starts at 2 characters";
            var scope = ScopeTexts();
            DrawPrompt(w, scope.Prompt, _searchQuery.ToString(), "", suffix, active: !_replaceField);
            string searchHelp = scope.Help;
            if (_replaceMode)
            {
                DrawPrompt(w, " Replace with: ", _replaceQuery.ToString(), "", "", row: _statusY + 1, active: _replaceField);
                searchHelp = _searchScope == SearchScope.CurrentFile
                    ? " Tab switches fields · Enter replaces one · Alt+A replaces all · Alt+D chooses folder · Alt+R closes replace · Esc goes back"
                    : " Tab switches fields · Enter opens · Alt+A replaces all in files · Alt+D chooses folder · Alt+R closes replace · Esc goes back";
            }
            WriteAt(0, helpY, searchHelp, ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }
        if (_mode == UiMode.Confirm)
        {
            WriteAt(0, _statusY, " " + _confirm?.Question, ConsoleColor.White, ConsoleColor.DarkRed, w);
            WriteAt(0, helpY, "", ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }

        string modeLabel = _mode == UiMode.SearchResults
            ? _replaceMode ? "REPLACE" : _exactSearch ? "EXACT" : "SEARCH"
            : _focus == Focus.Tree ? "TREE" : "EDITOR";
        string selInfo = _editor.HasSelection ? $" · selected: {_editor.SelectionCharCount()}" : "";
        string pos = _editor.HasFile ? $" Ln {_editor.Cy + 1}/{_editor.LineCount}, Col {_editor.CursorColumn + 1}{selInfo} " : "";
        int mainW = Math.Max(0, w - pos.Length);
        WriteAt(0, _statusY, $" [{modeLabel}] {_status}", ConsoleColor.Black, ConsoleColor.Gray, mainW);
        if (pos.Length > 0) WriteAt(mainW, _statusY, pos, ConsoleColor.Black, ConsoleColor.Gray, pos.Length);

        string help = _mode == UiMode.SearchResults
            ? _replaceMode
                ? " ↑↓ pick · Enter opens/replaces in file · Alt+A replaces all · Alt+D chooses folder · Alt+R closes replace · Esc hides"
                : $" ↑↓ PgUp/PgDn or wheel to pick · Enter/double-click opens above · Alt+E {(_exactSearch ? "fuzzy" : "exact")} · Esc hides the panel"
            : _focus == Focus.Tree
                ? " ↑↓ select · Enter opens · Ctrl+E next change · Ctrl+N new file · Ctrl+Shift+N folder · Ctrl+R rename · Ctrl+C/X/V copy/move · Ctrl+Del delete · Ctrl+F search here · Ctrl+F+F all folders · Alt+R replace · Ctrl+O add root · Ctrl+L go to"
                : " Esc to tree · Ctrl+E next change · Ctrl+F find · Alt+R replace · Ctrl+F+F all folders · Ctrl+L go to line · Ctrl+R rename · Shift+arrows select · Ctrl+A all · Ctrl+K delete line · Ctrl+C/X/V clipboard · Ctrl+S save · Ctrl+W close tab · Ctrl+P pin tab";
        if (_paste is { } job) help = PasteProgress(job);
        else if (_picked is { } p && _focus == Focus.Tree && _mode == UiMode.Normal)
            help = $" Ctrl+V {(p.Cut ? "moves" : "copies")} {p.Label} here · Esc forgets it ·" + help;
        if (_mode == UiMode.Normal && _resultsVisible)
            help += " · Ctrl+G — next hit";
        WriteAt(0, helpY, help, ConsoleColor.DarkGray, ConsoleColor.Black, w);
    }
}
