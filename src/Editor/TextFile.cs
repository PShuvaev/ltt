using System.Runtime.InteropServices;
using System.Text;

public enum TextEncoding { Utf8, Utf16LE, Utf16BE, Latin1, Utf32LE, Utf32BE }

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
            TextEncoding.Utf32LE => "UTF-32 LE",
            TextEncoding.Utf32BE => "UTF-32 BE",
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
        var info = Followed(path);
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

    /// <summary>
    /// A file's lines streamed (for search), decoded as Read decodes them: by its
    /// BOM, else as UTF-8 — strictly, so a file that is not UTF-8 throws
    /// DecoderFallbackException, to be read again as Latin-1 (latin1: true).
    /// </summary>
    public static IEnumerable<string> ReadLines(string path, bool latin1)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = latin1
            ? new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false)
            : new StreamReader(fs, DecoderFor(TextEncoding.Utf8), detectEncodingFromByteOrderMarks: true);
        var split = new LineSplitter(); // the lines Read makes, so a hit's line number is the editor's
        var lines = new List<string>();
        var buf = new char[1 << 16];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            split.Feed(buf.AsSpan(0, read), lines);
            foreach (var line in lines) yield return line;
            lines.Clear();
        }
        split.Finish(lines);
        foreach (var line in lines) yield return line;
    }

    /// <summary>Lines from bytes, decoded the way a file is read (a committed version of one, say).</summary>
    public static List<string> Lines(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Decode(ms).Lines;
    }

    private static (List<string> Lines, TextFormat Format) Decode(Stream fs)
    {
        Span<byte> head = stackalloc byte[4];
        int n = fs.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        var (enc, bomLen) =
            n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? (TextEncoding.Utf8, 3)
            : n >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0 ? (TextEncoding.Utf32LE, 4)
            : n >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 0xFE && head[3] == 0xFF ? (TextEncoding.Utf32BE, 4)
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
        TextEncoding.Utf32LE => new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true),
        TextEncoding.Utf32BE => new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true),
        _ => Encoding.Latin1,
    };

    private static Encoding EncoderFor(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => new UTF8Encoding(false),
        TextEncoding.Utf16LE => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        TextEncoding.Utf16BE => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        TextEncoding.Utf32LE => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        TextEncoding.Utf32BE => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => Encoding.Latin1,
    };

    private static byte[] Bom(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => new byte[] { 0xEF, 0xBB, 0xBF },
        TextEncoding.Utf16LE => new byte[] { 0xFF, 0xFE },
        TextEncoding.Utf16BE => new byte[] { 0xFE, 0xFF },
        TextEncoding.Utf32LE => new byte[] { 0xFF, 0xFE, 0, 0 },
        TextEncoding.Utf32BE => new byte[] { 0, 0, 0xFE, 0xFF },
        _ => Array.Empty<byte>(),
    };

    /// <summary>Decode and split into lines (see LineSplitter), counting each kind of break.</summary>
    private static (List<string>, TextFormat) ReadAs(Stream fs, TextEncoding enc, int bomLen)
    {
        fs.Position = bomLen;
        using var reader = new StreamReader(fs, DecoderFor(enc), detectEncodingFromByteOrderMarks: false,
            bufferSize: 1 << 16, leaveOpen: true);
        var lines = new List<string>();
        var split = new LineSplitter();
        var buf = new char[1 << 16];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            var span = buf.AsSpan(0, read);
            if (enc is TextEncoding.Utf8 or TextEncoding.Latin1 && span.Contains('\0'))
                throw new InvalidDataException("The file looks binary; editing is disabled.");
            split.Feed(span, lines);
        }
        split.Finish(lines);

        var (lf, crlf, cr) = (split.Lf, split.CrLf, split.Cr);
        int kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        string newLine = crlf > lf && crlf >= cr ? "\r\n" : cr > lf && cr > crlf ? "\r" : "\n";
        return (lines, new TextFormat(enc, bomLen > 0, newLine, kinds > 1));
    }

    /// <summary>
    /// Lines out of decoded text, a chunk at a time: broken at \n and \r\n, and at a lone \r only in
    /// text with no \n at all (old Mac files) — elsewhere a lone \r is part of its line (a progress bar
    /// redrawn in a log). Lines a lone \r ended wait until the first \n, or the end, tells which they are.
    /// </summary>
    private sealed class LineSplitter
    {
        public int Lf, CrLf, Cr;            // the breaks, by kind
        private readonly StringBuilder _partial = new();
        private List<string>? _held;        // ended by a lone \r while no \n has come yet
        private bool _pendingCr;            // a \r ended the last chunk: a \n may still follow
        private bool _sawLf;

        public void Feed(ReadOnlySpan<char> s, List<string> lines)
        {
            int i = 0;
            if (_pendingCr && s.Length > 0)
            {
                _pendingCr = false;
                if (s[0] == '\n') { CrLf++; Emit(default, lines); i = 1; }
                else LoneCr();
            }
            while (i < s.Length)
            {
                int k = s[i..].IndexOfAny('\r', '\n');
                if (k < 0) { _partial.Append(s[i..]); return; }
                int at = i + k;
                if (s[at] == '\n') { Lf++; Emit(s[i..at], lines); i = at + 1; }
                else if (at + 1 == s.Length) { _partial.Append(s[i..at]); _pendingCr = true; return; }
                else if (s[at + 1] == '\n') { CrLf++; Emit(s[i..at], lines); i = at + 2; }
                else { _partial.Append(s[i..at]); LoneCr(); i = at + 1; }
            }
        }

        /// <summary>The end of the text: lines still held were lines after all; then the last one ("" after a break).</summary>
        public void Finish(List<string> lines)
        {
            if (_pendingCr) { _pendingCr = false; LoneCr(); }
            if (_held != null) { lines.AddRange(_held); Cr = _held.Count; _held = null; }
            lines.Add(_partial.ToString());
            _partial.Clear();
        }

        private void LoneCr()
        {
            if (_sawLf) _partial.Append('\r'); // text: this file breaks its lines with \n
            else
            {
                (_held ??= new List<string>()).Add(_partial.ToString());
                _partial.Clear();
            }
        }

        private void Emit(ReadOnlySpan<char> tail, List<string> lines)
        {
            string line = _partial.Length == 0 ? new string(tail) : _partial.Append(tail).ToString();
            _partial.Clear();
            if (_held != null) // the first \n: the lone \r before it were text
            {
                line = string.Join('\r', _held) + '\r' + line;
                _held = null;
            }
            _sawLf = true;
            lines.Add(line);
        }
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
                if (bad < 0) continue;
                Rune.DecodeFromUtf16(line.AsSpan(bad), out var rune, out _); // the whole character, not half an emoji
                throw new IOException($"'{rune}' cannot be saved in this file's Latin-1 encoding.");
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
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 1 << 16,
            };
            // An existing file's text stays its owner's until the copy takes the file's own mode.
            if (kind != null) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            fs = new FileStream(temp, options);
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
                CopyXattrs(target, temp); // while the copy is still writable
                // The owner and the group first (a change of owner may clear set-id bits), then the mode.
                if (kind is { } k && chown(temp, k.Uid, k.Gid) != 0) chown(temp, uint.MaxValue, k.Gid);
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

    /// <summary>
    /// Overwrite the file itself. Not atomic: the new bytes are made in full before it is touched, and
    /// should the writing stop midway (a full disk), the old bytes go back over the part it reached.
    /// </summary>
    private static void WriteInPlace(string target, IReadOnlyList<string> lines, TextFormat format, Encoding encoding)
    {
        var bytes = new MemoryStream();
        WriteTo(bytes, lines, format, encoding);
        using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        var old = new byte[fs.Length];
        fs.ReadExactly(old);
        const int Chunk = 1 << 16;
        long done = 0;
        try
        {
            var buf = bytes.GetBuffer();
            for (long len = bytes.Length; done < len; done += Chunk)
                RandomAccess.Write(fs.SafeFileHandle, buf.AsSpan((int)done, (int)Math.Min(Chunk, len - done)), done);
            fs.SetLength(bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) // EFBIG: ArgumentOutOfRange
        {
            if (!PutBack(fs, old, done + Chunk))
                throw new IOException($"{e.Message} The file may be damaged: its old text could not be put back.", e);
            throw;
        }
    }

    /// <summary>The old bytes over the part a failed write may have reached; whether the file is as it was.</summary>
    private static bool PutBack(FileStream fs, byte[] old, long reached)
    {
        try { RandomAccess.Write(fs.SafeFileHandle, old.AsSpan(0, (int)Math.Min(old.Length, reached)), 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { } // as far as it goes: checked below
        try
        {
            if (fs.Length != old.Length) fs.SetLength(old.Length);
            var now = new byte[old.Length];
            for (int got = 0, n; got < now.Length; got += n)
                if ((n = RandomAccess.Read(fs.SafeFileHandle, now.AsSpan(got), got)) == 0) return false;
            return now.AsSpan().SequenceEqual(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
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

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int copyfile(string from, string to, IntPtr state, uint flags); // macOS

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ListXattrs(string path, byte[]? list, nint size); // Linux

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(string path, string name, byte[]? value, nint size);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(string path, string name, byte[] value, nint size, int flags);

    /// <summary>
    /// A file's extended attributes (Finder tags, user.* ones) onto its replacement, as far as they
    /// go: one that may not be set (security.* without the right) is left out.
    /// </summary>
    private static void CopyXattrs(string from, string to)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                copyfile(from, to, IntPtr.Zero, 1 << 2 /* COPYFILE_XATTR */);
                return;
            }
            if (!OperatingSystem.IsLinux()) return;
            nint size = ListXattrs(from, null, 0);
            if (size <= 0) return;
            var list = new byte[size];
            if ((size = ListXattrs(from, list, size)) <= 0) return;
            foreach (string name in Encoding.UTF8.GetString(list, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                nint length = GetXattr(from, name, null, 0);
                if (length < 0) continue;
                var value = new byte[length];
                if ((length = GetXattr(from, name, value, length)) >= 0) SetXattr(to, name, value, length, 0);
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
    }

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

    /// <summary>
    /// Whether path is a regular file, how many names it has, which file it is (the device and the
    /// inode), its owner and group, and when its inode last changed (ns); null when unknown. Links are followed.
    /// </summary>
    internal static (bool Regular, long Links, long Dev, long Ino, uint Uid, uint Gid, long ChangeNs)? Stat(string path)
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        bool mac = OperatingSystem.IsMacOS(), linux = OperatingSystem.IsLinux();
        // Byte offsets of st_mode and st_nlink, the size of st_nlink, st_uid (st_gid follows it) and
        // st_ctim. st_dev comes first (4 bytes on macOS, 8 on Linux) and st_ino sits at 8 everywhere.
        var (mode, links, linkSize, uid, ctime) =
            mac ? (4, 6, 2, 16, 64)
            : linux && arch == Architecture.X64 ? (24, 16, 8, 28, 104)
            : linux && arch == Architecture.Arm64 ? (16, 20, 4, 24, 104)
            : (-1, -1, 0, 0, 0);
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
        long dev = mac ? BitConverter.ToInt32(buf, 0) : BitConverter.ToInt64(buf, 0);
        long changeNs = BitConverter.ToInt64(buf, ctime) * 1_000_000_000 + BitConverter.ToInt64(buf, ctime + 8);
        return ((BitConverter.ToUInt16(buf, mode) & 0xF000) == 0x8000, count, dev, BitConverter.ToInt64(buf, 8),
            BitConverter.ToUInt32(buf, uid), BitConverter.ToUInt32(buf, uid + 4), changeNs);
    }

    /// <summary>
    /// What identifies a version of the file on disk; null when it is missing. Besides the time and
    /// the size, the inode and its change time: a rewrite that keeps the first two (cp -p, rsync -t,
    /// a coarse clock) still moves these — and so does a mere chmod, so the text has the last word.
    /// </summary>
    public static (DateTime WriteUtc, long Length, long Ino, long ChangeNs)? Stamp(string path)
    {
        try
        {
            var fi = Followed(path);
            if (!fi.Exists) return null;
            var st = Stat(path);
            return (fi.LastWriteTimeUtc, fi.Length, st?.Ino ?? 0, st?.ChangeNs ?? 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The file a path names: through any links to their final target (whose time and size are the file's).</summary>
    private static FileInfo Followed(string path)
    {
        var fi = new FileInfo(path);
        return fi.LinkTarget != null && fi.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target ? target : fi;
    }
}
