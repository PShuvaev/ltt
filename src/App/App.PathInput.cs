/// <summary>Path prompts (Ctrl+O / Ctrl+N / Ctrl+R / Ctrl+L): input state, live completion, root add/remove, go to.</summary>
internal sealed partial class App
{
    private string CurrentPathPrompt() => _pathPurpose switch
    {
        PathPurpose.CreateFile => " New file: ",
        PathPurpose.CreateFolder => " New folder: ",
        PathPurpose.Rename => " Rename: ",
        PathPurpose.GoTo => " Go to: ",
        _ => PathPrompt,
    };

    private void OpenPathInput(PathPurpose purpose, string initial = "")
    {
        _pathPurpose = purpose;
        _pathInput.Clear();
        // Relative paths start from the selected folder (or the open file's) — never
        // from the process's working directory, which may be a temporary install folder.
        _pathBaseDir = TargetDirectory();
        _pathInput.Append(initial.Length > 0 || purpose is not (PathPurpose.CreateFile or PathPurpose.CreateFolder)
            ? initial
            : Tilde(_pathBaseDir) + Path.DirectorySeparatorChar);
        _mode = UiMode.PathInput;
        if (purpose == PathPurpose.GoTo) _screen.Invalidate(); // Ctrl+L also repaints a garbled screen
        UpdatePathCompletions();
    }

    private void UpdatePathCompletions()
    {
        string input = _pathInput.ToString();
        // Rename takes a bare name; Go to takes a line number or a path (complete only the latter).
        if (_pathPurpose == PathPurpose.Rename || (_pathPurpose == PathPurpose.GoTo && input.All(char.IsDigit)))
        {
            _pathCandidates.Clear();
            _pathGhost = "";
            _pathCandidateIndex = -1;
            return;
        }
        var (candidates, ghost) = PathCompletion.Compute(input, _pathBaseDir);
        _pathCandidates = candidates;
        _pathCandidateIndex = -1;
        _pathGhost = ghost;
    }

    private void SetPathInput(string value, bool keepCandidates = false)
    {
        _pathInput.Clear();
        _pathInput.Append(value);
        if (keepCandidates) _pathGhost = "";
        else UpdatePathCompletions();
    }

    /// <summary>Tab: complete to the common prefix, then cycle through the candidates.</summary>
    private void CompletePath()
    {
        if (_pathCandidates.Count == 0) return;
        if (_pathCandidates.Count == 1)
        {
            SetPathInput(_pathCandidates[0]);
            return;
        }
        string raw = _pathInput.ToString();
        string lcp = PathCompletion.CommonPrefix(_pathCandidates);
        if (_pathCandidateIndex < 0 && lcp.Length > raw.Length)
        {
            SetPathInput(lcp);
            return;
        }
        CycleCandidate(+1);
    }

    private void CycleCandidate(int delta)
    {
        if (_pathCandidates.Count == 0) return;
        _pathCandidateIndex = ((_pathCandidateIndex + delta) % _pathCandidates.Count + _pathCandidates.Count)
                              % _pathCandidates.Count;
        SetPathInput(_pathCandidates[_pathCandidateIndex], keepCandidates: true);
    }

    /// <summary>
    /// "path", "path:line" or "path:line:column" (as compilers and agents print
    /// them, gcc and Go with a colon after); a path that exists as typed wins
    /// over a suffix reading. Lines and columns come back 0-based. null — nothing usable.
    /// </summary>
    internal static (string Path, int Line, int Column)? ParseLocation(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (File.Exists(text) || Directory.Exists(text)) return (text, 0, -1);
        if (text.Length > 1 && text[^1] == ':') text = text[..^1]; // "file:12:5: error…"
        var parts = text.Split(':');
        // Trailing numeric fields; a Windows drive "C:" stays part of the path.
        int numeric = 0;
        for (int i = parts.Length - 1; i > 0 && numeric < 2 && int.TryParse(parts[i], out _); i--) numeric++;
        if (numeric == 0) return (text, 0, -1);
        string path = string.Join(':', parts[..^numeric]);
        int line = Math.Max(1, int.Parse(parts[^numeric])) - 1;
        int col = numeric == 2 ? Math.Max(1, int.Parse(parts[^1])) - 1 : -1;
        return (path, line, col);
    }

    /// <summary>Ctrl+L: a line number jumps in the open file; a path (with :line) opens it there.</summary>
    private void GoToFromInput()
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.Normal;
        if (raw.Length == 0) { _status = "Go to cancelled."; return; }
        if (int.TryParse(raw, out int lineNo))
        {
            if (!_editor.HasFile) { _status = "Open a file first, or type a path."; return; }
            _focus = Focus.Editor;
            _editor.GoToLine(Math.Max(1, lineNo) - 1);
            _status = $"Line {_editor.Cy + 1} of {_editor.LineCount}.";
            return;
        }
        if (ParseLocation(raw) is not { } loc) return;
        string path = ResolveInputPath(loc.Path);
        if (!File.Exists(path))
        {
            // Agents print paths relative to the project: try each root.
            string? inRoot = _tree.RootPaths.Select(r => Path.GetFullPath(Path.Combine(r, PathCompletion.ExpandHome(loc.Path))))
                .FirstOrDefault(File.Exists);
            if (inRoot == null) { _status = $"File not found: {loc.Path}"; return; }
            path = inRoot;
        }
        path = _tree.InPanel(path);
        _tree.SelectPath(path);
        TryOpen(path, loc.Line, column: loc.Column);
    }

    private void AddRootFromInput()
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.Normal;
        _focus = Focus.Tree;
        if (raw.Length == 0) { _status = "Adding a folder cancelled."; return; }

        var loc = ParseLocation(raw) ?? (raw, 0, -1);
        string p = ResolveInputPath(loc.Path);
        if (File.Exists(p))
        {
            p = _tree.InPanel(p);
            if (!_tree.RootPaths.Any(r => Paths.IsInside(p, r))) AddRootUi(Path.GetDirectoryName(p)!);
            _tree.SelectPath(p);
            TryOpen(p, loc.Line, column: loc.Column);
            return;
        }
        p = ResolveInputPath(raw);
        if (!Directory.Exists(p))
        {
            _status = $"Folder not found: {p}";
            return;
        }
        AddRootUi(p);
    }

    private void AddRootUi(string dir)
    {
        bool added = _tree.AddRoot(dir);
        SyncWatcher();
        FilesChanged();
        _status = added
            ? $"Folder added to the left panel: {dir} ({_tree.Roots.Count} in total). Delete on a root removes it."
            : $"Folder already in the panel: {dir}";
    }

    private void RemoveRootUi(TreeNode root)
    {
        if (_tree.Roots.Count <= 1)
        {
            _status = "The last folder cannot be removed from the panel.";
            return;
        }
        if (_tree.RemoveRoot(root))
        {
            if (_pendingSizing is { } p && Paths.IsSameOrInside(p.Parent.FullPath, root.FullPath)) CancelSizing();
            SyncWatcher();
            FilesChanged();
            _status = $"Folder removed from the panel: {root.FullPath} (files on disk untouched).";
        }
    }
}
