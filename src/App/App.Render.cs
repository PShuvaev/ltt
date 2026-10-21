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
        string num = r == Math.Floor(r)
            ? r.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (num == "1000" && u < 5) { num = "1"; u++; } // 999 B rounds to 1K, 1000K to 1M
        string core = u < 0 ? num : num + "KMGTPE"[u];
        return exact ? core : "≥" + core;
    }

    private const int MaxHighlightedLine = 20_000;

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

    /// <summary>
    /// An editor line drawn from display column scrollX in `width` cells, with
    /// syntax colours and the selection [selFrom, selTo) (indexes into the line)
    /// white on dark blue; selEol — the selected line break shows as one cell.
    /// Text cut off at an edge is flagged in that edge cell with a dim ‹ or ›.
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
                sel ? ConsoleColor.White : Highlighter.ColorFor(tok.Kind),
                sel ? ConsoleColor.DarkBlue : ConsoleColor.Black,
                end - sx, out int consumed, tabOrigin: col, pad: false);
            sx += used;
            col += used;
            lastSel = sel;
            if (consumed < runEnd - i) { cut = true; break; } // the edge cut this run
            i = runEnd;
            if (i >= tok.Start + tok.Length) t++;
        }
        if (sx < end && selEol && i >= line.Length) // a selected line break — one cell in the selection colour
            sx += screen.Put(sx, y, " ", ConsoleColor.White, ConsoleColor.DarkBlue, 1);
        if (sx < end) screen.Put(sx, y, "", ConsoleColor.Gray, ConsoleColor.Black, end - sx);

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
        _lastW = w;
        _lastH = h;
        _screen.Begin(w, h);

        if (w < 42 || h < 10)
        {
            WriteAt(0, 0, " Terminal too small (need at least 42x10).", ConsoleColor.White, ConsoleColor.DarkRed, w);
            _screen.Flush();
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

        string activeLabel; // see BuildTitle
        bool showDirty = false;
        string activeTabLabel = "";
        if (_mode == UiMode.SearchResults && _hits.Count > 0)
        {
            activeLabel = _hits[Math.Clamp(_hitIndex, 0, _hits.Count - 1)].FilePath;
            _titlePath = activeLabel;
        }
        else if (_focus == Focus.Tree)
        {
            activeLabel = _tree.Selected?.FullPath ?? _tree.Roots[0].FullPath;
            _titlePath = activeLabel;
        }
        else if (_editor.HasFile)
        {
            activeLabel = _editor.FilePath!;
            _titlePath = activeLabel;
            showDirty = _editor.Dirty;
            activeTabLabel = _tabs.Tabs.Count > 1 ? $" [tab {_tabs.ActiveIndex + 1}/{_tabs.Tabs.Count}]" : "";
        }
        else
        {
            activeLabel = "no file open";
            _titlePath = null;
        }
        WriteAt(0, 0, BuildTitle(activeLabel, showDirty, activeTabLabel, w), ConsoleColor.White, ConsoleColor.DarkBlue, w);

        DrawPanels(w, contentH);
        if (_resultsVisible) DrawResultsPanel(w);

        DrawStatusAndHelp(w, helpY);
        PlaceCursor(w);
        _screen.Flush();
    }

    private void PlaceCursor(int w)
    {
        if (_mode is UiMode.SearchInput or UiMode.PathInput)
        {
            _screen.Cursor = (Math.Min(w - 1, _promptCursorX), _statusY);
        }
        else if (_mode == UiMode.Normal && _focus == Focus.Editor && _editor.HasFile)
        {
            int x = _editorTextX + (_editor.CursorColumn - _editor.ScrollX);
            int y = _contentTop + (_editor.Cy - _editor.ScrollY);
            // Sideways scrolling can leave the cursor out of view on either side.
            if (x >= _editorTextX && x < _editorTextX + _editor.ViewWidth
                && y >= _contentTop && y < _contentTop + _mainContentH)
                _screen.Cursor = (x, y);
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

        _treeTop = 1;
        _treeContentH = contentH + 1;
        _tree.ViewHeight = _treeContentH;
        _tree.EnsureVisible();
        int gutter = Math.Max(4, _editor.LineCount.ToString().Length + 1);
        _gutter = gutter;
        int viewW = Math.Max(1, editorW - gutter);
        // Pull the view to the cursor only when this tab's view size changed, else
        // the wheel cannot scroll away from the cursor. The size is per tab, so a
        // tab that was hidden while the layout changed catches up when shown.
        if (_editor.HasFile && (_editor.ViewWidth != viewW || _editor.ViewHeight != contentH))
        {
            _editor.ViewWidth = viewW;
            _editor.ViewHeight = contentH;
            _editor.EnsureCursorVisible();
        }
        _editor.ViewWidth = viewW;
        _editor.ViewHeight = contentH;
        _editorTextX = editorX + gutter;

        DrawTabBar(editorX, editorW);

        bool hasSel = _editor.HasSelection;
        var (selSL, selSC, selEL, selEC) = hasSel ? _editor.SelectionRange() : (-1, 0, -1, 0);
        var syntax = _editor.Syntax;

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
                if (leftW >= 20)
                {
                    string size = node.IsRoot ? "" : FormatSize(node.Size, node.SizeExact);
                    WriteAt(0, y, text, fg, bg, leftW - sizeColW);
                    WriteAt(leftW - sizeColW, y, size.PadLeft(sizeColW),
                        sel ? ConsoleColor.Gray : ConsoleColor.DarkGray, bg, sizeColW);
                }
                else WriteAt(0, y, text, fg, bg, leftW);
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
                    // The gutter's last cell marks a change against the last commit (or the file as opened).
                    byte mark = _editor.ChangeMark(lineNo);
                    if (mark != 0)
                        WriteAt(editorX + gutter - 1, y,
                            (mark & (LineDiff.Added | LineDiff.Modified)) != 0 ? "▎" : (mark & LineDiff.RemovedBelow) != 0 ? "▁" : "▔",
                            (mark & LineDiff.Added) != 0 ? ConsoleColor.Green : (mark & LineDiff.Modified) != 0 ? ConsoleColor.Yellow : ConsoleColor.Red,
                            ConsoleColor.Black, 1);
                    string lineText = _editor.LineText(lineNo);
                    // Only the visible part is tokenized; the comment state comes from the editor's cache.
                    // A very long line (minified code, a log) is drawn without colours.
                    bool inBlock = _editor.BlockCommentStateBefore(lineNo);
                    if (lineText.Length > MaxHighlightedLine)
                    {
                        _tokens.Clear();
                        _tokens.Add(new Token(0, lineText.Length, TokenKind.Normal));
                    }
                    else
                        Highlighter.TokenizeInto(_tokens, lineText, syntax, ref inBlock,
                            Cells.IndexAt(lineText, _editor.ScrollX + viewW) + 1);
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
    /// The tab label: ● — unsaved edits, ⚠ — changed on disk under unsaved
    /// edits, ✗ — gone from disk, × — close. Tabs whose files share a name
    /// also show the parent folder.
    /// </summary>
    private static string TabLabel(EditorTab t, bool ambiguous)
    {
        var ed = t.Editor;
        string name = t.Name;
        if (ambiguous && ed.FilePath != null)
            name += " · " + Path.GetFileName(Path.GetDirectoryName(ed.FilePath) ?? "");
        return " " + name + (ed.Dirty ? " ●" : "") + (ed.MissingOnDisk ? " ✗" : ed.ChangedOnDisk ? " ⚠" : "") + " × ";
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
        WriteAt(editorX, 1, "", ConsoleColor.Gray, ConsoleColor.Black, editorW);

        var nameCounts = _tabs.Tabs.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Count());
        var labels = _tabs.Tabs.Select(t => TabLabel(t, nameCounts[t.Name] > 1)).ToList();
        var widths = labels.Select(l => Cells.WidthOf(l)).ToList();

        int start = _tabs.ActiveIndex;
        int used = Math.Min(widths[start], editorW);
        while (start > 0 && used + widths[start - 1] <= editorW)
        {
            start--;
            used += widths[start];
        }

        int x = editorX;
        int lastDrawn = start - 1;
        for (int i = start; i < labels.Count; i++)
        {
            int remaining = editorX + editorW - x;
            if (remaining <= 0) break;
            int cellW = Math.Min(widths[i], remaining);
            bool active = i == _tabs.ActiveIndex;
            var bg = active
                ? (edFocused ? ConsoleColor.DarkCyan : ConsoleColor.DarkGray)
                : ConsoleColor.Black;
            var fg = active ? ConsoleColor.White : ConsoleColor.Gray;
            WriteAt(x, 1, labels[i], fg, bg, cellW);
            int closeAt = labels[i].LastIndexOf('×');
            int closeRel = closeAt >= 0 ? Cells.ColumnOf(labels[i], closeAt) : -1;
            _tabCells.Add((_tabs.Tabs[i], x, x + cellW - 1, closeRel >= 0 && closeRel < cellW ? x + closeRel : -1));
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
        string count = $"{_hitIndex + (_hits.Count > 0 ? 1 : 0)}/{_hits.Count}{(_hitsTruncated ? "+" : "")}"
                       + (_hitsSkipped > 0 ? $" ({_hitsSkipped} files over 8 MB skipped)" : "");
        WriteAt(0, _resultsTop,
            $" Search results ({scopeLabel}) for \"{_searchQuery}\": {count} · Enter opens · Ctrl+G next · Esc hides",
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

    /// <summary>The input line of a prompt; returns where the cursor goes. The text scrolls to keep its end in view.</summary>
    private int DrawPrompt(int w, string label, string input, string ghost, string suffix)
    {
        int labelW = Cells.WidthOf(label);
        int room = Math.Max(1, w - labelW - 1 - Cells.WidthOf(suffix)); // one cell for the cursor
        string shown = FitTail(input, room);
        int x = _screen.Put(0, _statusY, label + shown, ConsoleColor.White, ConsoleColor.DarkBlue, w, out _, pad: false);
        int cursorX = x;
        if (ghost.Length > 0 && x < w)
            x += _screen.Put(x, _statusY, ghost, ConsoleColor.DarkGray, ConsoleColor.DarkBlue, w - x, out _, pad: false);
        if (suffix.Length > 0 && x < w)
            x += _screen.Put(x, _statusY, suffix, ConsoleColor.White, ConsoleColor.DarkBlue, w - x, out _, pad: false);
        if (x < w) WriteAt(x, _statusY, "", ConsoleColor.White, ConsoleColor.DarkBlue, w - x);
        return cursorX;
    }

    private void DrawStatusAndHelp(int w, int helpY)
    {
        if (_mode == UiMode.PathInput)
        {
            // Typed text in white, the ghost suggestion trailing in grey.
            _promptCursorX = DrawPrompt(w, CurrentPathPrompt(), _pathInput.ToString(), _pathGhost, "");

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
                    hx += _screen.Put(hx, helpY, text, fg, bg, w - hx, out _, pad: false);
                }
                Seg(" Tab ▸ ", ConsoleColor.DarkGray, ConsoleColor.Black);
                for (int i = 0; i < _pathCandidates.Count && hx < w; i++)
                {
                    string cand = _pathCandidates[i];
                    string name = Path.GetFileName(cand.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (cand.EndsWith(Path.DirectorySeparatorChar) || cand.EndsWith(Path.AltDirectorySeparatorChar))
                        name += "/";
                    bool sel = i == _pathCandidateIndex;
                    Seg(name, sel ? ConsoleColor.White : ConsoleColor.Gray,
                        sel ? ConsoleColor.DarkBlue : ConsoleColor.Black);
                    Seg("  ", ConsoleColor.Gray, ConsoleColor.Black);
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
                    PathPurpose.GoTo => " Enter jumps: 120 — a line, file.cs:120 — a file at a line · Esc cancels",
                    _ => " Enter adds the folder to the left panel · Tab/↑↓ completion · Esc cancels · a file path (file:line too) opens the file",
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
                    ? $"   · found: {_hits.Count}{(_hitsTruncated ? "+" : "")}"
                    : "   · search starts at 2 characters";
            _promptCursorX = DrawPrompt(w, CurrentSearchPrompt(), _searchQuery.ToString(), "", suffix);
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
            WriteAt(0, _statusY, " " + _confirm?.Question, ConsoleColor.White, ConsoleColor.DarkRed, w);
            WriteAt(0, helpY, "", ConsoleColor.DarkGray, ConsoleColor.Black, w);
            return;
        }

        string modeLabel = _mode == UiMode.SearchResults ? "SEARCH" : _focus == Focus.Tree ? "TREE" : "EDITOR";
        string selInfo = _editor.HasSelection ? $" · selected: {_editor.SelectionCharCount()}" : "";
        string pos = _editor.HasFile ? $" Ln {_editor.Cy + 1}/{_editor.LineCount}, Col {_editor.CursorColumn + 1}{selInfo} " : "";
        int mainW = Math.Max(0, w - pos.Length);
        WriteAt(0, _statusY, $" [{modeLabel}] {_status}", ConsoleColor.Black, ConsoleColor.Gray, mainW);
        if (pos.Length > 0) WriteAt(mainW, _statusY, pos, ConsoleColor.Black, ConsoleColor.Gray, pos.Length);

        string help = _mode == UiMode.SearchResults
            ? " ↑↓ PgUp/PgDn or wheel to pick · Enter/double-click opens above · Esc hides the panel"
            : _focus == Focus.Tree
                ? " ↑↓ select · Enter opens · Ctrl+N new file · Ctrl+Shift+N folder · Ctrl+R rename · Ctrl+C/X/V copy/move · Ctrl+Del delete · Ctrl+F search here · Ctrl+F+F all folders · Ctrl+O add root · Ctrl+L go to"
                : " Esc to tree · Ctrl+F find in file · Ctrl+F+F find everywhere · Ctrl+L go to line · Ctrl+R rename · Shift+arrows select · Ctrl+A all · Ctrl+C/X/V clipboard · Ctrl+S save · Ctrl+W close tab";
        if (_paste is { } job) help = PasteProgress(job);
        else if (_picked is { } p && _focus == Focus.Tree && _mode == UiMode.Normal)
            help = $" Ctrl+V {(p.Cut ? "moves" : "copies")} {p.Label} here · Esc forgets it ·" + help;
        if (_mode == UiMode.Normal && _resultsVisible)
            help += " · Ctrl+G — next hit";
        WriteAt(0, helpY, help, ConsoleColor.DarkGray, ConsoleColor.Black, w);
    }
}
