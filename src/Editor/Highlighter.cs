/// <summary>What a run of characters is, for its colour: vim's highlight groups come down to these (see VimSyntax).</summary>
public enum TokenKind : byte { Normal, Keyword, Str, Comment, Number, Preproc, Added, Changed, Removed }

/// <summary>A highlighted range of the source line (no text is copied).</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

public static class Highlighter
{
    public static ConsoleColor ColorFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => ConsoleColor.Cyan,
        TokenKind.Str => ConsoleColor.Yellow,
        TokenKind.Comment => ConsoleColor.DarkGreen,
        TokenKind.Number => ConsoleColor.Magenta,
        TokenKind.Preproc => ConsoleColor.DarkCyan,
        TokenKind.Added => ConsoleColor.Green, // the colours of the gutter's change marks
        TokenKind.Changed => ConsoleColor.Yellow,
        TokenKind.Removed => ConsoleColor.Red,
        _ => ConsoleColor.Gray,
    };
}
