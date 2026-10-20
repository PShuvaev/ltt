using System.Text;

namespace ConsoleEditor;

/// <summary>Mouse handling, the tree wheel, and title-path selection (auto-copied).</summary>
internal sealed partial class App
{
    /// <summary>Build the title text: one path — of the active element — and its range for selection.</summary>
    internal static (string Text, (int Start, int Len) Path) BuildTitle(
        string activeLabel, bool isPath, bool dirty, string tabLabel)
    {
        var sb = new StringBuilder(" ConsoleEditor · ");
        int pathStart = sb.Length;
        sb.Append(activeLabel);
        if (dirty) sb.Append(" ●");
        sb.Append(tabLabel);
        return (sb.ToString(), (pathStart, isPath ? activeLabel.Length : 0));
    }

    private bool HasTitleSelection => _titleSelFrom >= 0 && _titleSelTo > _titleSelFrom;

    private string TitleSelectedText()
    {
        if (!HasTitleSelection) return "";
        int from = Math.Clamp(_titleSelFrom, 0, _titleText.Length);
        int to = Math.Clamp(_titleSelTo, from, _titleText.Length);
        return _titleText.Substring(from, to - from);
    }

    /// <summary>Select the active element's whole path if column x falls inside it.</summary>
    private void SelectTitleSegmentAt(int x)
    {
        _titleSelFor = _titleText;
        if (_titlePathSeg.Len > 0 && x >= _titlePathSeg.Start && x < _titlePathSeg.Start + _titlePathSeg.Len)
        {
            _titleSelFrom = _titlePathSeg.Start;
            _titleSelTo = _titlePathSeg.Start + _titlePathSeg.Len;
        }
        else
        {
            _titleSelFrom = -1;
            _titleSelTo = -1;
        }
    }

    /// <summary>
    /// Whatever is selected in the title lands on the clipboard by itself —
    /// while dragging (quietly, announce: false) and finally on release.
    /// </summary>
    private void CopyTitleSelection(bool announce = true, bool useSystemHelper = true)
    {
        string text = TitleSelectedText();
        if (text.Length == 0)
        {
            if (announce)
                _status = "Click the path in the top line to copy it in full.";
            return;
        }
        Clipboard.Copy(text, useSystemHelper);
        if (announce) _status = $"Copied path: {text}";
    }

    /// <summary>
    /// Tree wheel step slowed 5× against the old 3 rows per notch:
    /// each notch adds 3/5 of a row, the remainder accumulates in numerator.
    /// Five notches down move the selection exactly 3 rows (was 15).
    /// </summary>
    internal static int TreeWheelSteps(ref int numerator, bool down)
    {
        numerator += down ? 3 : -3;
        int steps = numerator / 5; // integer division toward zero — the remainder keeps its sign
        numerator -= steps * 5;
        return steps;
    }

