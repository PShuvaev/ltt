using System.Runtime.InteropServices;
using System.Text;

/// <summary>Input event: a key, the mouse, a terminal paste, or the terminal going away.</summary>
internal abstract record InputEvent;

internal sealed record KeyInput(ConsoleKeyInfo Key) : InputEvent;

/// <summary>Text pasted through the terminal (bracketed paste): inserted as-is, as one edit.</summary>
internal sealed record PasteInput(string Text) : InputEvent;

/// <summary>The terminal is gone (hangup / end of input): unsaved work must be secured now.</summary>
internal sealed record HangupInput : InputEvent;

/// <summary>
/// Mouse in the SGR protocol (ESC [&lt; b ; x ; y M/m). Coordinates are already 0-based.
/// Button: 0/1/2 — left/middle/right, 64 — wheel up, 65 — wheel down,
/// 66/67 — wheel left/right (upper bits may carry modifiers, hence IsWheel/CleanButton).
/// </summary>
internal sealed record MouseInput(int Button, int X, int Y, bool Pressed) : InputEvent
{
    public bool IsWheel => (Button & 64) != 0;
    public bool WheelDown => IsWheel && (Button & 1) != 0;
    /// <summary>Wheel left/right — a trackpad swipe or tilt wheel; WheelDown then means right.</summary>
    public bool IsHorizontalWheel => IsWheel && (Button & 2) != 0;
    /// <summary>Shift held (bit 4): Shift+wheel scrolls sideways.</summary>
    public bool HasShift => (Button & 4) != 0;
    /// <summary>Motion with a button held (bit 32) — drag selection.</summary>
    public bool IsMotion => (Button & 32) != 0;
    public bool IsLeftDrag => IsMotion && (Button & 3) == 0;
    public bool IsLeftClick => Pressed && !IsWheel && (Button & 3) == 0 && (Button & 32) == 0;
    public bool IsMiddleClick => Pressed && !IsWheel && (Button & 3) == 1 && (Button & 32) == 0;
    public bool IsRightClick => Pressed && !IsWheel && (Button & 3) == 2 && (Button & 32) == 0;
}

internal interface ITerminalInput : IDisposable
{
    /// <summary>Read an event but wait no longer than timeoutMs: false — nothing arrived in time.</summary>
    bool TryRead(int timeoutMs, out InputEvent? ev);

    /// <summary>The terminal settings again, after the process was stopped and continued.</summary>
    void Resume();
}

/// <summary>Fallback without the mouse (Windows, or when raw mode is unavailable).</summary>
internal sealed class ConsoleInput : ITerminalInput
{
    private static InputEvent Read()
    {
        var k = Console.ReadKey(intercept: true);
        // Windows reports AltGr as Ctrl+Alt: a character typed with it is text, not a shortcut
        // (Polish AltGr+S is ś, German AltGr+Q is @ — neither may save or quit).
        if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Modifiers.HasFlag(ConsoleModifiers.Alt)
            && k.KeyChar >= ' ' && k.KeyChar != '\x7F')
            k = new ConsoleKeyInfo(k.KeyChar, (ConsoleKey)0, k.Modifiers.HasFlag(ConsoleModifiers.Shift), false, false);
        return new KeyInput(k);
    }

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

    public void Resume() { }

    public void Dispose() { }
}

/// <summary>
/// Custom parsing of a Unix terminal input stream: raw mode via termios
/// (which also stops the XOFF/XON driver from swallowing Ctrl+S/Ctrl+Q),
/// key escape sequences, the kitty keyboard protocol, bracketed paste and SGR mouse.
/// </summary>
internal sealed class RawTerminalInput : ITerminalInput
{
    private const int StdinFd = 0;
    private const int TcsaNow = 0;
    private const int TermiosSize = 512; // generously sized for macOS and Linux

