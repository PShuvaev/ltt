using System.Text;

/// <summary>
/// The frame as a grid of cells. Drawing code puts text into it; Flush
/// compares the grid with what the terminal already shows and sends only
/// the cells that changed, in one write — nothing at all when nothing did.
/// All text passes through Put, which is the single place where it is made
/// safe and measured: control characters become visible stand-ins (a file
/// can never drive the terminal), tabs become spaces, wide characters take
/// two cells and combining marks join the cell before them.
/// </summary>
internal sealed class Screen
{
    private struct Cell : IEquatable<Cell>
    {
        public char Ch, Low;     // the character (Low — second half of a surrogate pair)
        public string? Marks;    // combining marks drawn on top of it, if any
        public byte Fg, Bg;
        public byte Kind;        // 0 — narrow, 1 — wide (two cells), 2 — right half of a wide one

        public readonly bool Equals(Cell o) =>
            Ch == o.Ch && Low == o.Low && Fg == o.Fg && Bg == o.Bg && Kind == o.Kind && Marks == o.Marks;
    }

    private Cell[] _cells = Array.Empty<Cell>(), _shown = Array.Empty<Cell>();
    private bool _clearFirst = true;
    private readonly StringBuilder _out = new(1 << 16);
    private char[] _chars = new char[1 << 16];
    private byte[] _bytes = new byte[1 << 17];
    private static readonly Stream Stdout = Console.OpenStandardOutput();

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Where to show the terminal cursor after the frame; null hides it.</summary>
    public (int X, int Y)? Cursor { get; set; }

    // ConsoleColor (enum order) -> ANSI foreground code; background is +10.
    private static readonly int[] FgAnsi = { 30, 34, 32, 36, 31, 35, 33, 37, 90, 94, 92, 96, 91, 95, 93, 97 };

    /// <summary>
    /// Start a blank frame of the given size (whatever is not drawn shows as
    /// empty); a new size (or Invalidate) repaints the whole terminal.
    /// </summary>
    public void Begin(int width, int height)
    {
        if (width != Width || height != Height)
        {
            Width = Math.Max(0, width);
            Height = Math.Max(0, height);
            _cells = new Cell[Width * Height];
            _shown = new Cell[Width * Height];
            _clearFirst = true;
        }
        Array.Fill(_cells, new Cell { Ch = ' ', Fg = (byte)ConsoleColor.Gray });
        Cursor = null;
    }

    /// <summary>Forget what the terminal shows: the next Flush repaints the whole screen.</summary>
    public void Invalidate() => _clearFirst = true;

    /// <summary>
    /// Put text at (x, y) within `width` cells: clipped there and, unless pad
    /// is false, padded with spaces to fill them. Tabs run to multiples of
    /// Cells.TabWidth counted from tabOrigin cells before x. A combining mark
    /// joins the character before it; one at the start of the text has none,
    /// unless the text goes on from a run drawn just before (joinBefore), and is
    /// not put on whatever stands to its left. Returns the cells the text took;
    /// consumed — how many of its characters fitted.
    /// </summary>
    public int Put(int x, int y, ReadOnlySpan<char> text, ConsoleColor fg, ConsoleColor bg, int width,
        out int consumed, int tabOrigin = 0, bool pad = true, bool joinBefore = false)
    {
        consumed = 0;
        if (y < 0 || y >= Height || x >= Width || width <= 0) return 0;
        if (x < 0) { width += x; x = 0; }
        int end = Math.Min(Width, x + width);
        byte f = (byte)fg, b = (byte)bg;
        int col = x, i = 0;
        while (i < text.Length && col < end)
        {
            char c = text[i];
            if (c == '\t')
            {
                int n = Cells.TabWidth - (col - x + tabOrigin) % Cells.TabWidth;
                for (; n > 0 && col < end; n--) Set(col++, y, ' ', '\0', f, b, 0);
                if (n > 0) break; // cut by the edge
                i++;
                continue;
            }
            int w = Cells.WidthAt(text, i, 0, out int len);
            if (w == 0)
            {
                if (col > x || (joinBefore && col > 0)) AddMarks(col - 1, y, text.Slice(i, len));
                i += len;
                continue;
            }
            if (col + w > end) break; // a wide character that does not fit
            char ch = len == 1 && char.IsSurrogate(c) ? '\uFFFD' : Cells.Visible(c);
            Set(col, y, ch, len == 2 ? text[i + 1] : '\0', f, b, (byte)(w == 2 ? 1 : 0));
            if (w == 2) Set(col + 1, y, ' ', '\0', f, b, 2);
            col += w;
            i += len;
        }
        consumed = i;
        int used = col - x;
        if (pad) while (col < end) Set(col++, y, ' ', '\0', f, b, 0);
        return used;
    }

