using System.Diagnostics;
using System.Text;

namespace ConsoleEditor;

/// <summary>
/// Putting text on the system clipboard via two routes at once: the system
/// utility (pbcopy etc. — works in terminals without OSC 52) and OSC 52
/// (works over SSH, where the local machine's utility is absent).
/// </summary>
internal static class Clipboard
{
    /// <summary>System clipboard utilities for the current OS, in priority order.</summary>
    internal static List<(string File, string Args)> HelperCandidates()
    {
        if (OperatingSystem.IsMacOS()) return new() { ("pbcopy", "") };
        if (OperatingSystem.IsWindows()) return new() { ("clip", "") };
        return new() { ("wl-copy", ""), ("xclip", "-selection clipboard"), ("xsel", "--clipboard --input") };
    }

    private static (string File, string Args)? _helper;
    private static bool _helperResolved;

    private static (string File, string Args)? ResolveHelper()
    {
        if (_helperResolved) return _helper;
        _helperResolved = true;
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var cand in HelperCandidates())
        {
            foreach (string dir in pathDirs)
            {
                if (File.Exists(Path.Combine(dir, cand.File))
                    || (OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, cand.File + ".exe"))))
                    return _helper = cand;
            }
        }
        return null;
    }

    public static void Copy(string text, bool useSystemHelper = true)
    {
        if (useSystemHelper) TrySystem(text);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write($"\x1b]52;c;{b64}\a");
    }

    /// <summary>Feed text to the system clipboard utility, if present. Errors are silently ignored.</summary>
    private static void TrySystem(string text)
    {
        if (ResolveHelper() is not { } helper) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = helper.File,
                Arguments = helper.Args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc == null) return;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Utility unavailable — OSC 52 still applies.
        }
    }
}
