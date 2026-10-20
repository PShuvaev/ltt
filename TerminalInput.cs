using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleEditor;

/// <summary>Input event: a key or the mouse.</summary>
internal abstract record InputEvent;

internal sealed record KeyInput(ConsoleKeyInfo Key) : InputEvent;

/// <summary>
/// Mouse in the SGR protocol (ESC [&lt; b ; x ; y M/m). Coordinates are already 0-based.
/// Button: 0/1/2 — left/middle/right, 64 — wheel up, 65 — wheel down
/// (upper bits may carry modifiers, hence IsWheel/CleanButton).
/// </summary>
internal sealed record MouseInput(int Button, int X, int Y, bool Pressed) : InputEvent
{
    public bool IsWheel => (Button & 64) != 0;
    public bool WheelDown => IsWheel && (Button & 1) != 0;
    /// <summary>Motion with a button held (bit 32) — drag selection.</summary>
    public bool IsMotion => (Button & 32) != 0;
    public bool IsLeftDrag => IsMotion && (Button & 3) == 0;
    public bool IsLeftClick => Pressed && !IsWheel && (Button & 3) == 0 && (Button & 32) == 0;
    public bool IsMiddleClick => Pressed && !IsWheel && (Button & 3) == 1 && (Button & 32) == 0;
    public bool IsRightClick => Pressed && !IsWheel && (Button & 3) == 2 && (Button & 32) == 0;
}

internal interface ITerminalInput : IDisposable
{
    InputEvent Read();

    /// <summary>Read an event but wait no longer than timeoutMs: false — nothing arrived in time.</summary>
    bool TryRead(int timeoutMs, out InputEvent? ev);
}

/// <summary>Fallback without the mouse (Windows, or when raw mode is unavailable).</summary>
internal sealed class ConsoleInput : ITerminalInput
{
    public InputEvent Read() => new KeyInput(Console.ReadKey(intercept: true));

    public bool TryRead(int timeoutMs, out InputEvent? ev)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            bool available;
            try { available = Console.KeyAvailable; }
            catch (InvalidOperationException) { ev = Read(); return true; } // input redirected — just block
            if (available) { ev = Read(); return true; }
            if (DateTime.UtcNow >= deadline) { ev = null; return false; }
            Thread.Sleep(10);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Custom parsing of a Unix terminal input stream: raw mode via termios
/// (which also stops the XOFF/XON driver from swallowing Ctrl+S/Ctrl+Q),
/// key escape sequences and SGR mouse (modes 1000+1006).
/// </summary>
internal sealed class RawTerminalInput : ITerminalInput
{
    private const int StdinFd = 0;
    private const int TcsaNow = 0;
    private const int TermiosSize = 512; // generously sized for macOS and Linux

    private readonly IntPtr _termios = Marshal.AllocHGlobal(TermiosSize);
    private readonly byte[] _savedTermios = new byte[TermiosSize];
    private readonly byte[] _one = new byte[1];
    private readonly Queue<InputEvent> _pending = new();
    private bool _disposed;

    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int act, IntPtr termios);
    [DllImport("libc")] private static extern void cfmakeraw(IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nuint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }
    [DllImport("libc", SetLastError = true)] private static extern int poll(PollFd[] fds, nuint nfds, int timeoutMs);

    public RawTerminalInput()
    {
        if (tcgetattr(StdinFd, _termios) != 0)
            throw new InvalidOperationException("stdin is not a terminal");
        Marshal.Copy(_termios, _savedTermios, 0, TermiosSize);
        cfmakeraw(_termios);
        tcsetattr(StdinFd, TcsaNow, _termios);

        // Mouse reports: 1000 — clicks and wheel, 1002 — motion with a button held, 1006 — SGR.
        Console.Write("\x1b[?1000h\x1b[?1002h\x1b[?1006h");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.Write("\x1b[?1006l\x1b[?1002l\x1b[?1000l");
        Marshal.Copy(_savedTermios, 0, _termios, TermiosSize);
        tcsetattr(StdinFd, TcsaNow, _termios);
        Marshal.FreeHGlobal(_termios);
    }

    // -2 = EOF, -1 = timeout, otherwise a byte
    private int ReadByte(int timeoutMs)
    {
        var pfd = new[] { new PollFd { Fd = StdinFd, Events = 1 /* POLLIN */ } };
        while (true)
        {
            int ready = poll(pfd, 1, timeoutMs);
            if (ready < 0) return -2;
            if (ready == 0) return -1; // timeout expired (never reached when timeoutMs < 0)
            nint n = read(StdinFd, _one, 1);
            if (n > 0) return _one[0];
            if ((pfd[0].Revents & 0x18) != 0) return -2; // POLLHUP | POLLERR — the terminal is gone
            if (timeoutMs >= 0) return -1;
            // read()==0 without hangup happens in canonical mode after Ctrl+D — just keep waiting.
        }
    }

