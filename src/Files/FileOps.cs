namespace ConsoleEditor;

/// <summary>Moving files and folders on disk.</summary>
internal static class FileOps
{
    /// <summary>Move a file/folder; a case-only rename goes through a temporary name (for case-insensitive FS).</summary>
    internal static void Move(string oldPath, string newPath, bool isDir)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            string tmp = Path.Combine(Path.GetDirectoryName(oldPath)!, ".rename-" + Guid.NewGuid().ToString("N"));
            if (isDir) Directory.Move(oldPath, tmp); else File.Move(oldPath, tmp);
            if (isDir) Directory.Move(tmp, newPath); else File.Move(tmp, newPath);
            return;
        }
        if (isDir) Directory.Move(oldPath, newPath);
        else File.Move(oldPath, newPath);
    }
}
