/// <summary>The open tabs — an editor each — in opening order, and which one is active.</summary>
internal sealed class TabManager
{
    public List<TextEditor> Tabs { get; } = new();
    public int ActiveIndex { get; private set; } = -1;

    public TextEditor? Active => ActiveIndex >= 0 && ActiveIndex < Tabs.Count ? Tabs[ActiveIndex] : null;
    public bool AnyDirty => Tabs.Any(t => t.Dirty);
    public int DirtyCount => Tabs.Count(t => t.Dirty);

    /// <summary>The tab of a file, however its path is spelled (a symlinked folder on the way); -1 for none.</summary>
    public int IndexOfPath(string fullPath)
    {
        int i = Tabs.FindIndex(t => Paths.Same(t.FilePath, fullPath));
        if (i >= 0 || Tabs.Count == 0) return i;
        string real = Paths.Real(fullPath);
        return Tabs.FindIndex(t => t.FilePath is { } p && Paths.Same(Paths.Real(p), real));
    }

    /// <summary>Add a tab at the end and make it active.</summary>
    public TextEditor Add(TextEditor editor)
    {
        Tabs.Add(editor);
        ActiveIndex = Tabs.Count - 1;
        return editor;
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
    public void Close(TextEditor tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        tab.Colours?.Close();
        bool wasActive = idx == ActiveIndex;
        Tabs.RemoveAt(idx);
        if (Tabs.Count == 0) ActiveIndex = -1;
        else if (wasActive) ActiveIndex = Math.Min(idx, Tabs.Count - 1);
        else if (idx < ActiveIndex) ActiveIndex--;
    }
}