    private static KeyInput QuitKey() => new(new ConsoleKeyInfo('\u0011', ConsoleKey.Q, false, false, true));

    private static ConsoleKeyInfo MakeKey(char ch, ConsoleKey key, bool shift = false, bool alt = false, bool ctrl = false) =>
        new(ch, key, shift, alt, ctrl);

    public InputEvent Read()
    {
        if (_pending.Count > 0) return _pending.Dequeue();

        int b = ReadByte(-1);
        if (b < 0) return QuitKey(); // EOF: the terminal is gone — quit
        return DispatchFirstByte(b);
    }

    public bool TryRead(int timeoutMs, out InputEvent? ev)
    {
        if (_pending.Count > 0) { ev = _pending.Dequeue(); return true; }
        int b = ReadByte(timeoutMs);
        if (b == -1) { ev = null; return false; } // timeout — the main loop has other things to do
        if (b == -2) { ev = QuitKey(); return true; } // EOF
        ev = DispatchFirstByte(b);
        return true;
    }

    private InputEvent DispatchFirstByte(int b)
    {
        switch (b)
        {
            case 0x1B: return ReadEscape();
            case 0x0D or 0x0A: return new KeyInput(MakeKey('\r', ConsoleKey.Enter));
            case 0x09: return new KeyInput(MakeKey('\t', ConsoleKey.Tab));
            case 0x7F or 0x08: return new KeyInput(MakeKey('\b', ConsoleKey.Backspace));
        }
        if (b is >= 0x01 and <= 0x1A) // Ctrl+A..Z (Enter/Tab already handled above)
            return new KeyInput(MakeKey((char)b, ConsoleKey.A + b - 1, ctrl: true));
        if (b < 0x20) return Read(); // ignore other control bytes
        if (b < 0x80) return new KeyInput(CharKey((char)b));
        return ReadUtf8(b);
    }

    private static ConsoleKeyInfo CharKey(char c)
    {
        if (c is >= 'a' and <= 'z') return MakeKey(c, ConsoleKey.A + (c - 'a'));
        if (c is >= 'A' and <= 'Z') return MakeKey(c, ConsoleKey.A + (c - 'A'), shift: true);
        if (c is >= '0' and <= '9') return MakeKey(c, ConsoleKey.D0 + (c - '0'));
        if (c == ' ') return MakeKey(c, ConsoleKey.Spacebar);
        return MakeKey(c, (ConsoleKey)0);
    }

    private InputEvent ReadUtf8(int lead)
    {
        int len = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;
        var bytes = new byte[len];
        bytes[0] = (byte)lead;
        for (int i = 1; i < len; i++)
        {
            int cont = ReadByte(50);
            if (cont < 0) return Read();
            bytes[i] = (byte)cont;
        }
        string s = Encoding.UTF8.GetString(bytes);
        if (s.Length == 0) return Read();
        for (int i = 1; i < s.Length; i++) // surrogate pair: the second char becomes the next event
            _pending.Enqueue(new KeyInput(MakeKey(s[i], (ConsoleKey)0)));
        char c = s[0];
        // Uppercase non-Latin letters report Shift, like Latin capitals.
        return new KeyInput(MakeKey(c, (ConsoleKey)0, shift: char.IsUpper(c)));
    }

    private InputEvent ReadEscape()
    {
        int b2 = ReadByte(40);
        if (b2 < 0) return new KeyInput(MakeKey('\u001b', ConsoleKey.Escape)); // a lone Esc

        if (b2 == '[')
        {
            var p = new StringBuilder();
            while (true)
            {
                int c = ReadByte(40);
                if (c < 0) return Read(); // truncated sequence
                if (c is >= 0x40 and <= 0x7E)
                    return ParseCsi(p.ToString(), (char)c) ?? Read();
                p.Append((char)c);
                if (p.Length > 32) return Read();
            }
        }
        if (b2 == 'O') // SS3: F1-F4, arrows, Home/End
        {
            int c = ReadByte(40);
            if (c < 0) return Read();
            var key = (char)c switch
            {
                'P' => ConsoleKey.F1, 'Q' => ConsoleKey.F2, 'R' => ConsoleKey.F3, 'S' => ConsoleKey.F4,
                'A' => ConsoleKey.UpArrow, 'B' => ConsoleKey.DownArrow,
                'C' => ConsoleKey.RightArrow, 'D' => ConsoleKey.LeftArrow,
                'H' => ConsoleKey.Home, 'F' => ConsoleKey.End,
                _ => (ConsoleKey)0,
            };
            return key == (ConsoleKey)0 ? Read() : new KeyInput(MakeKey('\0', key));
        }
        // Alt+Backspace (Esc then DEL) — Undo in the editor, as on Windows.
        if (b2 is 0x7F or 0x08)
            return new KeyInput(MakeKey('\b', ConsoleKey.Backspace, alt: true));

        // Alt+character
        if (b2 >= 0x20 && b2 < 0x7F)
        {
            var k = CharKey((char)b2);
            return new KeyInput(MakeKey(k.KeyChar, k.Key, k.Modifiers.HasFlag(ConsoleModifiers.Shift), alt: true));
        }
        return Read();
    }

