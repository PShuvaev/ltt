using System.Text;

/// <summary>
/// Unsaved edits that outlive a crash, a hangup or a kill signal: each dirty
/// buffer is written to a recovery file outside the project, and the next
/// start reopens them as unsaved tabs. A session keeps only its latest set
/// of files and removes them when it ends normally; restored files are deleted.
/// </summary>
internal static class Recovery
{
    private const string Magic = "ltt-recovery 1";

    /// <summary>Where recovery files go (LTT_RECOVERY_DIR overrides it, e.g. for tests).</summary>
    public static string Dir => Environment.GetEnvironmentVariable("LTT_RECOVERY_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), "ltt", "recovery");

    // Names are "<UTC time>-<session>-<sequence>.txt", so they sort oldest first.
    private static readonly string Session = Guid.NewGuid().ToString("N")[..8];
    private static int _sequence;
    private static readonly object Gate = new();
    private static List<string> _written = new(); // this session's current set

    /// <summary>Whether this session has recovery files on disk.</summary>
    public static bool HasFiles { get { lock (Gate) return _written.Count > 0; } }

    /// <summary>
    /// Write every dirty buffer as this session's new set, replacing its
    /// earlier one; returns how many were written. Never throws.
    /// </summary>
    public static int Save(IEnumerable<TextEditor> editors)
    {
        lock (Gate)
        {
            var written = new List<string>();
            foreach (var ed in editors)
            {
                try
                {
                    if (!ed.Dirty || ed.FilePath is not { } path || ed.SnapshotLines() is not { } lines) continue;
                    Directory.CreateDirectory(Dir);
                    string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Session}-{++_sequence:D6}.txt";
                    string file = Path.Combine(Dir, name), temp = file + ".tmp";
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        // The line count shows a file cut short for what it is.
                        w.Write($"{Magic}\n{lines.Length}\n{path}\n");
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (i > 0) w.Write('\n');
                            w.Write(lines[i]);
                        }
                        w.Flush();
                        fs.Flush(flushToDisk: true);
                    }
                    File.Move(temp, file, overwrite: true); // complete or not there at all
                    written.Add(file);
                }
                catch (Exception) { /* best effort: the next buffer may still make it */ }
            }
            foreach (var old in _written) Delete(old);
            _written = written;
            return written.Count;
        }
    }

    /// <summary>The session ended with nothing left unsaved: its files are not needed.</summary>
    public static void Clear() => Save(Array.Empty<TextEditor>());

    /// <summary>
    /// Recovered buffers, the newest for each path, oldest first: the recovery
    /// file, the original path, the lines. Older copies of a path are deleted.
    /// </summary>
    public static List<(string File, string Path, List<string> Lines)> Load()
    {
        var newest = new Dictionary<string, (string File, string Path, List<string> Lines)>(Paths.Comparer);
        try
        {
            if (!Directory.Exists(Dir)) return new();
            foreach (var file in Directory.GetFiles(Dir, "*.txt").Order(StringComparer.Ordinal))
            {
                try
                {
                    var all = File.ReadAllText(file, Encoding.UTF8).Split('\n');
                    if (all.Length < 4 || all[0] != Magic || all[2].Length == 0
                        || !int.TryParse(all[1], out int count) || all.Length - 3 != count)
                        continue; // not ours, or cut short
                    if (newest.TryGetValue(all[2], out var older)) Delete(older.File);
                    newest[all[2]] = (file, all[2], all[3..].ToList());
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return newest.Values.OrderBy(r => r.File, StringComparer.Ordinal).ToList();
    }

    public static void Delete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Append an error report next to the recovery files; returns its path (or "").</summary>
    public static string LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string log = Path.Combine(Path.GetDirectoryName(Dir)!, "crash.log");
            File.AppendAllText(log, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
            return log;
        }
        catch (Exception) { return ""; }
    }
}
