using System.Runtime.InteropServices;

/// <summary>Moving and copying files and folders on disk.</summary>
internal static class FileOps
{
    /// <summary>How a copy went: counted by the copying thread, read by the UI.</summary>
    internal sealed class CopyProgress
    {
        public int Copied;      // files and links
        public int Skipped;     // pipes, sockets, devices: nothing to copy
        public int Failed;
        public string? FirstError;
    }

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
        if (isDir)
        {
            Directory.Move(from, to);
            return;
        }
        bool taken = Path.Exists(to);
        try
        {
            File.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // File.Move links the new name first: when the old one cannot go, the new one must not stay.
            if (!taken && Path.Exists(from) && TextFile.Stat(from) is { } a && TextFile.Stat(to) is { } b
                && (a.Dev, a.Ino) == (b.Dev, b.Ino))
                File.Delete(to);
            throw;
        }
    }

    /// <summary>
    /// Move to a path that does not exist yet: a rename on the same disk. A
    /// folder on another disk is copied, and the original deleted only when
    /// everything made it (a file is moved across disks by File.Move itself).
    /// </summary>
    internal static void Relocate(string from, string to, bool isDir, CopyProgress progress, CancellationToken ct)
    {
        if (!isDir || !OtherDisk(from, to))
        {
            try
            {
                Move(from, to, isDir);
                return;
            }
            catch (IOException e) when (isDir && !OperatingSystem.IsWindows() && e.HResult == 18) { } // EXDEV
        }
        Copy(from, to, progress, ct);
        if (progress.Failed > 0 || progress.Skipped > 0 || ct.IsCancellationRequested) return;
        if (isDir) Directory.Delete(from, recursive: true); // a symlink: just the link
        else File.Delete(from);
    }

    // rename(2) stops at the file system's edge, and so does Directory.Move — whose error does not say
    // so reliably (.NET on macOS reports EXDEV as 203). The folders the entry leaves and enters tell.
    private static bool OtherDisk(string from, string to) => OperatingSystem.IsWindows()
        ? !string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)
        : TextFile.Stat(Path.GetDirectoryName(from)!) is { } a && TextFile.Stat(Path.GetDirectoryName(to)!) is { } b
          && a.Dev != b.Dev;

    /// <summary>
    /// Copy a file or folder to a path that does not exist yet. Links are copied
    /// as links, never followed; pipes and devices are skipped; an entry that
    /// fails is counted and the rest goes on. Files and folders keep their modes.
    /// </summary>
    internal static void Copy(string from, string to, CopyProgress progress, CancellationToken ct)
    {
        if (Path.Exists(to)) throw new IOException($"Already exists: {to}");
        CopyEntry(Directory.Exists(from) ? new DirectoryInfo(from) : new FileInfo(from), to, progress, ct);
    }

    private static void CopyEntry(FileSystemInfo from, string to, CopyProgress progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            if (from.LinkTarget is { } link) CopyLink(from, to, link);
            else if (from is DirectoryInfo dir)
            {
                // Private from the start, as the original (0700 stays 0700); its exact mode once it is
                // filled, so that a read-only folder still takes what goes in it.
                var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(from.FullName);
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(to);
                else Directory.CreateDirectory(to, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var entry in dir.EnumerateFileSystemInfos())
                    CopyEntry(entry, Path.Combine(to, entry.Name), progress, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, mode);
                return;
            }
            else if (TextFile.Stat(from.FullName) is { Regular: false })
            {
                progress.Skipped++;
                return;
            }
            else File.Copy(from.FullName, to);
            progress.Copied++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            progress.Failed++;
            progress.FirstError ??= e.Message;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buf, nint size);

    [DllImport("libc", SetLastError = true)]
    private static extern int symlink(byte[] target, string linkPath);

    /// <summary>A link copied as the bytes it holds: a target that is not UTF-8 would not survive a string.</summary>
    private static void CopyLink(FileSystemInfo from, string to, string link)
    {
        if (!OperatingSystem.IsWindows())
        {
            var buf = new byte[4097]; // PATH_MAX and the closing NUL
            nint n;
            try { n = readlink(from.FullName, buf, 4096); }
            catch (EntryPointNotFoundException) { n = -1; }
            if (n >= 0)
            {
                buf[n] = 0;
                if (symlink(buf, to) != 0)
                    throw new IOException($"Cannot make the link {to}: {Marshal.GetLastPInvokeErrorMessage()}");
                return;
            }
        }
        if (from is DirectoryInfo) Directory.CreateSymbolicLink(to, link);
        else File.CreateSymbolicLink(to, link);
    }

    /// <summary>
    /// dir/name, or the first free "name 2", "name 3"… (the number goes before a file's extension);
    /// alsoTaken — names in use though nothing is on disk there.
    /// </summary>
    internal static string FreeName(string dir, string name, bool isDir, Func<string, bool>? alsoTaken = null)
    {
        int dot = isDir ? -1 : name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name, ext = dot > 0 ? name[dot..] : "";
        string path = Path.Combine(dir, name);
        for (int n = 2; Path.Exists(path) || alsoTaken?.Invoke(path) == true; n++) path = Path.Combine(dir, $"{stem} {n}{ext}");
        return path;
    }
}
