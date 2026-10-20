namespace ConsoleEditor;

public enum TokenKind { Normal, Keyword, Str, Comment, Number, Preproc }

public readonly record struct Token(string Text, TokenKind Kind);

/// <summary>
/// Simple line-based syntax highlighting with no external dependencies:
/// keywords, strings, comments (//, /* */, #), numbers, preprocessor.
/// Multi-line /* */ is supported via inBlockComment carried between lines.
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

    public static List<Token> Tokenize(string line, string ext, ref bool inBlockComment)
    {
        var (keywords, hashComment, slashComment) = LangFor(ext);
        var tokens = new List<Token>();
        var buf = new System.Text.StringBuilder();

        void Flush(TokenKind kind)
        {
            if (buf.Length == 0) return;
            tokens.Add(new Token(buf.ToString(), kind));
            buf.Clear();
        }

        // The rest of the line is a comment.
        void RestAsComment(int from)
        {
            Flush(TokenKind.Normal);
            buf.Append(line, from, line.Length - from);
            Flush(TokenKind.Comment);
        }

        // Preprocessor in C-like languages: #include, #if, #region ...
        string trimmed = line.TrimStart();
        if (!hashComment && !inBlockComment && trimmed.StartsWith('#'))
        {
            tokens.Add(new Token(line, TokenKind.Preproc));
            return tokens;
        }

        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];

            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0) { buf.Append(line, i, line.Length - i); i = line.Length; }
                else { buf.Append(line, i, end + 2 - i); i = end + 2; inBlockComment = false; }
                Flush(TokenKind.Comment);
                continue;
            }

            if (slashComment && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                RestAsComment(i);
                break;
            }
            if (slashComment && c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                Flush(TokenKind.Normal);
                inBlockComment = true;
                buf.Append("/*");
                i += 2;
                continue;
            }
            if (hashComment && c == '#')
            {
                RestAsComment(i);
                break;
            }

            // Strings: "...", '...', `...` with '\' escaping
            if (c is '"' or '\'' or '`')
            {
                Flush(TokenKind.Normal);
                char quote = c;
                buf.Append(c);
                i++;
                while (i < line.Length)
                {
                    buf.Append(line[i]);
                    if (line[i] == '\\' && i + 1 < line.Length) { buf.Append(line[i + 1]); i += 2; continue; }
                    if (line[i] == quote) { i++; break; }
                    i++;
                }
                Flush(TokenKind.Str);
                continue;
            }

            // Numbers: 123, 1.5, 0xFF, 10_000, 1e-3
            if (char.IsDigit(c) && (buf.Length == 0 || !char.IsLetter(buf[^1])))
            {
                Flush(TokenKind.Normal);
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '.'))
                    buf.Append(line[i++]);
                Flush(TokenKind.Number);
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                Flush(TokenKind.Normal);
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    buf.Append(line[i++]);
                string word = buf.ToString().TrimStart('@');
                Flush(keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Normal);
                continue;
            }

            buf.Append(c);
            i++;
        }
        Flush(inBlockComment ? TokenKind.Comment : TokenKind.Normal);
        if (tokens.Count == 0) tokens.Add(new Token("", TokenKind.Normal));
        return tokens;
    }
}
