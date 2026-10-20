public enum TokenKind { Normal, Keyword, Str, Comment, Number, Preproc }

/// <summary>A highlighted range of the source line (no text is copied).</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Simple line-based syntax highlighting with no external dependencies:
/// keywords, strings, comments (//, /* */, #), numbers, preprocessor.
/// Multi-line /* */ is supported via inBlockComment carried between lines.
/// Works on spans and returns ranges, so tokenizing allocates nothing
/// per token — important because it runs on every rendered frame.
/// </summary>
public static class Highlighter
{
    private static readonly HashSet<string> CSharp = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked",
        "class","const","continue","decimal","default","delegate","do","double","else",
        "enum","event","explicit","extern","false","finally","fixed","float","for",
        "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
        "long","namespace","new","null","object","operator","out","override","params",
        "private","protected","public","readonly","record","ref","required","return",
        "sbyte","sealed","short","sizeof","stackalloc","static","string","struct",
        "switch","this","throw","true","try","typeof","uint","ulong","unchecked",
        "unsafe","ushort","using","var","virtual","void","volatile","while","async",
        "await","get","set","init","when","where","yield","nint","nuint","global",
    };

    private static readonly HashSet<string> CLike = new(StringComparer.Ordinal)
    {
        "auto","bool","break","case","catch","char","class","const","constexpr",
        "continue","default","delete","do","double","else","enum","explicit","export",
        "extern","false","final","float","for","friend","goto","if","inline","int",
        "long","mutable","namespace","new","noexcept","nullptr","operator","override",
        "private","protected","public","register","return","short","signed","sizeof",
        "static","struct","switch","template","this","throw","true","try","typedef",
        "typename","union","unsigned","using","virtual","void","volatile","while",
        // Java / JS / TS / Go / Rust — a common set for other C-like languages
        "abstract","async","await","boolean","byte","extends","function","implements",
        "import","instanceof","interface","let","package","super","throws","var",
        "func","go","defer","chan","map","range","select","type","fallthrough",
        "fn","impl","match","mut","pub","trait","where","loop","mod","crate","move",
        "const","of","from","typeof","undefined","null","None","True","False",
    };

    private static readonly HashSet<string> Python = new(StringComparer.Ordinal)
    {
        "and","as","assert","async","await","break","class","continue","def","del",
        "elif","else","except","False","finally","for","from","global","if","import",
        "in","is","lambda","None","nonlocal","not","or","pass","raise","return",
        "True","try","while","with","yield","match","case","print","self",
    };

    public static ConsoleColor ColorFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => ConsoleColor.Cyan,
        TokenKind.Str => ConsoleColor.Yellow,
        TokenKind.Comment => ConsoleColor.DarkGreen,
        TokenKind.Number => ConsoleColor.Magenta,
        TokenKind.Preproc => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Gray,
    };

    private static (HashSet<string> Kw, bool HashComment, bool SlashComment) LangFor(string ext) =>
        ext.ToLowerInvariant() switch
        {
            ".cs" => (CSharp, false, true),
            ".py" or ".rb" or ".sh" or ".bash" or ".yml" or ".yaml" or ".toml" or ".pl" => (Python, true, false),
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".java" or ".js" or ".ts"
                or ".tsx" or ".jsx" or ".go" or ".rs" or ".swift" or ".kt" or ".php"
                or ".scala" or ".m" or ".mm" => (CLike, false, true),
            _ => (CLike, false, true),
        };

    public static List<Token> Tokenize(ReadOnlySpan<char> line, string ext, ref bool inBlockComment)
    {
        var tokens = new List<Token>();
        TokenizeInto(tokens, line, ext, ref inBlockComment);
        return tokens;
    }

    /// <summary>Tokenize into a caller-owned list (cleared first) to avoid per-line allocation.</summary>
    public static void TokenizeInto(List<Token> tokens, ReadOnlySpan<char> line, string ext, ref bool inBlockComment)
    {
        var (keywords, hashComment, slashComment) = LangFor(ext);
        var kwLookup = keywords.GetAlternateLookup<ReadOnlySpan<char>>();
        tokens.Clear();

        void Emit(TokenKind kind, int start, int length)
        {
            if (length > 0) tokens.Add(new Token(start, length, kind));
        }

        // Preprocessor in C-like languages: #include, #if, #region ...
        if (!hashComment && !inBlockComment && line.TrimStart().StartsWith("#"))
        {
            tokens.Add(new Token(0, line.Length, TokenKind.Preproc));
            return;
        }

        int i = 0;
        int pend = 0; // start of the pending Normal run [pend, i)
        void FlushNormal(int upto)
        {
            Emit(TokenKind.Normal, pend, upto - pend);
            pend = upto;
        }

        while (i < line.Length)
        {
            char c = line[i];

            if (inBlockComment)
            {
                int end = line[i..].IndexOf("*/");
                if (end < 0)
                {
                    Emit(TokenKind.Comment, i, line.Length - i);
                    i = line.Length;
                }
                else
                {
                    Emit(TokenKind.Comment, i, end + 2);
                    i += end + 2;
                    inBlockComment = false;
                }
                pend = i;
                continue;
            }

            if (slashComment && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                FlushNormal(i);
                Emit(TokenKind.Comment, i, line.Length - i);
                i = line.Length;
                pend = i;
                break;
            }
            if (slashComment && c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                FlushNormal(i);
                inBlockComment = true;
                continue; // the inBlockComment branch above emits the comment token
            }
            if (hashComment && c == '#')
            {
                FlushNormal(i);
                Emit(TokenKind.Comment, i, line.Length - i);
                i = line.Length;
                pend = i;
                break;
            }

            // Strings: "...", '...', `...` with '\' escaping
            if (c is '"' or '\'' or '`')
            {
                FlushNormal(i);
                int start = i;
                char quote = c;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
                    if (line[i] == quote) { i++; break; }
                    i++;
                }
                Emit(TokenKind.Str, start, i - start);
                pend = i;
                continue;
            }

            // Numbers: 123, 1.5, 0xFF, 10_000, 1e-3
            if (char.IsDigit(c) && (i == 0 || !char.IsLetter(line[i - 1])))
            {
                FlushNormal(i);
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '.')) i++;
                Emit(TokenKind.Number, start, i - start);
                pend = i;
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                FlushNormal(i);
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                if (word.StartsWith("@")) word = word[1..];
                Emit(kwLookup.Contains(word) ? TokenKind.Keyword : TokenKind.Normal, start, i - start);
                pend = i;
                continue;
            }

            i++;
        }
        FlushNormal(i);
        if (tokens.Count == 0) tokens.Add(new Token(0, 0, TokenKind.Normal));
    }
}
