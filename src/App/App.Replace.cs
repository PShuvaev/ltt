using System.Text;

/// <summary>Literal replacement over the same file/folder/all-folders scopes as search.</summary>
internal sealed partial class App
{
    private readonly StringBuilder _replaceQuery = new();
    private bool _replaceMode, _replaceField, _pendingReplaceAll;

    private string FindQuery() => ExactSearch ? _searchQuery.ToString() : _searchQuery.ToString().Trim();

    private void ToggleReplace()
    {
        bool fresh = _mode == UiMode.Normal;
        if (fresh)
        {
            if (ResolveScopedSearchTarget() == null)
            {
                _status = "Open or select a file/folder first.";
                return;
            }
            OpenScopedSearch();
        }
        if (_mode is not (UiMode.SearchInput or UiMode.SearchResults)) return;
        _replaceMode = !_replaceMode;
        _replaceField = _replaceMode && !fresh;
        _mode = UiMode.SearchInput;
        ClearSearch();
        RunLiveSearch();
    }

    private void ReplaceCurrentHit()
    {
        if (_searchScope != SearchScope.CurrentFile || _hits.Count == 0) return;
        var hit = _hits[Math.Clamp(_hitIndex, 0, _hits.Count - 1)];
        string query = FindQuery(), replacement = _replaceQuery.ToString();
        if (!OpenHit(hit)) return;
        int line = _editor.Cy;
        if (!_editor.ReplaceAt(line, _editor.Cx, query, replacement))
        {
            RunLiveSearch();
            _status = "The buffer changed; matches were refreshed.";
        }
        else
        {
            int end = _editor.Cx;
            RunLiveSearch();
            int next = _hits.FindIndex(h => HitLine(h) > line || HitLine(h) == line && h.MatchStart >= end);
            _hitIndex = next >= 0 ? next : 0;
            _status = "Replaced one occurrence. Ctrl+Z undoes; Ctrl+S saves.";
        }
        _mode = UiMode.SearchInput;
        _replaceField = true;
    }

    private void ChooseSearchFolder()
    {
        string dir = _searchScope == SearchScope.SelectedFolder && _searchTargetPath != null
            ? _searchTargetPath
            : TargetDirectory();
        OpenPathInput(PathPurpose.SearchFolder, Tilde(dir) + Path.DirectorySeparatorChar);
    }

    private void SearchFolderFromInput()
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.SearchInput;
        if (raw.Length == 0) return;
        string path = ResolveInputPath(raw);
        if (!Directory.Exists(path)) { _status = $"Folder not found: {path}"; return; }
        path = _tree.InPanel(path);
        _tree.SelectPath(path);
        ApplySearchScope(SearchScope.SelectedFolder, path);
        RunLiveSearch();
    }

    private void StartReplaceAll()
    {
        string query = FindQuery();
        if (!Searchable(query)) { _status = "Type something to find first."; return; }
        CancelSearch();
        if (_searchScope == SearchScope.CurrentFile)
        {
            if (_searchTargetPath == null || !TryOpen(_searchTargetPath, 0, activateOnly: true)) return;
            var result = FuzzySearch.SearchLines(_editor.FilePath!, _editor.DisplayName, _editor.Lines, query,
                int.MaxValue - 1, literal: true);
            ReviewReplaceAll(result);
            return;
        }

        string? target = _searchTargetPath == null ? null : Path.GetFullPath(_searchTargetPath);
        var scope = _searchScope;
        var roots = _tree.RootPaths;
        var buffers = DirtyBuffers();
        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var task = Task.Run(() => scope == SearchScope.SelectedFolder
            ? FuzzySearch.Search(target!, query, int.MaxValue - 1, ct: ct, buffers: buffers, literal: true)
            : FuzzySearch.SearchMany(roots, query, int.MaxValue - 1, ct: ct, buffers: buffers, literal: true));
        _pendingReplaceAll = true;
        _pendingSearch = (task, cts, query, TabMarks());
    }

    private void ReviewReplaceAll(SearchResult result)
    {
        _pendingReplaceAll = false;
        if (result.Truncated) { _status = "Too many matches to replace safely."; _mode = UiMode.SearchInput; return; }
        if (result.Hits.Count == 0) { _status = "No matches to replace."; _mode = UiMode.SearchInput; return; }
        string query = FindQuery(), replacement = _replaceQuery.ToString();
        int files = result.Hits.Select(h => h.FilePath).Distinct(Paths.Comparer).Count();
        string skipped = result.SkippedLarge > 0 ? $"; {result.SkippedLarge} large files skipped" : "";
        string scope = ScopeTexts().Header;
        string question = _searchScope == SearchScope.CurrentFile
            ? $"Replace {result.Hits.Count} occurrences in {scope}? R replaces · Esc cancels"
            : $"Replace {result.Hits.Count} occurrences in {files} files in {scope}? R writes files; dirty tabs stay unsaved{skipped} · Esc cancels";
        Ask(question, ('r', () => ApplyReplaceAll(result.Hits, query, replacement)));
    }

    private void ApplyReplaceAll(List<SearchHit> hits, string query, string replacement)
    {
        if (_searchScope == SearchScope.CurrentFile)
        {
            if (_searchTargetPath == null || _tabs.IndexOfPath(_searchTargetPath) is not (>= 0 and var idx))
            {
                _status = "The file is no longer open.";
                return;
            }
            var ed = _tabs.Tabs[idx];
            if (hits.Any(h => h.LineNumber <= 0 || h.LineNumber > ed.Lines.Count
                              || ed.LineText(h.LineNumber - 1) != h.LineText))
            {
                _status = "The buffer changed; no replacements were made. Run the search again.";
                return;
            }
            int n = ed.ReplaceAll(query, replacement);
            _status = n == 0 ? "The replacement makes no textual change." : $"Replaced {n} occurrences. Ctrl+Z undoes; Ctrl+S saves.";
        }
        else
        {
            var report = ProjectReplace.Apply(hits, query, replacement, _tabs.Tabs);
            _status = $"Written: {report.MatchesWritten} replacements in {report.FilesWritten} files";
            if (report.FilesUnsaved > 0)
                _status += $"; unsaved tabs: {report.MatchesUnsaved} replacements in {report.FilesUnsaved}";
            if (report.Skipped.Count > 0) _status += $"; skipped: {report.Skipped.Count}";
        }
        _hitsStale = true;
        _mode = UiMode.Normal;
        _focus = Focus.Editor;
        RunLiveSearch();
    }
}
