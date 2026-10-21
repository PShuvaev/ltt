/// <summary>One editor tab: an open file.</summary>
internal sealed class EditorTab
{
    public required TextEditor Editor { get; init; }
    public string Name => Editor.DisplayName;
}

/// <summary>The open tabs, in opening order, and which one is active.</summary>
internal sealed class TabManager
{
    public List<EditorTab> Tabs { get; } = new();
    public int ActiveIndex { get; private set; } = -1;

    public EditorTab? Active => ActiveIndex >= 0 && ActiveIndex < Tabs.Count ? Tabs[ActiveIndex] : null;
    public bool AnyDirty => Tabs.Any(t => t.Editor.Dirty);
    public int DirtyCount => Tabs.Count(t => t.Editor.Dirty);

    public int IndexOfPath(string fullPath) => Tabs.FindIndex(t => Paths.Same(t.Editor.FilePath, fullPath));

    /// <summary>Add a tab at the end and make it active.</summary>
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

    /// <summary>
    /// Close a tab (unsaved edits are the caller's concern). Closing the active
    /// tab activates the one that took its place — the right neighbour, or the
    /// left one when it was the last.
    /// </summary>
    public void Close(EditorTab tab)
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
