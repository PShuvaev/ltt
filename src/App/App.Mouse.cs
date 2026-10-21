/// <summary>Mouse handling: panels, the tab bar, the tree wheel, and click-to-copy on the title.</summary>
internal sealed partial class App
{
    /// <summary>Rows to move for one wheel notch: 3/5 of a row, the remainder accumulating in numerator.</summary>
    internal static int TreeWheelSteps(ref int numerator, bool down)
    {
        numerator += down ? 3 : -3;
        int steps = numerator / 5; // integer division toward zero — the remainder keeps its sign
        numerator -= steps * 5;
        return steps;
    }

    /// <summary>Wheel over the results panel: same 3/5 pace as the tree wheel.</summary>
    private void ScrollResultsByWheel(bool down)
    {
        int steps = TreeWheelSteps(ref _resultsWheelNumerator, down);
        if (steps != 0)
            _hitIndex = Math.Clamp(_hitIndex + steps, 0, Math.Max(0, _hits.Count - 1));
    }

    private void HandleMouse(MouseInput m)
    {
        // While typing a query, the live results are already on screen —
        // the wheel over them must scroll without leaving the input first.
        if (_mode == UiMode.SearchInput)
        {
            if (m.IsWheel && _resultsVisible && m.Y >= _resultsTop)
                ScrollResultsByWheel(m.WheelDown);
            return;
        }
        if (_mode is UiMode.Confirm or UiMode.PathInput) return;

        // The wheel scrolls the panel under the cursor: tree, code or results.
        // Sideways (a swipe, a tilt wheel or Shift+wheel) only the code scrolls.
        if (m.IsWheel)
        {
            bool overCode = m.X > _leftW && m.Y >= _contentTop && m.Y < _contentTop + _mainContentH && _editor.HasFile;
            if (m.Y == 1 && m.X >= _editorX && _tabs.Tabs.Count > 1)
            {
                SwitchTab(m.WheelDown ? 1 : -1); // a swipe too
            }
            else if (overCode && (m.IsHorizontalWheel || m.HasShift))
                _editor.WheelSideways(right: m.WheelDown);
            else if (m.IsHorizontalWheel)
                return; // a swipe elsewhere has nothing to scroll
            else if (_resultsVisible && m.Y >= _resultsTop)
                ScrollResultsByWheel(m.WheelDown);
            else if (m.X < _leftW && m.Y >= _treeTop && m.Y < _treeTop + _treeContentH)
            {
                int steps = TreeWheelSteps(ref _treeWheelNumerator, m.WheelDown);
                if (steps != 0) _tree.MoveBy(steps);
            }
            else if (overCode)
                _editor.WheelScroll(m.WheelDown);
            return;
        }

        // Tab bar: click activates, × or the middle button closes. The cells
        // remember their tab, so a tab closed earlier in the same burst of
        // input is simply no longer there.
        if (m.Pressed && m.Y == 1 && m.X >= _editorX)
        {
            foreach (var cell in _tabCells)
            {
                if (m.X < cell.X0 || m.X > cell.X1) continue;
                int index = _tabs.Tabs.IndexOf(cell.Tab);
                if (index < 0) return;
                if (m.IsMiddleClick || (m.IsLeftClick && m.X == cell.CloseX)) CloseTab(cell.Tab);
                else if (m.IsLeftClick)
                {
                    ActivateTab(index, announce: true);
                    _mode = UiMode.Normal;
                    _focus = Focus.Editor;
                }
                return;
            }
            return;
        }

        // Releasing the button ends a drag selection.
        if (!m.Pressed)
        {
            if (_dragging)
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
            if (_dragging && m.IsLeftDrag && _editor.HasFile)
            {
                int line = _editor.ScrollY + Math.Clamp(m.Y - _contentTop, 0, Math.Max(0, _mainContentH - 1));
                _editor.SetCursorAtCell(line, _editor.ScrollX + Math.Max(0, m.X - _editorTextX));
            }
            return;
        }

        // Right button on a tree root — remove the folder from the panel (files stay on disk).
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

        // A click on the top line copies the path shown there.
        if (m.Y == 0)
        {
            if (_titlePath == null) return;
            Clipboard.Copy(_titlePath);
            _status = $"Copied path: {_titlePath}";
            return;
        }

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
            // Sizes counted in the background can re-sort the rows between two
            // clicks: a double-click counts only when both hit the same entry.
            var clicked = _tree.Visible[idx];
            bool sameEntry = isDouble && ReferenceEquals(clicked, _lastClickedNode);
            _lastClickedNode = clicked;
            _mode = UiMode.Normal;
            _focus = Focus.Tree;
            _tree.SelectIndex(idx);
            if (sameEntry && !_tree.ToggleSelected() && _tree.Selected is { IsDir: false } file)
                TryOpen(file.FullPath, 0, activateOnly: true);
        }
        // Click on the editor: cursor to the point and selection start; double-click — the whole word.
        else if (m.X > _leftW && _editor.HasFile)
        {
            if (m.Y < _contentTop || m.Y >= _contentTop + _mainContentH) return;
            _mode = UiMode.Normal;
            _focus = Focus.Editor;
            _editor.SetCursorAtCell(_editor.ScrollY + (m.Y - _contentTop),
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
