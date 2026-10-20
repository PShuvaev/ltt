namespace ConsoleEditor;

/// <summary>Path prompts (Ctrl+O / Ctrl+N / F2): input state, live completion, root add/remove.</summary>
internal sealed partial class App
{
    private string CurrentPathPrompt() => _pathPurpose switch
    {
        PathPurpose.CreateFile => " New file: ",
        PathPurpose.CreateFolder => " New folder: ",
        PathPurpose.Rename => " Rename: ",
        _ => PathPrompt,
    };

    private void OpenPathInput(PathPurpose purpose, string initial = "")
    {
        _pathPurpose = purpose;
        _pathInput.Clear();
        if (purpose is PathPurpose.CreateFile or PathPurpose.CreateFolder)
        {
            _pathBaseDir = TargetDirectory();
            _pathInput.Append(initial.Length > 0
                ? initial
                : _pathBaseDir + Path.DirectorySeparatorChar);
        }
        else
        {
            _pathInput.Append(initial);
        }
        _mode = UiMode.PathInput;
        if (purpose == PathPurpose.Rename)
        {
            _pathCandidates.Clear();
            _pathGhost = "";
            _pathCandidateIndex = -1;
        }
        else
        {
            UpdatePathCompletions();
        }
    }

    private void UpdatePathCompletions()
    {
        if (_pathPurpose == PathPurpose.Rename)
        {
            _pathCandidates.Clear();
            _pathGhost = "";
            _pathCandidateIndex = -1;
            return;
        }
        string baseDir = _pathPurpose is PathPurpose.CreateFile or PathPurpose.CreateFolder
                         && _pathBaseDir.Length > 0
            ? _pathBaseDir
            : Directory.GetCurrentDirectory();
        var (candidates, ghost) = PathCompletion.Compute(_pathInput.ToString(), baseDir);
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

    private void AddRootFromInput()
    {
        string raw = _pathInput.ToString().Trim();
        _mode = UiMode.Normal;
        _focus = Focus.Tree;
        if (raw.Length == 0) { _status = "Adding a folder cancelled."; return; }

        string p = Path.GetFullPath(PathCompletion.ExpandHome(raw));

        if (File.Exists(p))
        {
            AddRootUi(Path.GetDirectoryName(p)!);
            TryOpen(p, 0);
            return;
        }
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
        ResetLiveSearch();
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
            SyncWatcher();
            ResetLiveSearch();
            _status = $"Folder removed from the panel: {root.FullPath} (files on disk untouched).";
        }
    }
}
