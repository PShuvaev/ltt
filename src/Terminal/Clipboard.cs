using System.Text;

/// <summary>
/// The system clipboard via two routes at once: the system utility (pbcopy
/// etc. — works in terminals without OSC 52) and OSC 52 (works over SSH,
/// where the local machine's utility is absent). Reading goes through the
/// utility only.
/// </summary>
internal static class Clipboard
{
    /// <summary>System clipboard writers for the current OS, in priority order.</summary>
    internal static List<(string File, string Args)> HelperCandidates()
    {
        if (OperatingSystem.IsMacOS()) return new() { ("pbcopy", "") };
        if (OperatingSystem.IsWindows()) return new() { ("clip", "") };
        return new() { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") };
    }

    /// <summary>System clipboard readers for the current OS, in priority order.</summary>
    internal static List<(string File, string Args)> PasteCandidates()
    {
        if (OperatingSystem.IsMacOS()) return new() { ("pbpaste", "") };
        if (OperatingSystem.IsWindows())
            return new() { ("powershell", "-NoProfile -NonInteractive -Command \"Get-Clipboard -Raw\"") };
        return new() { ("wl-paste", "--no-newline"), ("xclip", "-selection clipboard -o"), ("xsel", "--clipboard --output") };
    }

    private const int MaxPasteChars = 4_000_000;
    private static readonly object CopyLock = new();
    private static Task _copying = Task.CompletedTask; // copies run one after another, off the UI thread

    /// <summary>The first candidate whose program is on PATH.</summary>
    private static (string File, string Args)? FirstOnPath(List<(string File, string Args)> candidates)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var cand in candidates)
            foreach (string dir in pathDirs)
                if (File.Exists(Path.Combine(dir, cand.File))
                    || (OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, cand.File + ".exe"))))
                    return cand;
        return null;
    }

    // pbcopy/pbpaste pick the text encoding from the locale: without a UTF-8
    // one (common in SSH sessions and IDE terminals) non-ASCII text is garbled.
    private static readonly Dictionary<string, string>? HelperEnv =
        OperatingSystem.IsMacOS() ? new() { ["LC_ALL"] = "en_US.UTF-8" } : null;

    private static string? RunHelper((string File, string Args) helper, string? input, int timeoutMs) =>
        Proc.Run(helper.File, helper.Args, stdin: input, timeoutMs: timeoutMs, env: HelperEnv);

    /// <summary>Put text on the clipboard; the system utility runs in the background, never blocking the UI.</summary>
    public static void Copy(string text)
    {
        if (FirstOnPath(HelperCandidates()) is { } helper)
            lock (CopyLock) _copying = _copying.ContinueWith(_ => RunHelper(helper, text, 3000), TaskScheduler.Default);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write($"\x1b]52;c;{b64}\a");
    }

    /// <summary>
    /// Read the system clipboard via its utility (pbpaste and friends).
    /// Returns null when no reader is available or it fails/times out —
    /// over SSH there is no read route (OSC 52 read is rarely enabled),
    /// and the terminal's own paste still works there.
    /// </summary>
    public static string? TryPaste()
    {
        if (FirstOnPath(PasteCandidates()) is not { } reader) return null;
        string? text = RunHelper(reader, null, 1500);
        return text != null && text.Length > MaxPasteChars ? text[..MaxPasteChars] : text;
    }
}