    /// <summary>Parse a CSI: parameters and the final byte. null — an unknown sequence.</summary>
    internal static InputEvent? ParseCsi(string p, char final)
    {
        // SGR mouse: ESC [< b;x;y M (press/wheel) or m (release)
        if (p.StartsWith('<'))
        {
            var parts = p[1..].Split(';');
            if (parts.Length == 3
                && int.TryParse(parts[0], out int btn)
                && int.TryParse(parts[1], out int mx)
                && int.TryParse(parts[2], out int my)
                && final is 'M' or 'm')
                return new MouseInput(btn, Math.Max(0, mx - 1), Math.Max(0, my - 1), final == 'M');
            return null;
        }

        var ps = p.Split(';', StringSplitOptions.RemoveEmptyEntries);
        int Arg(int i, int def) => ps.Length > i && int.TryParse(ps[i], out int v) ? v : def;
        int modCode = ps.Length > 1 && int.TryParse(ps[1].Split(':')[0], out int mc) ? mc : 1;
        var (shift, alt, ctrl) = DecodeModifiers(modCode);

        // CSI u (kitty keyboard): ESC [ code ; modifier u — how terminals
        // with the progressive keyboard protocol send Ctrl+letters.
        if (final == 'u')
        {
            bool sh = shift, al = alt, ct = ctrl;
            int cp = Arg(0, 0);
            if (cp is >= 'a' and <= 'z')
            {
                char ch = ct || al ? (char)cp : sh ? char.ToUpperInvariant((char)cp) : (char)cp;
                return new KeyInput(MakeKey(ch, ConsoleKey.A + (cp - 'a'), sh, al, ct));
            }
            return cp switch
            {
                27 => new KeyInput(MakeKey('\u001b', ConsoleKey.Escape, sh, al, ct)),
                13 => new KeyInput(MakeKey('\r', ConsoleKey.Enter, sh, al, ct)),
                9 => new KeyInput(MakeKey('\t', ConsoleKey.Tab, sh, al, ct)),
                127 => new KeyInput(MakeKey('\b', ConsoleKey.Backspace, sh, al, ct)),
                32 => new KeyInput(MakeKey(' ', ConsoleKey.Spacebar, sh, al, ct)),
                _ => null,
            };
        }
        ConsoleKey key;
        if (final == '~')
        {
            key = Arg(0, 0) switch
            {
                1 or 7 => ConsoleKey.Home,
                2 => ConsoleKey.Insert,
                3 => ConsoleKey.Delete,
                4 or 8 => ConsoleKey.End,
                5 => ConsoleKey.PageUp,
                6 => ConsoleKey.PageDown,
                11 => ConsoleKey.F1, 12 => ConsoleKey.F2, 13 => ConsoleKey.F3, 14 => ConsoleKey.F4,
                15 => ConsoleKey.F5, 17 => ConsoleKey.F6, 18 => ConsoleKey.F7, 19 => ConsoleKey.F8,
                20 => ConsoleKey.F9, 21 => ConsoleKey.F10, 23 => ConsoleKey.F11, 24 => ConsoleKey.F12,
                _ => (ConsoleKey)0,
            };
        }
        else
        {
            key = final switch
            {
                'A' => ConsoleKey.UpArrow,
                'B' => ConsoleKey.DownArrow,
                'C' => ConsoleKey.RightArrow,
                'D' => ConsoleKey.LeftArrow,
                'H' => ConsoleKey.Home,
                'F' => ConsoleKey.End,
                'Z' => ConsoleKey.Tab, // Shift+Tab
                _ => (ConsoleKey)0,
            };
            if (final == 'Z') shift = true;
        }

        return key == (ConsoleKey)0 ? null
            : new KeyInput(MakeKey(key == ConsoleKey.Tab ? '\t' : '\0', key, shift, alt, ctrl));
    }

    /// <summary>CSI modifier code (1 + bitmask: 1=Shift, 2=Alt, 4=Ctrl) as three flags.</summary>
    private static (bool Shift, bool Alt, bool Ctrl) DecodeModifiers(int modCode) =>
        (modCode is 2 or 4 or 6 or 8, modCode is 3 or 4 or 7 or 8, modCode is 5 or 6 or 7 or 8);
}
