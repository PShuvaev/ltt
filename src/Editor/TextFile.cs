using System.Runtime.InteropServices;
using System.Text;

public enum TextEncoding { Utf8, Utf16LE, Utf16BE, Latin1 }

/// <summary>How a file's text is stored on disk; saving writes it back the same way.</summary>
public sealed record TextFormat(TextEncoding Encoding, bool Bom, string NewLine, bool MixedNewLines = false)
{
    public static readonly TextFormat Default = new(TextEncoding.Utf8, false, "\n");

    /// <summary>Notable traits for the status line: empty for UTF-8 with LF line endings.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Encoding != TextEncoding.Utf8) parts.Add(Encoding switch
        {
            TextEncoding.Latin1 => "Latin-1 (not valid UTF-8)",
            TextEncoding.Utf16LE => "UTF-16 LE",
            _ => "UTF-16 BE",
        });
        else if (Bom) parts.Add("UTF-8 with BOM");
        string nl = NewLine == "\r\n" ? "CRLF" : NewLine == "\r" ? "CR" : "LF";
        if (MixedNewLines) parts.Add($"mixed line endings, saved as {nl}");
        else if (nl != "LF") parts.Add(nl);
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Reading and writing text files so that what the user did not edit stays
/// byte-for-byte the same: encoding and BOM, line endings, and whether the
/// file ends with a line break (the line list then ends with an empty line).
/// </summary>
internal static class TextFile
{
    /// <summary>Read a file into lines (without their breaks) and its format.</summary>
    public static (List<string> Lines, TextFormat Format) Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"File not found: {path}", path);
        if (info.Length == 0)
        {
            // FIFOs, sockets and devices report no length and would block or never end.
            if (Stat(path) is { Regular: false }) throw new IOException($"Not a regular file: {path}");
            return (new List<string> { "" }, TextFormat.Default);
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Decode(fs);
    }

    /// <summary>Lines from bytes, decoded the way a file is read (a committed version of one, say).</summary>
    public static List<string> Lines(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Decode(ms).Lines;
    }

    private static (List<string> Lines, TextFormat Format) Decode(Stream fs)
    {
        Span<byte> head = stackalloc byte[3];
        int n = fs.ReadAtLeast(head, 3, throwOnEndOfStream: false);
        var (enc, bomLen) =
            n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? (TextEncoding.Utf8, 3)
            : n >= 2 && head[0] == 0xFF && head[1] == 0xFE ? (TextEncoding.Utf16LE, 2)
            : n >= 2 && head[0] == 0xFE && head[1] == 0xFF ? (TextEncoding.Utf16BE, 2)
            : (TextEncoding.Utf8, 0);
        try
        {
            return ReadAs(fs, enc, bomLen);
        }
        catch (DecoderFallbackException)
        {
            // Not valid in its encoding. Latin-1 maps every byte (a BOM too) to one
            // character, so the bytes round-trip unchanged; broken UTF-16 is full of
            // NULs and is refused as binary.
            return ReadAs(fs, TextEncoding.Latin1, 0);
        }
    }

    private static Encoding DecoderFor(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => new UTF8Encoding(false, throwOnInvalidBytes: true),
        TextEncoding.Utf16LE => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
        TextEncoding.Utf16BE => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
        _ => Encoding.Latin1,
    };

    private static Encoding EncoderFor(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => new UTF8Encoding(false),
        TextEncoding.Utf16LE => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        TextEncoding.Utf16BE => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        _ => Encoding.Latin1,
    };

    private static byte[] Bom(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => new byte[] { 0xEF, 0xBB, 0xBF },
        TextEncoding.Utf16LE => new byte[] { 0xFF, 0xFE },
        TextEncoding.Utf16BE => new byte[] { 0xFE, 0xFF },
        _ => Array.Empty<byte>(),
    };

    /// <summary>Decode and split on \n, \r\n and \r, counting each kind of break.</summary>
    private static (List<string>, TextFormat) ReadAs(Stream fs, TextEncoding enc, int bomLen)
    {
        fs.Position = bomLen;
        using var reader = new StreamReader(fs, DecoderFor(enc), detectEncodingFromByteOrderMarks: false,
            bufferSize: 1 << 16, leaveOpen: true);
        var lines = new List<string>();
        var partial = new StringBuilder();
        var buf = new char[1 << 16];
        int lf = 0, crlf = 0, cr = 0;
        bool pendingCr = false; // a \r ended the last chunk: a \n may still follow
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            var span = buf.AsSpan(0, read);
            if (enc is TextEncoding.Utf8 or TextEncoding.Latin1 && span.Contains('\0'))
                throw new InvalidDataException("The file looks binary; editing is disabled.");
            int i = 0;
            if (pendingCr)
            {
                pendingCr = false;
                if (span[0] == '\n') { crlf++; i = 1; }
                else cr++;
            }
            while (i < read)
            {
                int k = span[i..].IndexOfAny('\r', '\n');
                if (k < 0) { partial.Append(span[i..]); break; }
                if (partial.Length == 0) lines.Add(new string(span.Slice(i, k)));
                else { partial.Append(span.Slice(i, k)); lines.Add(partial.ToString()); partial.Clear(); }
                int at = i + k;
                if (span[at] == '\n') { lf++; i = at + 1; }
                else if (at + 1 == read) { pendingCr = true; i = read; }
                else if (span[at + 1] == '\n') { crlf++; i = at + 2; }
                else { cr++; i = at + 1; }
            }
        }
        if (pendingCr) cr++;
        lines.Add(partial.ToString()); // after the last break: "" when the file ends with one

        int kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        string newLine = crlf > lf && crlf >= cr ? "\r\n" : cr > lf && cr > crlf ? "\r" : "\n";
        return (lines, new TextFormat(enc, bomLen > 0, newLine, kinds > 1));
    }

    /// <summary>
    /// Write lines joined by the format's line break. The new text goes to a
    /// temporary file next to the target, which then replaces it in one
    /// rename — a failure midway leaves the original intact. Symlinks are
    /// written through, permissions are kept, read-only files, pipes and
    /// devices are refused. A file with other hard links, or in a read-only
    /// folder, is overwritten in place instead.
    /// </summary>
    public static void Write(string path, IReadOnlyList<string> lines, TextFormat format)
    {
        var encoding = EncoderFor(format.Encoding);
        // Latin-1 can only hold U+0000..U+00FF: refuse up front rather than write '?'.
        if (format.Encoding == TextEncoding.Latin1)
            foreach (var line in lines)
            {
                int bad = line.AsSpan().IndexOfAnyExceptInRange('\0', '\u00FF');
                if (bad >= 0)
                    throw new IOException($"'{line[bad]}' cannot be saved in this file's Latin-1 encoding.");
            }

        string target = path;
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } link) target = link.FullName;
        }
        catch (IOException) { /* a dangling or looping link: write the path itself */ }
        var kind = File.Exists(target) ? Stat(target) : null;
        if (kind is { Regular: false }) throw new IOException($"Not a regular file: {target}");
        if (File.Exists(target) && !IsWritable(target))
            throw new UnauthorizedAccessException($"The file is read-only: {target}");
        // Replacing a hard-linked file would leave its other names with the old text.
        if (kind is { Links: > 1 }) { WriteInPlace(target, lines, format, encoding); return; }

        // A short name: the file's own name may already be as long as a name can be.
        string temp = Path.Combine(Path.GetDirectoryName(target)!, $".ltt-{Guid.NewGuid().ToString("N")[..12]}.tmp");
        FileStream fs;
        try
        {
            fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (UnauthorizedAccessException) when (File.Exists(target))
        {
            // The folder is read-only but the file is not. Any other failure
            // (a full disk) is reported with the original untouched.
            WriteInPlace(target, lines, format, encoding);
            return;
        }
        try
        {
            using (fs)
            {
                WriteTo(fs, lines, format, encoding);
                fs.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(target))
            {
                try { File.SetUnixFileMode(temp, File.GetUnixFileMode(target)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Overwrite the file itself. Not atomic: the new bytes are made in full before it is touched.</summary>
    private static void WriteInPlace(string target, IReadOnlyList<string> lines, TextFormat format, Encoding encoding)
    {
        var bytes = new MemoryStream();
        WriteTo(bytes, lines, format, encoding);
        using var fs = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.Write(bytes.GetBuffer(), 0, (int)bytes.Length);
        fs.SetLength(bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static void WriteTo(Stream stream, IReadOnlyList<string> lines, TextFormat format, Encoding encoding)
    {
        if (format.Bom) stream.Write(Bom(format.Encoding));
        using var w = new StreamWriter(stream, encoding, 1 << 16, leaveOpen: true);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) w.Write(format.NewLine);
            w.Write(lines[i]);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    private static bool IsWritable(string path) => OperatingSystem.IsWindows()
        ? (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0
        : access(path, 2 /* W_OK */) == 0;

    // stat(2) for the file type and the hard-link count. struct stat differs
    // per platform: only the layouts below are read, elsewhere it is unknown.
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat(string path, byte[] buf);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int statInode64(string path, byte[] buf);

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int xstat(int version, string path, byte[] buf);

    /// <summary>Whether path is a regular file and how many names it has; null when unknown.</summary>
    internal static (bool Regular, long Links)? Stat(string path)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        bool mac = OperatingSystem.IsMacOS(), linux = OperatingSystem.IsLinux();
        // Byte offsets of st_mode and st_nlink, and the size of st_nlink.
        var (mode, links, linkSize) =
            mac ? (4, 6, 2)
            : linux && arch == Architecture.X64 ? (24, 16, 8)
            : linux && arch == Architecture.Arm64 ? (16, 20, 4)
            : (-1, -1, 0);
        if (mode < 0) return null;
        var buf = new byte[256];
        int rc;
        try
        {
            try { rc = mac && arch == Architecture.X64 ? statInode64(path, buf) : stat(path, buf); }
            catch (EntryPointNotFoundException) when (linux)
            {
                rc = xstat(arch == Architecture.X64 ? 1 : 0, path, buf); // glibc before 2.33
            }
        }
        catch (EntryPointNotFoundException) { return null; }
        if (rc != 0) return null;
        long count = linkSize switch
        {
            2 => BitConverter.ToUInt16(buf, links),
            4 => BitConverter.ToUInt32(buf, links),
            _ => BitConverter.ToInt64(buf, links),
        };
        return ((BitConverter.ToUInt16(buf, mode) & 0xF000) == 0x8000, count);
    }

    /// <summary>What identifies a version of the file on disk; null when it is missing.</summary>
    public static (DateTime WriteUtc, long Length)? Stamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