    /// <summary>Put text clipped and padded to `width` cells (see the full overload).</summary>
    public int Put(int x, int y, ReadOnlySpan<char> text, ConsoleColor fg, ConsoleColor bg, int width) =>
        Put(x, y, text, fg, bg, width, out _);

    private void Set(int x, int y, char ch, char low, byte fg, byte bg, byte kind)
    {
        int i = y * Width + x;
        // Overwriting half of a wide character leaves its other half as a blank.
        if (_cells[i].Kind == 2 && kind != 2 && x > 0) _cells[i - 1] = Blank(_cells[i - 1]);
        if (_cells[i].Kind == 1 && kind != 1 && x + 1 < Width) _cells[i + 1] = Blank(_cells[i + 1]);
        _cells[i] = new Cell { Ch = ch, Low = low, Fg = fg, Bg = bg, Kind = kind };
    }

    private static Cell Blank(Cell c) => new() { Ch = ' ', Fg = c.Fg, Bg = c.Bg };

    // A few marks stack on a character; Zalgo text puts thousands on one, which would only cost time
    // on every frame and flood the terminal. A cell keeps this many UTF-16 units of them.
    private const int MaxMarks = 16;

    private void AddMarks(int x, int y, ReadOnlySpan<char> marks)
    {
        ref var c = ref _cells[y * Width + x];
        if (c.Kind == 2 && x > 0) c = ref _cells[y * Width + x - 1];
        if ((c.Marks?.Length ?? 0) + marks.Length > MaxMarks) return;
        c.Marks = c.Marks == null ? marks.ToString() : c.Marks + marks.ToString();
    }

    /// <summary>Send the changed cells to the terminal in one write.</summary>
    public void Flush()
    {
        var sb = _out;
        sb.Clear();
        // DEC 2026 synchronized output: supporting terminals present the
        // whole frame atomically (no tearing mid-scroll); others ignore it.
        sb.Append("\x1b[?2026h\x1b[?25l");
        if (_clearFirst)
        {
            sb.Append("\x1b[0m\x1b[2J");
            Array.Fill(_shown, new Cell { Ch = '\uFFFF' }); // matches nothing: every cell is sent
            _clearFirst = false;
        }
        int lastFg = -1, lastBg = -1;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            int first = -1, last = -1;
            for (int x = 0; x < Width; x++)
                if (!_cells[row + x].Equals(_shown[row + x]))
                {
                    if (first < 0) first = x;
                    last = x;
                }
            if (first < 0) continue;
            if (_cells[row + first].Kind == 2 && first > 0) first--;           // start at a wide character's left half
            if (_cells[row + last].Kind == 1 && last + 1 < Width) last++;      // and end after its right half
            sb.Append("\x1b[").Append(y + 1).Append(';').Append(first + 1).Append('H');
            for (int x = first; x <= last; x++)
            {
                var c = _cells[row + x];
                if (c.Kind == 2) continue; // drawn by its left half
                if (c.Fg != lastFg || c.Bg != lastBg)
                {
                    sb.Append("\x1b[").Append(FgAnsi[c.Fg & 15]).Append(';').Append(FgAnsi[c.Bg & 15] + 10).Append('m');
                    lastFg = c.Fg;
                    lastBg = c.Bg;
                }
                sb.Append(c.Ch);
                if (c.Low != '\0') sb.Append(c.Low);
                if (c.Marks != null) sb.Append(c.Marks);
            }
            Array.Copy(_cells, row + first, _shown, row + first, last - first + 1);
        }
        if (Cursor is { } cur && cur.X >= 0 && cur.X < Width && cur.Y >= 0 && cur.Y < Height)
            sb.Append("\x1b[").Append(cur.Y + 1).Append(';').Append(cur.X + 1).Append("H\x1b[?25h");
        sb.Append("\x1b[?2026l");
        Write(sb);
    }

    private void Write(StringBuilder sb)
    {
        int len = sb.Length;
        if (_chars.Length < len) _chars = new char[len * 2];
        sb.CopyTo(0, _chars, 0, len); // whole, so a surrogate pair is never split between chunks
        int max = Encoding.UTF8.GetMaxByteCount(len);
        if (_bytes.Length < max) _bytes = new byte[max * 2];
        int n = Encoding.UTF8.GetBytes(_chars, 0, len, _bytes, 0);
        Stdout.Write(_bytes, 0, n);
        Stdout.Flush();
    }
}
