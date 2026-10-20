using System.Runtime.InteropServices;
using System.Text;

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
/// key escape sequences, the kitty keyboard protocol (physical base-layout
/// keys, so shortcuts work on any keyboard layout) and SGR mouse.
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
    private readonly int _savedStdinDup = -1; // original stdin when /dev/tty was substituted
    private bool _disposed;

    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int act, IntPtr termios);
    [DllImport("libc")] private static extern void cfmakeraw(IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc", SetLastError = true)] private static extern int open(byte[] path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup2(int fd, int newFd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    private const int FGetFl = 3;
    private static readonly int ONonBlock = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }
    [DllImport("libc", SetLastError = true)] private static extern int poll(PollFd[] fds, nuint nfds, int timeoutMs);

    public RawTerminalInput()
    {
        if (tcgetattr(StdinFd, _termios) != 0)
            throw new InvalidOperationException("stdin is not a terminal");
        // A tty opened read-only (e.g. stdin redirected from /dev/tty by an
        // installer script) makes macOS poll() report instant POLLNVAL forever,
        // so a blocking read() after such a wakeup would freeze the UI.
        // Substitute our own read-write non-blocking open of the same terminal.
        int flags = fcntl(StdinFd, FGetFl, 0);
        if (flags >= 0 && (flags & 0x3) != 2) // not O_RDWR
        {
            int tty = open("/dev/tty\0"u8.ToArray(), 2 /* O_RDWR */ | ONonBlock);
            if (tty >= 0)
            {
                _savedStdinDup = dup(StdinFd);
                dup2(tty, StdinFd);
                close(tty);
            }
        }
        Marshal.Copy(_termios, _savedTermios, 0, TermiosSize);
        cfmakeraw(_termios);
        tcsetattr(StdinFd, TcsaNow, _termios);

        // Mouse reports: 1000 — clicks and wheel, 1002 — motion with a button held, 1006 — SGR.
        Console.Write("\x1b[?1000h\x1b[?1002h\x1b[?1006h");
        // Kitty keyboard protocol, flags 1|4|8|16: disambiguate, report alternate
        // (shifted/base-layout) keys, report all keys as CSI u, report associated
        // text. The base-layout codepoint identifies the physical PC key on any
        // keyboard layout. Terminals without the protocol simply ignore this.
        Console.Write("\x1b[>29u");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.Write("\x1b[<u\x1b[?1006l\x1b[?1002l\x1b[?1000l");
        Marshal.Copy(_savedTermios, 0, _termios, TermiosSize);
        tcsetattr(StdinFd, TcsaNow, _termios);
        if (_savedStdinDup >= 0) { dup2(_savedStdinDup, StdinFd); close(_savedStdinDup); }
        Marshal.FreeHGlobal(_termios);
    }

    // -2 = EOF, -1 = timeout, otherwise a byte
    private int ReadByte(int timeoutMs)
    {
        var pfd = new[] { new PollFd { Fd = StdinFd, Events = 1 /* POLLIN */ } };
        long deadline = timeoutMs < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int wait = timeoutMs < 0 ? -1 : (int)Math.Max(0, deadline - Environment.TickCount64);
            int ready = poll(pfd, 1, wait);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
                return -2;
            }
            if (ready > 0)
            {
                nint n = read(StdinFd, _one, 1);
                if (n > 0) return _one[0];
                if (n == 0 && (pfd[0].Revents & 0x18) != 0) return -2; // POLLHUP | POLLERR — the terminal is gone
                // Spurious wakeup or EAGAIN (see the ctor note): fall through to the deadline check.
            }
            if (Environment.TickCount64 >= deadline) return -1;
            Thread.Sleep(2); // pace retries when poll() cannot really wait (POLLNVAL case)
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
                {
                    string ps = p.ToString();
                    if (c == 'u' && !ps.StartsWith('<'))
                    {
                        var events = ParseCsiU(ps);
                        if (events.Count == 0) return Read();
                        for (int i = 1; i < events.Count; i++) _pending.Enqueue(events[i]);
                        return events[0];
                    }
                    return ParseCsi(ps, (char)c) ?? Read();
                }
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
        // Alt+Backspace (Esc then DEL) — delete-word in the editor.
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

    /// <summary>The index-th `sep`-separated field of a CSI parameter string (empty when absent).</summary>
    private static ReadOnlySpan<char> Field(ReadOnlySpan<char> s, int index, char sep = ';')
    {
        for (int i = 0; i < index; i++)
        {
            int at = s.IndexOf(sep);
            if (at < 0) return default;
            s = s[(at + 1)..];
        }
        int end = s.IndexOf(sep);
        return end < 0 ? s : s[..end];
    }

    private static int SubInt(ReadOnlySpan<char> field, int index) =>
        int.TryParse(Field(field, index, ':'), out int v) ? v : 0;

    /// <summary>Parse a CSI: parameters and the final byte. null — an unknown sequence.</summary>
    internal static InputEvent? ParseCsi(string p, char final)
    {
        // SGR mouse: ESC [< b;x;y M (press/wheel) or m (release)
        if (p.StartsWith('<'))
        {
            var body = p.AsSpan(1);
            if (int.TryParse(Field(body, 0), out int btn)
                && int.TryParse(Field(body, 1), out int mx)
                && int.TryParse(Field(body, 2), out int my)
                && final is 'M' or 'm')
                return new MouseInput(btn, Math.Max(0, mx - 1), Math.Max(0, my - 1), final == 'M');
            return null;
        }

        int Arg(int i, int def) => int.TryParse(Field(p, i), out int v) ? v : def;
        int modCode = SubInt(Field(p, 1), 0);
        var (shift, alt, ctrl) = DecodeModifiers(modCode);

        // CSI u (kitty keyboard protocol) has its own field grammar.
        if (final == 'u')
        {
            var evs = ParseCsiU(p);
            return evs.Count > 0 ? evs[0] : null;
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

    /// <summary>
    /// CSI u (kitty): `cp[:shifted[:base]] ; mods[:event] ; text... u`.
    /// The physical key identity comes from the base-layout codepoint when the
    /// terminal reports it, so shortcuts like Ctrl+F work on any keyboard
    /// layout; the typed character comes from the associated text, or from the
    /// (shifted) key codepoint itself.
    /// </summary>
    private static List<InputEvent> ParseCsiU(string p)
    {
        var none = new List<InputEvent>();
        // Empty sub-fields are meaningful here: "1040::102" is base-without-shifted.
        var keyField = Field(p, 0);
        var modField = Field(p, 1);
        int cp = SubInt(keyField, 0);
        int shiftedCp = SubInt(keyField, 1);
        int baseCp = SubInt(keyField, 2);
        int modCode = SubInt(modField, 0);
        int eventType = SubInt(modField, 1);
        if (eventType == 3) return none; // key release (not requested, tolerated)
        var (sh, al, ct) = DecodeModifiers(modCode == 0 ? 1 : modCode);
        int id = baseCp > 0 ? baseCp : cp;

        string text = "";
        var textField = Field(p, 2);
        if (!textField.IsEmpty)
        {
            var sb = new StringBuilder();
            var rest = textField;
            while (true)
            {
                int at = rest.IndexOf(':');
                var part = at < 0 ? rest : rest[..at];
                if (int.TryParse(part, out int tcp) && tcp >= 0x20 && tcp <= 0x10FFFF && tcp is not (>= 0xD800 and <= 0xDFFF))
                    sb.Append(char.ConvertFromUtf32(tcp));
                if (at < 0) break;
                rest = rest[(at + 1)..];
            }
            text = sb.ToString();
        }

        ConsoleKey key = id switch
        {
            27 => ConsoleKey.Escape,
            13 => ConsoleKey.Enter,
            9 => ConsoleKey.Tab,
            127 => ConsoleKey.Backspace,
            32 => ConsoleKey.Spacebar,
            57414 => ConsoleKey.Enter, // keypad Enter
            >= 'a' and <= 'z' => ConsoleKey.A + (id - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (id - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (id - '0'),
            >= 57399 and <= 57408 => ConsoleKey.D0 + (id - 57399), // keypad digits
            >= 57376 and <= 57387 => ConsoleKey.F13 + (id - 57376),
            _ => (ConsoleKey)0,
        };

        if (text.Length > 0)
        {
            var evs = new List<InputEvent> { new KeyInput(MakeKey(text[0], key, sh, al, ct)) };
            for (int i = 1; i < text.Length; i++)
                evs.Add(new KeyInput(MakeKey(text[i], (ConsoleKey)0, sh, al, ct)));
            return evs;
        }

        char TypedChar() => (sh && shiftedCp > 0 ? shiftedCp : cp) is int tcp and > 0 and <= 0xFFFF ? (char)tcp : '\0';

        if (key != (ConsoleKey)0)
        {
            char ch = key switch
            {
                ConsoleKey.Escape => '\u001b',
                ConsoleKey.Enter => '\r',
                ConsoleKey.Tab => '\t',
                ConsoleKey.Backspace => '\b',
                ConsoleKey.Spacebar => ' ',
                _ => TypedChar(),
            };
            return new List<InputEvent> { new KeyInput(MakeKey(ch, key, sh, al, ct)) };
        }

        // Unmapped key in all-keys mode: a printable character can still be typed.
        if (!ct && !al && cp >= 0x20 && cp <= 0xFFFF && cp is not (>= 57344 and <= 63743))
            return new List<InputEvent> { new KeyInput(MakeKey(TypedChar(), (ConsoleKey)0, sh, al, ct)) };
        return none;
    }

    /// <summary>CSI modifier code (1 + bitmask: 1=Shift, 2=Alt, 4=Ctrl, 8=Super...) as three flags.</summary>
    private static (bool Shift, bool Alt, bool Ctrl) DecodeModifiers(int modCode)
    {
        int m = Math.Max(0, modCode - 1);
        return ((m & 1) != 0, (m & 2) != 0, (m & 4) != 0);
    }
}
