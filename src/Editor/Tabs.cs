/// <summary>One editor tab: an open file and its pinned flag.</summary>
internal sealed class EditorTab
{
    public required TextEditor Editor { get; init; }
    public bool Pinned { get; set; }
    public string Name => Editor.DisplayName;
}

internal enum CloseResult { Closed, IsPinned, IsDirty }

/// <summary>
/// List of tabs with pinned ones first. A pinned tab cannot be closed
/// by the usual close command — it has to be unpinned first.
/// </summary>
internal sealed class TabManager
{
    public List<EditorTab> Tabs { get; } = new();
    public int ActiveIndex { get; private set; } = -1;

    public EditorTab? Active => ActiveIndex >= 0 && ActiveIndex < Tabs.Count ? Tabs[ActiveIndex] : null;
    public bool AnyDirty => Tabs.Any(t => t.Editor.Dirty);
    public int DirtyCount => Tabs.Count(t => t.Editor.Dirty);

    public int IndexOfPath(string fullPath) =>
        Tabs.FindIndex(t => string.Equals(t.Editor.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add a tab at the end (pinned stay on the left) and make it active.</summary>
    public EditorTab Add(TextEditor editor)
    {
        var tab = new EditorTab { Editor = editor };
        Tabs.Add(tab);
        ActiveIndex = Tabs.Count - 1;
        return tab;
    }

    public void Activate(int index)
    {
        if (index >= 0 && index < Tabs.Count) ActiveIndex = index;
    }

    public void ActivateRelative(int delta)
    {
        if (Tabs.Count == 0) return;
        ActiveIndex = ((ActiveIndex + delta) % Tabs.Count + Tabs.Count) % Tabs.Count;
    }

    /// <summary>Pin/unpin; pinned tabs are always on the left, order within groups is preserved.</summary>
    public void TogglePin(EditorTab tab)
    {
        tab.Pinned = !tab.Pinned;
        var active = Active;
        var sorted = Tabs.OrderByDescending(t => t.Pinned).ToList(); // OrderBy is stable
        Tabs.Clear();
        Tabs.AddRange(sorted);
        ActiveIndex = active == null ? -1 : Tabs.IndexOf(active);
    }

    /// <summary>Close attempt: pinned and dirty tabs are left alone without confirmation.</summary>
    public CloseResult RequestClose(EditorTab tab)
    {
        if (tab.Pinned) return CloseResult.IsPinned;
        if (tab.Editor.Dirty) return CloseResult.IsDirty;
        ForceClose(tab);
        return CloseResult.Closed;
    }

    public void ForceClose(EditorTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        bool wasActive = idx == ActiveIndex;
        Tabs.RemoveAt(idx);
        if (Tabs.Count == 0) ActiveIndex = -1;
        else if (wasActive) ActiveIndex = Math.Min(idx, Tabs.Count - 1);
        else if (idx < ActiveIndex) ActiveIndex--;
    }
}