    private readonly IntPtr _termios = Marshal.AllocHGlobal(TermiosSize);
    private readonly byte[] _savedTermios = new byte[TermiosSize];
    private readonly byte[] _buf = new byte[4096]; // bytes read but not yet parsed: _buf[_bufPos.._bufLen)
    private int _bufPos, _bufLen;
    private readonly Queue<InputEvent> _pending = new();
    private readonly Stack<int> _unread = new(); // bytes read ahead and handed back
    private readonly int _savedStdinDup = -1; // original stdin when /dev/tty was substituted
    private readonly bool _noTerminal; // parsing given bytes (tests)
    private int _disposed;

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
    // By reference: an array of structs is only copied in, and revents would never come back (POLLHUP unseen).
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fd, nuint nfds, int timeoutMs);

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
    }

    /// <summary>Parse the given bytes instead of a terminal (tests).</summary>
    internal RawTerminalInput(byte[] bytes)
    {
        _noTerminal = true;
        bytes.CopyTo(_buf, 0);
        _bufLen = bytes.Length;
    }

    /// <summary>Raw mode again: whatever ran while the process was stopped may have changed the settings.</summary>
    public void Resume()
    {
        if (!_noTerminal && Volatile.Read(ref _disposed) == 0) tcsetattr(StdinFd, TcsaNow, _termios);
    }

    /// <summary>Restore the terminal. Also called from a signal handler, so only the first call acts.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _noTerminal) return;
        Marshal.Copy(_savedTermios, 0, _termios, TermiosSize);
        tcsetattr(StdinFd, TcsaNow, _termios);
        if (_savedStdinDup >= 0) { dup2(_savedStdinDup, StdinFd); close(_savedStdinDup); }
        Marshal.FreeHGlobal(_termios);
    }

    // -2 = EOF, -1 = timeout, otherwise a byte
    private int ReadByte(int timeoutMs)
    {
        if (_unread.Count > 0) return _unread.Pop();
        if (_bufPos < _bufLen) return _buf[_bufPos++];
        if (_noTerminal) return -1;
        var pfd = new PollFd { Fd = StdinFd, Events = 1 /* POLLIN */ };
        long deadline = timeoutMs < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int wait = timeoutMs < 0 ? -1 : (int)Math.Max(0, deadline - Environment.TickCount64);
            int ready = poll(ref pfd, 1, wait);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
                return -2;
            }
            if (ready > 0)
            {
                nint n = read(StdinFd, _buf, (nuint)_buf.Length); // whatever has arrived: a paste comes in one go
                if (n > 0)
                {
                    _bufLen = (int)n;
                    _bufPos = 1;
                    return _buf[0];
                }
                if (n == 0 && (pfd.Revents & 0x18) != 0) return -2; // POLLHUP | POLLERR — the terminal is gone
                // Spurious wakeup or EAGAIN (see the ctor note): fall through to the deadline check.
            }
            if (Environment.TickCount64 >= deadline) return -1;
            Thread.Sleep(2); // pace retries when poll() cannot really wait (POLLNVAL case)
        }
    }

    private static ConsoleKeyInfo MakeKey(char ch, ConsoleKey key, bool shift = false, bool alt = false, bool ctrl = false) =>
        new(ch, key, shift, alt, ctrl);

    /// <summary>
    /// The next event within timeoutMs. Bytes that mean nothing (a stray
    /// control byte, an unknown sequence) are skipped without blocking past
    /// the deadline, so the main loop keeps redrawing and watching.
    /// </summary>
    public bool TryRead(int timeoutMs, out InputEvent? ev)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (_pending.Count > 0) { ev = _pending.Dequeue(); return true; }
            int b = ReadByte((int)Math.Max(0, deadline - Environment.TickCount64));
            if (b == -1) { ev = null; return false; } // timeout — the main loop has other things to do
            if (b == -2) { ev = new HangupInput(); return true; }
            ev = DispatchFirstByte(b);
            if (ev != null) return true;
        }
    }

    private InputEvent? DispatchFirstByte(int b)
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
        if (b < 0x20) return null; // other control bytes (Ctrl+Space, Ctrl+\ …) mean nothing here
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

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// A character from its UTF-8 lead byte. A broken sequence is dropped;
    /// a byte that cannot continue it is left for the next event (a key
    /// typed right after), never swallowed.
    /// </summary>
    private InputEvent? ReadUtf8(int lead, bool alt = false)
    {
        int len = lead switch { >= 0xC2 and <= 0xDF => 2, >= 0xE0 and <= 0xEF => 3, >= 0xF0 and <= 0xF4 => 4, _ => 0 };
        if (len == 0) return null; // a stray continuation byte, or never valid in UTF-8
        var bytes = new byte[len];
        bytes[0] = (byte)lead;
        for (int i = 1; i < len; i++)
        {
            int cont = ReadByte(50);
            if (cont is < 0x80 or > 0xBF)
            {
                if (cont >= 0) _unread.Push(cont);
                return null;
            }
            bytes[i] = (byte)cont;
        }
        string s;
        try { s = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; } // overlong, or an encoded surrogate
        for (int i = 1; i < s.Length; i++) // surrogate pair: the second char becomes the next event
            _pending.Enqueue(new KeyInput(MakeKey(s[i], (ConsoleKey)0, alt: alt)));
        char c = s[0];
        // Uppercase non-Latin letters report Shift, like Latin capitals.
        return new KeyInput(MakeKey(c, (ConsoleKey)0, shift: char.IsUpper(c), alt: alt));
    }

    private InputEvent? ReadEscape()
    {
        int b2 = ReadByte(40);
        if (b2 < 0) return new KeyInput(MakeKey('\u001b', ConsoleKey.Escape)); // a lone Esc

        if (b2 == 0x1B)
        {
            // Esc Esc: either Alt + an escape sequence (how meta-mode terminals send
            // Option+arrows), or Esc pressed twice — never stray text.
            int b3 = ReadByte(40);
            if (b3 is '[' or 'O')
            {
                _unread.Push(b3);
                var inner = ReadEscapeAfterEsc();
                if (inner is KeyInput { Key: var k })
                    return new KeyInput(MakeKey(k.KeyChar, k.Key, k.Modifiers.HasFlag(ConsoleModifiers.Shift),
                        alt: true, k.Modifiers.HasFlag(ConsoleModifiers.Control)));
                // A mouse report, a paste or a terminal reply right behind it (Esc pressed while a
                // trackpad still scrolls): the Esc was a key of its own.
                if (inner != null) _pending.Enqueue(inner);
                return new KeyInput(MakeKey('\u001b', ConsoleKey.Escape));
            }
            if (b3 >= 0) _unread.Push(b3);
            _unread.Push(0x1B);
            return new KeyInput(MakeKey('\u001b', ConsoleKey.Escape));
        }
        _unread.Push(b2);
        return ReadEscapeAfterEsc();
    }

    /// <summary>The rest of an escape sequence after its ESC.</summary>
    private InputEvent? ReadEscapeAfterEsc()
    {
        int b2 = ReadByte(40);
        if (b2 < 0) return new KeyInput(MakeKey('\u001b', ConsoleKey.Escape));
        if (b2 == '[')
        {
            var p = new StringBuilder();
            while (true)
            {
                int c = ReadByte(40);
                if (c < 0) return null; // truncated sequence
                if (c is < 0x20 or > 0x7E)
                {
                    _unread.Push(c); // not part of a sequence: a key typed after a cut-off one
                    return null;
                }
                if (c == '[' && p.Length == 0) // the Linux console's F1-F5: ESC [ [ A..E
                {
                    int f = ReadByte(40);
                    if (f is >= 'A' and <= 'E') return new KeyInput(MakeKey('\0', ConsoleKey.F1 + (f - 'A')));
                    if (f >= 0) _unread.Push(f);
                    return null;
                }
                // rxvt ends Shift+key with '$', elsewhere a byte in the middle of a sequence: ESC [ 7 $ is Shift+Home.
                if (c == '$' && p.Length > 0 && p.ToString().All(char.IsAsciiDigit)) return ParseCsi(p.ToString(), '$');
                if (c is >= 0x40 and <= 0x7E)
                {
                    if (p.Length > 256) return null; // overlong: dropped whole, never typed
                    string ps = p.ToString();
                    if (c == '~' && ps == "200") return ReadPaste();
                    if (c == 'M' && ps.Length == 0) return ReadX10Mouse();
                    if (c == 'u' && !ps.StartsWith('<'))
                    {
                        var events = ParseCsiU(ps);
                        if (events.Count == 0) return null;
                        for (int i = 1; i < events.Count; i++) _pending.Enqueue(events[i]);
                        return events[0];
                    }
                    return ParseCsi(ps, (char)c);
                }
                if (p.Length <= 256) p.Append((char)c);
            }
        }
        if (b2 == 'O') // SS3: F1-F4, arrows, Home/End, and the keypad in application mode
        {
            // Konsole and old xterm put a modifier before the final byte: ESC O 2 P is Shift+F1, ESC O 5 A Ctrl+Up.
            var mods = new StringBuilder();
            int c;
            while ((c = ReadByte(40)) is >= '0' and <= '9' or ';' && mods.Length < 16) mods.Append((char)c);
            if (c < 0) return null;
            var ev = ParseSs3((char)c);
            if (mods.Length == 0 || ev is not KeyInput { Key: var k }) return ev;
            string m = mods.ToString();
            var (sh, al, ct, _) = DecodeModifiers(int.TryParse(m[(m.LastIndexOf(';') + 1)..], out int code) ? code : 1);
            return new KeyInput(MakeKey(k.KeyChar, k.Key, sh || k.Modifiers.HasFlag(ConsoleModifiers.Shift), al,
                ct || k.Modifiers.HasFlag(ConsoleModifiers.Control)));
        }
        // OSC, DCS, APC, PM, SOS: a terminal's reply to someone's query (a colour, a version), never typed.
        if (b2 is ']' or 'P' or '_' or '^' or 'X')
        {
            SkipString();
            return null;
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
        if (b2 >= 0x80) return ReadUtf8(b2, alt: true); // Alt+ö, Alt+ф: the whole character
        return null;
    }

    /// <summary>
    /// Skip a string sequence up to its end: BEL or ST (ESC \), however long (an OSC 52 answer carries a
    /// whole clipboard); a pause in the input ends it too.
    /// </summary>
    private void SkipString()
    {
        for (int n = 0; n < 1 << 26; n++)
        {
            int b = ReadByte(40);
            if (b < 0 || b == 0x07) return;
            if (b != 0x1B) continue;
            int c = ReadByte(40);
            if (c >= 0 && c != '\\')
            {
                _unread.Push(c); // not ST: another sequence starts here
                _unread.Push(0x1B);
            }
            return;
        }
    }

    /// <summary>A mouse report in the old X10 form (ESC [ M and three bytes), from terminals without SGR reports.</summary>
    private InputEvent? ReadX10Mouse()
    {
        int b = ReadByte(40), x = ReadByte(40), y = ReadByte(40);
        if (b < 32 || x < 33 || y < 33) return null;
        b -= 32;
        bool release = (b & 3) == 3 && (b & 64) == 0; // X10 does not say which button went up
        int button = release ? b & ~3 : b;
        return button < 128 ? new MouseInput(button, x - 33, y - 33, !release) : null;
    }

    /// <summary>SS3 sequences: F1-F4, arrows (rxvt's a..d with Ctrl), Home/End and the application-mode keypad.</summary>
    internal static InputEvent? ParseSs3(char c)
    {
        ConsoleKey key = c switch
        {
            'P' => ConsoleKey.F1, 'Q' => ConsoleKey.F2, 'R' => ConsoleKey.F3, 'S' => ConsoleKey.F4,
            'A' or 'a' => ConsoleKey.UpArrow, 'B' or 'b' => ConsoleKey.DownArrow,
            'C' or 'c' => ConsoleKey.RightArrow, 'D' or 'd' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home, 'F' => ConsoleKey.End,
            'M' => ConsoleKey.Enter, 'I' => ConsoleKey.Tab,
            _ => (ConsoleKey)0,
        };
        if (key != (ConsoleKey)0)
            return new KeyInput(MakeKey(key switch { ConsoleKey.Enter => '\r', ConsoleKey.Tab => '\t', _ => '\0' }, key,
                ctrl: c is >= 'a' and <= 'd'));
        // Keypad digits and operators: ESC O p..y → 0..9, j k l m n o X → * + , - . / =, and its space
        char typed = c switch
        {
            >= 'p' and <= 'y' => (char)('0' + (c - 'p')),
            'j' => '*', 'k' => '+', 'l' => ',', 'm' => '-', 'n' => '.', 'o' => '/', 'X' => '=', ' ' => ' ',
            _ => '\0',
        };
        return typed == '\0' ? null : new KeyInput(CharKey(typed));
    }

    /// <summary>Bracketed paste: everything up to ESC [201~ is the pasted text.</summary>
    private InputEvent? ReadPaste()
    {
        var bytes = new List<byte>(256);
        ReadOnlySpan<byte> end = "\x1b[201~"u8;
        while (true)
        {
            int b = ReadByte(2000);
            if (b < 0) break; // the terminal stopped mid-paste: keep what arrived
            bytes.Add((byte)b);
            if (bytes.Count >= end.Length && CollectionsMarshal.AsSpan(bytes)[^end.Length..].SequenceEqual(end))
            {
                bytes.RemoveRange(bytes.Count - end.Length, end.Length);
                break;
            }
        }
        string text = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
        return text.Length == 0 ? null : new PasteInput(text);
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
                && final is 'M' or 'm'
                && btn < 128) // buttons 8+ (back/forward…) are not clicks
                return new MouseInput(btn, Math.Max(0, mx - 1), Math.Max(0, my - 1), final == 'M');
            return null;
        }

        int Arg(int i, int def) => int.TryParse(Field(p, i), out int v) ? v : def;
        var (shift, alt, ctrl, super) = DecodeModifiers(SubInt(Field(p, 1), 0));
        if (SubInt(Field(p, 1), 1) == 3) return null; // a key going up (kitty event type 3): nothing happens

        // CSI u (kitty keyboard protocol) has its own field grammar.
        if (final == 'u')
        {
            var evs = ParseCsiU(p);
            return evs.Count > 0 ? evs[0] : null;
        }
        ConsoleKey key;
        if (final is '~' or '^' or '$' or '@')
        {
            // xterm's modifyOtherKeys (tmux's extended keys too): CSI 27 ; modifiers ; key ~ is CSI key ; modifiers u.
            if (final == '~' && Arg(0, 0) == 27 && !Field(p, 2).IsEmpty)
            {
                var evs = int.TryParse(Field(p, 2), out int code) ? ParseCsiU($"{code};{Field(p, 1)}") : new();
                return evs.Count > 0 ? evs[0] : null;
            }
            // rxvt's own finals: ^ is Ctrl, $ is Shift, @ is both.
            if (final is '^' or '@') ctrl = true;
            if (final is '$' or '@') shift = true;
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
                'a' => ConsoleKey.UpArrow, 'b' => ConsoleKey.DownArrow, // rxvt's Shift+arrows
                'c' => ConsoleKey.RightArrow, 'd' => ConsoleKey.LeftArrow,
                _ => (ConsoleKey)0,
            };
            if (final is 'Z' or 'a' or 'b' or 'c' or 'd') shift = true;
        }
        if (key == (ConsoleKey)0) return null;
        // Cmd/Super+arrows move like on a Mac: to the line start/end, the document start/end.
        if (super)
        {
            (key, ctrl) = key switch
            {
                ConsoleKey.LeftArrow => (ConsoleKey.Home, false),
                ConsoleKey.RightArrow => (ConsoleKey.End, false),
                ConsoleKey.UpArrow => (ConsoleKey.Home, true),
                ConsoleKey.DownArrow => (ConsoleKey.End, true),
                _ => (key, ctrl),
            };
        }
        return new KeyInput(MakeKey(key == ConsoleKey.Tab ? '\t' : '\0', key, shift, alt, ctrl));
    }

    /// <summary>
    /// CSI u (kitty): `cp[:shifted[:base]] ; mods[:event] ; text... u`.
    /// A shortcut is the letter the layout produces when that is Latin
    /// (QWERTY, AZERTY, QWERTZ, Dvorak…); only for other scripts does the
    /// base-layout key — the physical US position — stand in, so Ctrl+F
    /// still works with a Cyrillic layout. Cmd/Super acts as Ctrl.
    /// The typed character comes from the associated text, or from the
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
        var (sh, al, ct, su) = DecodeModifiers(modCode == 0 ? 1 : modCode);
        // Hyper and Meta (16, 32) on their own: no shortcut of ltt's uses them, and nothing is typed.
        int bits = Math.Max(0, modCode - 1);
        if ((bits & 48) != 0 && (bits & 14) == 0) return none;
        if (su) ct = true;
        bool latin = cp is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        int id = latin || baseCp <= 0 ? cp : baseCp;

        string text = "";
        var textField = Field(p, 2);
        if (!textField.IsEmpty && !ct && !su)
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
            57417 => ConsoleKey.LeftArrow, 57418 => ConsoleKey.RightArrow, // keypad navigation (NumLock off)
            57419 => ConsoleKey.UpArrow, 57420 => ConsoleKey.DownArrow,
            57421 => ConsoleKey.PageUp, 57422 => ConsoleKey.PageDown,
            57423 => ConsoleKey.Home, 57424 => ConsoleKey.End,
            57425 => ConsoleKey.Insert, 57426 => ConsoleKey.Delete,
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

        char TypedChar() => (sh && shiftedCp > 0 ? shiftedCp : cp) is int tcp and > 0 and <= 0xFFFF
            and not (>= 0xD800 and <= 0xDFFF) ? (char)tcp : '\0';

        // The keypad's digits and operators type even from a terminal that leaves their text out.
        char pad = id switch
        {
            >= 57399 and <= 57408 => (char)('0' + (id - 57399)),
            57409 => '.', 57410 => '/', 57411 => '*', 57412 => '-', 57413 => '+', 57415 => '=', 57416 => ',',
            _ => '\0',
        };
        if (pad != '\0') return new List<InputEvent> { new KeyInput(MakeKey(pad, CharKey(pad).Key, sh, al, ct)) };

        if (key != (ConsoleKey)0)
        {
            char ch = key switch
            {
                ConsoleKey.Escape => '\u001b',
                ConsoleKey.Enter => '\r',
                ConsoleKey.Tab => '\t',
                ConsoleKey.Backspace => '\b',
                ConsoleKey.Spacebar => ' ',
                _ when id >= 57376 => '\0',
                _ => TypedChar(),
            };
            return new List<InputEvent> { new KeyInput(MakeKey(ch, key, sh, al, ct)) };
        }

        // Unmapped key in all-keys mode: a printable character can still be typed.
        if (!ct && !al && cp >= 0x20 && cp <= 0xFFFF && cp is not (>= 0xD800 and <= 0xDFFF or >= 57344 and <= 63743))
            return new List<InputEvent> { new KeyInput(MakeKey(TypedChar(), (ConsoleKey)0, sh, al, ct)) };
        return none;
    }

    /// <summary>CSI modifier code (1 + bitmask: 1=Shift, 2=Alt, 4=Ctrl, 8=Super…) as flags.</summary>
    private static (bool Shift, bool Alt, bool Ctrl, bool Super) DecodeModifiers(int modCode)
    {
        int m = Math.Max(0, modCode - 1);
        return ((m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0);
    }
}
