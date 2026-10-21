/// <summary>Moving files and folders on disk.</summary>
internal static class FileOps
{
    /// <summary>
    /// Move a file/folder. A case-only rename on a case-insensitive file system
    /// goes through a temporary name (a direct move would do nothing); if the
    /// second step fails, the first one is undone so nothing is left stranded.
    /// </summary>
    internal static void Move(string oldPath, string newPath, bool isDir)
    {
        bool caseOnly = Paths.Same(oldPath, newPath) && !string.Equals(oldPath, newPath, StringComparison.Ordinal);
        if (!caseOnly)
        {
            MoveOne(oldPath, newPath, isDir);
            return;
        }
        string tmp = Path.Combine(Path.GetDirectoryName(oldPath)!, ".rename-" + Guid.NewGuid().ToString("N"));
        MoveOne(oldPath, tmp, isDir);
        try
        {
            MoveOne(tmp, newPath, isDir);
        }
        catch
        {
            MoveOne(tmp, oldPath, isDir);
            throw;
        }
    }

    private static void MoveOne(string from, string to, bool isDir)
    {
        if (isDir) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
