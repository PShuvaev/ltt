using System.Diagnostics;
using System.Text;

/// <summary>Short-lived helper processes (git, clipboard utilities): UTF-8 text in and out, bounded in time.</summary>
internal static class Proc
{
    /// <summary>
    /// Run a program and return its output when it exits with an accepted code;
    /// null when it cannot start, fails, outlives timeoutMs or is cancelled (it is then killed).
    /// </summary>
    public static string? Run(string file, string args, string? workDir = null, string? stdin = null,
        int timeoutMs = 3000, int[]? okExitCodes = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? env = null) =>
        Start(file, args, workDir, stdin, timeoutMs, okExitCodes, ct, env, p => p.StandardOutput.ReadToEndAsync());

    /// <summary>Like Run, with the output as bytes (file contents in any encoding).</summary>
    public static byte[]? RunBytes(string file, string args, string? workDir = null, string? stdin = null,
        int timeoutMs = 3000) =>
        Start(file, args, workDir, stdin, timeoutMs, null, default, null, async p =>
        {
            var output = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(output);
            return output.ToArray();
        });

    private static T? Start<T>(string file, string args, string? workDir, string? stdin, int timeoutMs, int[]? okExitCodes,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env, Func<Process, Task<T>> read) where T : class
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = workDir ?? "",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (env != null)
                foreach (var (key, value) in env) psi.Environment[key] = value;
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var reading = read(proc);
            _ = proc.StandardError.ReadToEndAsync();
            if (stdin != null) proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!proc.WaitForExit(50))
            {
                if (!ct.IsCancellationRequested && Environment.TickCount64 < deadline) continue;
                try { proc.Kill(); } catch (InvalidOperationException) { /* already gone */ }
                return null;
            }
            return (okExitCodes ?? new[] { 0 }).Contains(proc.ExitCode) && reading.Wait(1000) ? reading.Result : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null; // the program is missing, or the folder vanished
        }
    }
}
