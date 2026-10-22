using System.Globalization;

/// <summary>
/// How text occupies terminal cells: a tab runs to the next multiple of
/// TabWidth, East Asian wide characters and emoji take two cells, combining
/// marks and joiners take none. Control characters would be executed by the
/// terminal, and characters that show nothing could hide in the text, so both
/// are drawn as visible stand-ins of one cell.
/// Columns in this class are cells; indexes are UTF-16 positions in a string.
/// </summary>
internal static class Cells
{
    public const int TabWidth = 4;

    // East Asian Wide / Fullwidth code point ranges (pairs, inclusive), from
    // the Unicode 13 data with the emoji blocks widened for later versions.
    private static readonly int[] Wide =
    {
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0,
        0x23F3, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F,
        0x2693, 0x2693, 0x26A1, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5,
        0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA, 0x26F2, 0x26F3, 0x26F5, 0x26F5,
        0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B, 0x2728, 0x2728,
        0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55,
        0x2E80, 0x303E, 0x3041, 0x3247, 0x3250, 0x4DBF, 0x4E00, 0xA4C6, 0xA960, 0xA97C,
        0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6B, 0xFF01, 0xFF60,
        0xFFE0, 0xFFE6, 0x16FE0, 0x1B2FB, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E,
        0x1F191, 0x1F19A, 0x1F200, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C, 0x1F37E, 0x1F393,
        0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4, 0x1F3F8, 0x1F43E,
        0x1F440, 0x1F440, 0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E, 0x1F550, 0x1F567,
        0x1F57A, 0x1F57A, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F, 0x1F680, 0x1F6C5,
        0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6DF, 0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC,
        0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1F9FF,
        0x1FA70, 0x1FAFF, 0x20000, 0x2FFFD, 0x30000, 0x3FFFD,
    };

    /// <summary>
    /// Characters that show nothing yet change what is shown: bidi controls (a bidi-aware terminal
    /// reorders the line around them: "Trojan Source"), zero-width spaces and word joiners, a BOM inside
    /// the text, the soft hyphen, line and paragraph separators. ZWJ, ZWNJ, variation selectors and
    /// emoji tags are not among them: they join what they join.
    /// </summary>
    public static bool Hidden(int cp) => cp is 0xAD or 0x61C or 0x180E or 0x200B or 0x200E or 0x200F
        or (>= 0x2028 and <= 0x202E) or (>= 0x2060 and <= 0x2064) or (>= 0x2066 and <= 0x206F) or 0xFEFF
        or (>= 0xFFF9 and <= 0xFFFB);

