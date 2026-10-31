/// <summary>The result of a folder-wide replacement: files written now, open buffers left unsaved, and files skipped.</summary>
internal sealed record ReplaceReport(int FilesWritten, int MatchesWritten, int FilesUnsaved, int MatchesUnsaved,
    List<string> Skipped);

/// <summary>Apply a reviewed literal search to current files, never saving somebody's unrelated unsaved edits.</summary>
internal static class ProjectReplace
{
    public static ReplaceReport Apply(IEnumerable<SearchHit> hits, string query, string replacement,
        IReadOnlyList<TextEditor> openTabs)
    {
        int files = 0, matches = 0, unsavedFiles = 0, unsavedMatches = 0;
        var skipped = new List<string>();
        foreach (var group in hits.GroupBy(h => h.FilePath, Paths.Comparer))
        {
            string path = group.Key;
            TextEditor? ed = openTabs.FirstOrDefault(t => t.FilePath is { } p && Paths.SameReal(p, path));
            bool owns = ed == null, wasDirty = ed?.Dirty ?? false;
            int replaced = 0;
            try
            {
                if (ed == null)
                {
                    ed = new TextEditor();
                    ed.Open(path);
                }
                if (group.Any(h => h.LineNumber <= 0 || h.LineNumber > ed.Lines.Count
                                   || ed.LineText(h.LineNumber - 1) != h.LineText)
                    || !wasDirty && ed.CheckDisk() != TextEditor.DiskState.Same)
                {
                    skipped.Add(path + ": changed since the search");
                    continue;
                }
                replaced = ed.ReplaceAll(query, replacement);
                if (replaced == 0) continue;
                if (wasDirty)
                {
                    unsavedFiles++;
                    unsavedMatches += replaced;
                    continue;
                }
                if (ed.CheckDisk() != TextEditor.DiskState.Same)
                    throw new IOException("changed while the replacement was being prepared");
                ed.Save();
                files++;
                matches += replaced;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                           or ArgumentException or NotSupportedException)
            {
                if (!owns && ed is { Dirty: true } && !wasDirty)
                {
                    unsavedFiles++;
                    unsavedMatches += replaced;
                }
                skipped.Add(path + ": " + e.Message);
            }
        }
        return new ReplaceReport(files, matches, unsavedFiles, unsavedMatches, skipped);
    }
}
