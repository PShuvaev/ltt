using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Path identity as the file system sees it: case-insensitive on Windows
/// and macOS (their default volumes), exact on Linux — where `a.txt` and
/// `A.txt` are two different files. macOS also treats both Unicode forms of
/// a name as one (git lists names composed, a folder listing gives them as
/// stored), so there they are compared composed.
/// </summary>
internal static class Paths
{
    private static readonly bool CaseSensitive = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private static readonly StringComparison Comparison =
        CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static readonly StringComparer Comparer = OperatingSystem.IsMacOS() ? new ComposedComparer()
        : CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private sealed class ComposedComparer : StringComparer
    {
        public override int Compare(string? x, string? y) => string.Compare(Canon(x), Canon(y), Comparison);
        public override bool Equals(string? x, string? y) => string.Equals(Canon(x), Canon(y), Comparison);
        public override int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(Canon(s)!);
    }

    private static string? Canon(string? s) =>
        s == null || !OperatingSystem.IsMacOS() || s.IsNormalized() ? s : s.Normalize();

    public static bool Same(string? a, string? b) => Comparer.Equals(a, b);

    /// <summary>Full path without a trailing separator (except for a filesystem root like "/").</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether path lies strictly inside dir.</summary>
    public static bool IsInside(string path, string dir)
    {
        path = Canon(path)!;
        string d = Path.TrimEndingDirectorySeparator(Canon(dir)!);
        if (path.Length <= d.Length || !path.StartsWith(d, Comparison)) return false;
        // A filesystem root ("/", "C:\") keeps its separator; any other dir must be followed by one.
        return IsSeparator(d[^1]) || IsSeparator(path[d.Length]);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    /// <summary>Whether path is dir itself or lies inside it.</summary>
    public static bool IsSameOrInside(string path, string dir) => Same(Path.TrimEndingDirectorySeparator(path),
        Path.TrimEndingDirectorySeparator(dir)) || IsInside(path, dir);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, byte[] resolved);

    /// <summary>
    /// The full path with every symlink in it resolved — for a path that does
    /// not exist, in the folders above it; as written on Windows.
    /// </summary>
    public static string Real(string path)
    {
        string full = Normalize(path);
        if (OperatingSystem.IsWindows()) return full;
        var buf = new byte[4096]; // PATH_MAX
        try
        {
            if (realpath(full, buf) != IntPtr.Zero) return Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0));
        }
        catch (EntryPointNotFoundException) { return full; }
        return Path.GetDirectoryName(full) is { } parent ? Path.Combine(Real(parent), Path.GetFileName(full)) : full;
    }

    /// <summary>Whether two paths name one file or folder, symlinks resolved.</summary>
    public static bool SameReal(string a, string b) => Same(a, b) || Same(Real(a), Real(b));

    /// <summary>
    /// The path spelled from the first of the folders that holds it, though it
    /// or they may be reached through a symlink (a linked folder; /tmp for
    /// /private/tmp on macOS), so one file is one tab and one tree entry. Null
    /// when none of them holds it.
    /// </summary>
    public static string? Within(string path, IReadOnlyList<string> folders)
    {
        if (folders.Any(f => IsSameOrInside(path, f))) return path;
        string real = Real(path);
        foreach (string f in folders)
        {
            string r = Real(f);
            if (Same(real, r)) return f;
            if (IsInside(real, r)) return Path.Combine(f, Path.GetRelativePath(r, real));
        }
        return null;
    }
}