    /// <summary>Cells a code point takes: 0, 1 or 2 (tabs are measured by the callers).</summary>
    public static int Width(int cp)
    {
        if (cp < 0x300) return 1; // ASCII and Latin-1; controls are drawn as one-cell stand-ins
        if (Hidden(cp)) return 1; // drawn as a stand-in too
        if (cp is >= 0x1160 and <= 0x11FF) return 0; // Hangul medial/final jamo join the syllable
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;
        if (cp < 0x1100) return 1;
        int lo = 0, hi = Wide.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < Wide[2 * mid]) hi = mid - 1;
            else if (cp > Wide[2 * mid + 1]) lo = mid + 1;
            else return 2;
        }
        return 1;
    }

    /// <summary>
    /// Cells of the character at s[i] when it starts at display column col
    /// (a surrogate pair counts once; len is its UTF-16 length).
    /// </summary>
    public static int WidthAt(ReadOnlySpan<char> s, int i, int col, out int len)
    {
        char c = s[i];
        len = 1;
        if (c < 0x300) return c == '\t' ? TabWidth - col % TabWidth : 1;
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            len = 2;
            return Width(char.ConvertToUtf32(c, s[i + 1]));
        }
        return char.IsSurrogate(c) ? 1 : Width(c);
    }

    /// <summary>
    /// Length of the leading run of plain one-cell text (printable, below U+0300).
    /// One scan that stops at the first other character, so walking a line run
    /// by run stays linear however many tabs it has.
    /// </summary>
    private static int SimpleRun(ReadOnlySpan<char> s)
    {
        int k = s.IndexOfAnyExceptInRange(' ', '\u02FF');
        return k < 0 ? s.Length : k;
    }

    /// <summary>Display column where s[index] starts (tabs expand from column 0 of s).</summary>
    public static int ColumnOf(ReadOnlySpan<char> s, int index)
    {
        int end = Math.Clamp(index, 0, s.Length), col = 0, i = 0;
        while (i < end)
        {
            int run = Math.Min(SimpleRun(s[i..end]), end - i);
            col += run;
            i += run;
            if (i >= end) break;
            col += WidthAt(s, i, col, out int len);
            i += len;
        }
        return col;
    }

    /// <summary>Total display width of s.</summary>
    public static int WidthOf(ReadOnlySpan<char> s) => ColumnOf(s, s.Length);

    /// <summary>
    /// Index of the character drawn at display column col (the right half of
    /// a wide character or a tab's tail gives that character); past the end — s.Length.
    /// </summary>
    public static int IndexAt(ReadOnlySpan<char> s, int col)
    {
        if (col <= 0) return 0;
        int c = 0, i = 0;
        while (i < s.Length)
        {
            int run = SimpleRun(s[i..]);
            if (col < c + run) return i + (col - c);
            c += run;
            i += run;
            if (i >= s.Length) break;
            int w = WidthAt(s, i, c, out int len);
            if (col < c + w) return i;
            c += w;
            i += len;
        }
        return s.Length;
    }

    /// <summary>What the terminal is given for c: control characters and hidden ones become visible stand-ins.</summary>
    public static char Visible(char c) => c switch
    {
        < ' ' => (char)(0x2400 + c), // ␀ … ␟ (control pictures)
        '\x7F' => '\u2421', // ␡
        >= '\x80' and <= '\x9F' => '\uFFFD',
        _ when Hidden(c) => '\uFFFD',
        _ => c,
    };

    /// <summary>Start of the text element (grapheme) after the one at index i.</summary>
    public static int NextBoundary(string s, int i) =>
        i >= s.Length ? s.Length : i + Math.Max(1, StringInfo.GetNextTextElementLength(s, i));

    /// <summary>Start of the text element (grapheme) that contains index i; i itself when it is a boundary.</summary>
    public static int FloorBoundary(string s, int i)
    {
        if (i <= 0) return 0;
        if (i >= s.Length) return s.Length;
        // Elements are short: scan forward from a little before i instead of
        // from the line start — but from outside any run whose pairing depends
        // on where it started (flags, marks, joined emoji).
        int p = Math.Max(0, i - 64);
        while (p > 0 && JoinsBack(s, p)) p--;
        while (true)
        {
            int next = p + Math.Max(1, StringInfo.GetNextTextElementLength(s, p));
            if (next > i) return p;
            p = next;
        }
    }

    /// <summary>Whether the character at p may belong to the same text element as the one before it.</summary>
    private static bool JoinsBack(string s, int p)
    {
        char c = s[p];
        if (char.IsLowSurrogate(c) || c == '\u200D' || s[p - 1] == '\u200D') return true;
        if (c == '\uD83C' && p + 1 < s.Length && s[p + 1] is >= '\uDDE6' and <= '\uDDFF') return true; // a flag letter
        return CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;
    }

    /// <summary>Start of the text element (grapheme) before index i.</summary>
    public static int PrevBoundary(string s, int i) => i <= 0 ? 0 : FloorBoundary(s, Math.Min(i, s.Length) - 1);

    /// <summary>i itself when it is a boundary, else the end of the text element (grapheme) that contains it.</summary>
    public static int CeilBoundary(string s, int i) => FloorBoundary(s, i) is var f && f < i ? NextBoundary(s, f) : i;

    /// <summary>Characters as the cursor steps over them: an emoji, or a letter with its marks, counts once.</summary>
    public static long CharCount(ReadOnlySpan<char> s)
    {
        long n = 0;
        while (s.Length > 0)
        {
            // Below U+0300 every character stands alone, unless a mark after it joins it.
            int other = s.IndexOfAnyInRange('\u0300', '\uFFFF');
            if (other < 0) return n + s.Length;
            if (other > 1) { n += other - 1; s = s[(other - 1)..]; }
            n++;
            s = s[Math.Max(1, StringInfo.GetNextTextElementLength(s))..];
        }
        return n;
    }
}