    private void HandleMouse(MouseInput m)
    {
        if (_mode is UiMode.SearchInput or UiMode.Confirm or UiMode.PathInput) return;
        if (m.IsLeftClick && m.Y != 0) { _titleSelFrom = -1; _titleSelTo = -1; }

        // The wheel scrolls the panel under the cursor: tree, code or results.
        if (m.IsWheel)
        {
            int delta = m.WheelDown ? 3 : -3;
            if (m.Y == 1 && m.X >= _editorX && _tabs.Tabs.Count > 1)
            {
                SwitchTab(m.WheelDown ? 1 : -1); // the wheel over the tab bar switches tabs
            }
            else if (_resultsVisible && m.Y >= _resultsTop)
                _hitIndex = Math.Clamp(_hitIndex + delta, 0, Math.Max(0, _hits.Count - 1));
            else if (m.X < _leftW && m.Y >= _treeTop && m.Y < _treeTop + _treeContentH)
            {
                // The tree scrolls 5× slower: 3/5 of a row per notch, remainder accumulated.
                int steps = TreeWheelSteps(ref _treeWheelNumerator, m.WheelDown);
                if (steps != 0) _tree.MoveBy(steps);
            }
            else if (m.X > _leftW && m.Y >= _contentTop && m.Y < _contentTop + _mainContentH && _editor.HasFile)
                _editor.WheelScroll(m.WheelDown); // 6/5 of a line per notch, instantly
            return;
        }

        // Tab bar: click activates, × or middle button closes, right button pins/unpins.
        if (m.Pressed && !m.IsWheel && m.Y == 1 && m.X >= _editorX)
        {
            foreach (var cell in _tabCells)
            {
                if (m.X < cell.X0 || m.X > cell.X1) continue;
                var tab = _tabs.Tabs[cell.Index];
                if (m.IsRightClick) TogglePin(tab);
                else if (m.IsMiddleClick || (m.IsLeftClick && m.X == cell.CloseX)) CloseTab(tab);
                else if (m.IsLeftClick)
                {
                    ActivateTab(cell.Index, announce: true);
                    _mode = UiMode.Normal;
                    _focus = Focus.Editor;
                }
                return;
            }
            return;
        }

        // Releasing the button ends a drag selection.
        if (!m.Pressed && !m.IsWheel)
        {
            if (_titleDragging)
            {
                _titleDragging = false;
                // A plain click without motion — select and copy the whole path.
                if (!_titleDragMoved) SelectTitleSegmentAt(_titleAnchor ?? m.X);
                CopyTitleSelection();
            }
            else if (_dragging)
            {
                _dragging = false;
                if (_editor.HasSelection)
                    _status = $"Selected: {_editor.SelectionCharCount()} chars. Ctrl+C copies, Ctrl+X cuts.";
                else
                    _editor.ClearSelection();
            }
            return;
        }

        // Motion with the left button held extends the editor selection.
        if (m.IsMotion)
        {
            if (_titleDragging && m.IsLeftDrag && _titleAnchor is int ta)
            {
                _titleDragMoved = true;
                _titleSelFor = _titleText;
                _titleSelFrom = Math.Min(ta, m.X);
                _titleSelTo = Math.Max(ta, m.X) + 1;
                // The clipboard mirrors the selection already while dragging.
                CopyTitleSelection(announce: false, useSystemHelper: false);
            }
            else if (_dragging && m.IsLeftDrag && _editor.HasFile)
            {
                int line = _editor.ScrollY + Math.Clamp(m.Y - _contentTop, 0, Math.Max(0, _mainContentH - 1));
                int col = _editor.ScrollX + Math.Max(0, m.X - _editorTextX);
                _editor.SetCursor(line, col);
            }
            return;
        }

        // Right button on a tree root — remove the folder from the panel.
        if (m.IsRightClick && m.Y >= _treeTop && m.Y < _treeTop + _treeContentH && m.X < _leftW)
        {
            int ridx = _tree.ScrollOffset + (m.Y - _treeTop);
            if (ridx < _tree.Visible.Count && _tree.Visible[ridx].IsRoot)
            {
                _focus = Focus.Tree;
                RemoveRootUi(_tree.Visible[ridx]);
            }
            return;
        }

        if (!m.IsLeftClick) return;

        bool isDouble = (DateTime.UtcNow - _lastClickAt).TotalMilliseconds < 450
                        && m.X == _lastClickX && m.Y == _lastClickY;
        _lastClickAt = isDouble ? DateTime.MinValue : DateTime.UtcNow;
        _lastClickX = m.X;
        _lastClickY = m.Y;

        // Click on the top line: start selecting the path; double-click — the whole path at once.
        if (m.Y == 0)
        {
            _titleDragging = true;
            _titleDragMoved = false;
            _titleAnchor = m.X;
            _titleSelFor = _titleText;
            if (isDouble)
            {
                SelectTitleSegmentAt(m.X);
                CopyTitleSelection(announce: false); // double-click: the path is already selected — copy at once
            }
            else { _titleSelFrom = m.X; _titleSelTo = m.X + 1; }
            return;
        }

        // Click on the bottom results panel
        if (_resultsVisible && m.Y == _resultsTop) { _mode = UiMode.SearchResults; return; }
        if (_resultsVisible && m.Y >= _resultsRowsTop && m.Y < _resultsRowsTop + _resultsRowCount)
        {
            int idx = _hitScroll + (m.Y - _resultsRowsTop);
            if (idx < _hits.Count)
            {
                _mode = UiMode.SearchResults;
                _hitIndex = idx;
                if (isDouble) OpenHit(_hits[idx]);
            }
            return;
        }

        // Click on the tree: select; double-click expands a folder / opens a file
        if (m.X < _leftW)
        {
            if (m.Y < _treeTop || m.Y >= _treeTop + _treeContentH) return;
            int idx = _tree.ScrollOffset + (m.Y - _treeTop);
            if (idx >= _tree.Visible.Count) return;
            _mode = UiMode.Normal;
            _focus = Focus.Tree;
            _tree.SelectIndex(idx);
            if (isDouble && !_tree.ToggleSelected() && _tree.Selected is { IsDir: false } file)
                TryOpen(file.FullPath, 0, activateOnly: true);
        }
        // Click on the editor: cursor to the point and selection start; double-click — the whole word.
        else if (m.X > _leftW && _editor.HasFile)
        {
            if (m.Y < _contentTop || m.Y >= _contentTop + _mainContentH) return;
            _mode = UiMode.Normal;
            _focus = Focus.Editor;
            _editor.SetCursor(
                _editor.ScrollY + (m.Y - _contentTop),
                _editor.ScrollX + Math.Max(0, m.X - _editorTextX));
            if (isDouble)
            {
                _editor.SelectWord();
                if (_editor.HasSelection)
                    _status = $"Word selected: {_editor.SelectionCharCount()} chars. Ctrl+C copies.";
            }
            else
            {
                _editor.SetAnchorHere();
            }
            _dragging = true;
        }
    }
}
