using System.Buffers;

public enum TokenKind { Normal, Keyword, Str, Comment, Number, Preproc }

/// <summary>A highlighted range of the source line (no text is copied).</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>What a line starts inside, carried over from the line before it: code, a /* */ comment, a `…` string.</summary>
public enum LineState : byte { Code, Comment, Backticks }

/// <summary>
/// What a language looks like to the highlighter, chosen once per file by
/// its name. Files of unknown kinds (Markdown, plain text, logs…) are Plain:
/// no highlighting at all, so prose apostrophes or URLs are never mistaken
/// for strings and comments.
/// </summary>
public sealed class Syntax
{
    public required string Name { get; init; }
    public HashSet<string>? Keywords { get; init; }
    public bool SlashComments { get; init; }   // // and /* */
    public bool HashComments { get; init; }    // # to the end of the line
    public string? HashAfter { get; init; }    // # opens a comment only at the line start or after one of these
    public bool Preprocessor { get; init; }    // a line starting with # is a directive
    public bool Strings { get; init; }         // "…", '…', `…` with \ escapes
    public bool RawSingleQuotes { get; init; } // '…' takes no \ escapes (shell, YAML)
    public bool Lifetimes { get; init; }       // Rust: 'a is a lifetime or a label, not a character
    public bool VerbatimStrings { get; init; } // C#: @"…" — "" is a quote, \ is literal
    public bool MultiLineBackticks { get; init; } // `…` may run over lines: JS template literals, Go raw strings
    public bool RawBackticks { get; init; }    // `…` takes no \ escapes (Go)
    public bool RegexLiterals { get; init; }   // JS: /…/ where an operand may stand is a regular expression
    public bool Numbers { get; init; }
    public bool IsPlain => Keywords == null && !SlashComments && !HashComments && !Strings && !Numbers;

    private static readonly HashSet<string> CSharpWords = new(StringComparer.Ordinal)
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

    private static readonly HashSet<string> CLikeWords = new(StringComparer.Ordinal)
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
        "of","from","typeof","undefined","null","None","True","False",
    };

    private static readonly HashSet<string> PythonWords = new(StringComparer.Ordinal)
    {
        "and","as","assert","async","await","break","class","continue","def","del",
        "elif","else","except","False","finally","for","from","global","if","import",
        "in","is","lambda","None","nonlocal","not","or","pass","raise","return",
        "True","try","while","with","yield","match","case","print","self",
    };

    private static readonly HashSet<string> ShellWords = new(StringComparer.Ordinal)
    {
        "if","then","else","elif","fi","for","while","until","do","done","case","esac",
        "function","in","return","local","export","readonly","declare","unset","shift",
        "break","continue","exit","source","alias","select","time","true","false",
    };

    private static readonly HashSet<string> RubyWords = new(StringComparer.Ordinal)
    {
        "alias","and","begin","break","case","class","def","defined?","do","else","elsif",
        "end","ensure","false","for","if","in","module","next","nil","not","or","redo",
        "rescue","retry","return","self","super","then","true","undef","unless","until",
        "when","while","yield","require","attr_accessor","attr_reader","lambda","puts",
    };

    public static readonly Syntax Plain = new() { Name = "text" };
    private static readonly Syntax CSharp = new()
    {
        Name = "C#", Keywords = CSharpWords, SlashComments = true, Preprocessor = true,
        Strings = true, VerbatimStrings = true, Numbers = true,
    };
    private static readonly Syntax CLike = new()
    {
        Name = "C-like", Keywords = CLikeWords, SlashComments = true, Preprocessor = true,
        Strings = true, Numbers = true,
    };
    private static readonly Syntax JavaScript = new()
    {
        Name = "JavaScript", Keywords = CLikeWords, SlashComments = true, Preprocessor = true,
        Strings = true, Numbers = true, MultiLineBackticks = true, RegexLiterals = true,
    };
    private static readonly Syntax Go = new()
    {
        Name = "Go", Keywords = CLikeWords, SlashComments = true, Preprocessor = true,
        Strings = true, Numbers = true, MultiLineBackticks = true, RawBackticks = true,
    };
    private static readonly Syntax Rust = new()
    {
        Name = "Rust", Keywords = CLikeWords, SlashComments = true, Preprocessor = true,
        Strings = true, Numbers = true, Lifetimes = true,
    };
    private static readonly Syntax Css = new() { Name = "CSS", SlashComments = true, Strings = true, Numbers = true };
    private static readonly Syntax Python = new()
        { Name = "Python", Keywords = PythonWords, HashComments = true, Strings = true, Numbers = true };
    // A # inside a word is no comment: $#, ${#a[@]}, ${p##*/}.
    private static readonly Syntax Shell = new()
    {
        Name = "shell", Keywords = ShellWords, HashComments = true, HashAfter = " \t;&|()<>",
        Strings = true, RawSingleQuotes = true, Numbers = true,
    };
    private static readonly Syntax Ruby = new()
        { Name = "Ruby", Keywords = RubyWords, HashComments = true, Strings = true, Numbers = true };
    private static readonly Syntax Config = new() { Name = "config", HashComments = true, Strings = true, Numbers = true };
    // A # comment needs a space before it, so a URL's #anchor stays in the value.
    private static readonly Syntax Yaml = new()
        { Name = "YAML", HashComments = true, HashAfter = " \t", Strings = true, RawSingleQuotes = true, Numbers = true };
    private static readonly Syntax HashOnly = new() { Name = "hash comments", HashComments = true };

    /// <summary>The syntax for a file, by extension or well-known name.</summary>
    public static Syntax For(string path)
    {
        string name = Path.GetFileName(path);
        switch (name)
        {
            case "Makefile" or "makefile" or "GNUmakefile" or "Dockerfile" or "Containerfile"
                or ".gitignore" or ".gitattributes" or ".dockerignore" or ".npmignore" or ".gitmodules":
                return HashOnly;
            case "Gemfile" or "Rakefile" or "Vagrantfile" or "Brewfile" or "Podfile" or "Fastfile":
                return Ruby;
            case ".bashrc" or ".bash_profile" or ".zshrc" or ".zprofile" or ".profile" or ".envrc":
                return Shell;
            case "CMakeLists.txt" or ".env":
                return Config;
        }
        if (name.StartsWith("Dockerfile.", StringComparison.Ordinal)) return HashOnly;
        if (name.StartsWith(".env.", StringComparison.Ordinal)) return Config;
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => CSharp,
            ".js" or ".mjs" or ".cjs" or ".ts" or ".mts" or ".cts" or ".tsx" or ".jsx" => JavaScript,
            ".go" => Go,
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".cxx" or ".hh" or ".hxx" or ".ino"
                or ".m" or ".mm" or ".java" or ".swift" or ".kt" or ".kts" or ".php"
                or ".scala" or ".dart" or ".groovy" or ".gradle" or ".proto" or ".zig" or ".sol"
                or ".json" or ".jsonc" or ".json5" => CLike,
            ".rs" => Rust,
            ".css" or ".scss" or ".less" => Css,
            ".py" or ".pyw" or ".pyi" => Python,
            ".sh" or ".bash" or ".zsh" or ".fish" or ".ksh" or ".bats" => Shell,
            ".rb" or ".rake" or ".gemspec" => Ruby,
            ".yml" or ".yaml" => Yaml,
            ".toml" or ".conf" or ".cfg" or ".properties" or ".tf" or ".tfvars"
                or ".hcl" or ".nix" or ".cmake" or ".ps1" or ".psm1" or ".pl" or ".pm" or ".r" => Config,
            ".mk" or ".dockerfile" => HashOnly,
            _ => Plain,
        };
    }
}

/// <summary>
/// Simple line-based syntax highlighting: keywords, strings, comments
/// (//, /* */, #), numbers, preprocessor, JavaScript's regular expressions.
/// Multi-line /* */ comments and `…` strings are supported via a LineState carried between lines.
/// Works on spans and returns ranges, so tokenizing allocates nothing
/// per token — important because it runs on every rendered frame.
/// </summary>
public static class Highlighter
{
    public static ConsoleColor ColorFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => ConsoleColor.Cyan,
        TokenKind.Str => ConsoleColor.Yellow,
        TokenKind.Comment => ConsoleColor.DarkGreen,
        TokenKind.Number => ConsoleColor.Magenta,
        TokenKind.Preproc => ConsoleColor.DarkCyan,
        _ => ConsoleColor.Gray,
    };

    // Characters that can change the comment state; everything between them is skipped
    // when only the state is wanted (EndState), so long lines are scanned vectorized.
    private static readonly SearchValues<char> StateChars = SearchValues.Create("/\"'`#@$");

    public static List<Token> Tokenize(ReadOnlySpan<char> line, Syntax syntax, ref LineState state)
    {
        var tokens = new List<Token>();
        TokenizeInto(tokens, line, syntax, ref state);
        return tokens;
    }

    /// <summary>Tokenize a line that starts inside a /* */ comment or not (in no `…` string).</summary>
    public static List<Token> Tokenize(ReadOnlySpan<char> line, Syntax syntax, ref bool inBlockComment)
    {
        var state = inBlockComment ? LineState.Comment : LineState.Code;
        var tokens = Tokenize(line, syntax, ref state);
        inBlockComment = state == LineState.Comment;
        return tokens;
    }

    /// <summary>What the line ends inside, given what it starts inside.</summary>
    public static LineState EndState(ReadOnlySpan<char> line, Syntax syntax, LineState state)
    {
        if (!CarriesState(syntax)) return LineState.Code;
        Scan(null, line, syntax, ref state, line.Length);
        return state;
    }

    /// <summary>Whether the line ends inside a /* */ comment, given whether it starts in one.</summary>
    public static bool EndState(ReadOnlySpan<char> line, Syntax syntax, bool inBlockComment) =>
        EndState(line, syntax, inBlockComment ? LineState.Comment : LineState.Code) == LineState.Comment;

    /// <summary>Only languages with /* */ comments or multi-line `…` strings carry a state from line to line.</summary>
    public static bool CarriesState(Syntax syntax) => syntax.SlashComments || syntax.MultiLineBackticks;

    /// <summary>
    /// Tokenize into a caller-owned list (cleared first) to avoid per-line allocation.
    /// Tokens cover [0, line.Length); past `limit` (the visible part) the rest is one plain run.
    /// </summary>
    public static void TokenizeInto(List<Token> tokens, ReadOnlySpan<char> line, Syntax syntax,
        ref LineState state, int limit = int.MaxValue)
    {
        tokens.Clear();
        if (!CarriesState(syntax)) state = LineState.Code;
        if (syntax.IsPlain)
        {
            tokens.Add(new Token(0, line.Length, TokenKind.Normal));
            return;
        }
        Scan(tokens, line, syntax, ref state, Math.Min(limit, line.Length));
        if (tokens.Count == 0) tokens.Add(new Token(0, 0, TokenKind.Normal));
    }

    /// <summary>The tokenizer proper; with tokens == null it only tracks the state carried to the next line.</summary>
    private static void Scan(List<Token>? tokens, ReadOnlySpan<char> line, Syntax syntax,
        ref LineState state, int limit)
    {
        if (!CarriesState(syntax)) state = LineState.Code;
        bool hasKeywords = syntax.Keywords != null;
        var kwLookup = hasKeywords ? syntax.Keywords!.GetAlternateLookup<ReadOnlySpan<char>>() : default;
        int i = 0;
        int pend = 0; // start of the pending Normal run [pend, i)
        var normal = TokenKind.Normal;

        void Emit(TokenKind kind, int start, int length)
        {
            if (length > 0) tokens?.Add(new Token(start, length, kind));
        }
        void FlushNormal(int upto)
        {
            Emit(normal, pend, upto - pend);
            pend = upto;
        }

        // A preprocessor directive (#include, #if, #region …): the line keeps the
        // directive colour, but strings and comments in it are still recognised.
        if (syntax.Preprocessor && state == LineState.Code)
        {
            int lead = line.Length - line.TrimStart().Length;
            if (lead < line.Length && line[lead] == '#')
            {
                normal = TokenKind.Preproc;
                i = lead + 1;
                while (i < line.Length && char.IsLetter(line[i])) i++;
            }
        }

        while (i < limit)
        {
            if (state == LineState.Comment)
            {
                int end = line[i..].IndexOf("*/");
                FlushNormal(i);
                if (end < 0)
                {
                    Emit(TokenKind.Comment, i, line.Length - i);
                    i = pend = line.Length;
                    return;
                }
                Emit(TokenKind.Comment, i, end + 2);
                i = pend = i + end + 2;
                state = LineState.Code;
                continue;
            }
            if (state == LineState.Backticks)
            {
                FlushNormal(i);
                int start = i;
                if (BackticksEnd(line, i, syntax) is var end and >= 0) { i = end; state = LineState.Code; }
                else i = line.Length; // the string goes on into the next line
                Emit(TokenKind.Str, start, i - start);
                pend = i;
                continue;
            }

            if (tokens == null)
            {
                int next = line[i..limit].IndexOfAny(StateChars);
                if (next < 0) break;
                i += next;
            }
            char c = line[i];
            char c1 = i + 1 < line.Length ? line[i + 1] : '\0';

            if (syntax.SlashComments && c == '/' && c1 == '/')
            {
                FlushNormal(i);
                Emit(TokenKind.Comment, i, line.Length - i);
                i = pend = line.Length;
                return;
            }
            if (syntax.SlashComments && c == '/' && c1 == '*')
            {
                FlushNormal(i);
                int end = line[(i + 2)..].IndexOf("*/"); // "/*/" does not close itself
                if (end < 0)
                {
                    Emit(TokenKind.Comment, i, line.Length - i);
                    state = LineState.Comment;
                    i = pend = line.Length;
                    return;
                }
                Emit(TokenKind.Comment, i, end + 4);
                i = pend = i + end + 4;
                continue;
            }
            if (syntax.HashComments && c == '#' && (syntax.HashAfter == null || i == 0 || syntax.HashAfter.Contains(line[i - 1])))
            {
                FlushNormal(i);
                Emit(TokenKind.Comment, i, line.Length - i);
                i = pend = line.Length;
                return;
            }

            // JavaScript: /…/ where an operand may stand (after = ( , return…) is a regular expression,
            // and a /* inside it opens no comment.
            if (syntax.RegexLiterals && c == '/' && RegexMayStart(line, i) && RegexEnd(line, i) is var regexEnd and >= 0)
            {
                FlushNormal(i);
                Emit(TokenKind.Str, i, regexEnd - i);
                i = pend = regexEnd;
                continue;
            }

            // Rust: 'a, 'static, 'outer: — a lifetime or a label; 'a' is a character.
            if (syntax.Lifetimes && c == '\'' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] == '_')
                && !(i + 2 < line.Length && line[i + 2] == '\''))
            {
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                continue;
            }

            // Strings: "…", '…', `…` with \ escapes; C# @"…" and $@"…" are verbatim.
            if (syntax.Strings)
            {
                int q = i;
                bool verbatim = false;
                if (syntax.VerbatimStrings)
                    while (q < line.Length && line[q] is '@' or '$' && q - i < 2)
                    {
                        verbatim |= line[q] == '@';
                        q++;
                    }
                if (q < line.Length && line[q] is '"' or '\'' or '`' && (q == i || line[q] == '"'))
                {
                    FlushNormal(i);
                    int start = i;
                    char quote = line[q];
                    if (quote == '`' && syntax.MultiLineBackticks)
                    {
                        if (BackticksEnd(line, q + 1, syntax) is var end and >= 0) i = end;
                        else { i = line.Length; state = LineState.Backticks; } // on into the next line
                        Emit(TokenKind.Str, start, i - start);
                        pend = i;
                        continue;
                    }
                    bool escapes = !verbatim && !(quote == '\'' && syntax.RawSingleQuotes);
                    i = q + 1;
                    while (i < line.Length)
                    {
                        if (verbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                        if (escapes && line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
                        if (line[i] == quote) { i++; break; }
                        i++;
                    }
                    Emit(TokenKind.Str, start, i - start);
                    pend = i;
                    continue;
                }
            }

            if (tokens == null) { i++; continue; } // nothing else changes the comment state

            // Numbers: 123, 1.5, 0xFF, 10_000, 1e-3 (in 0x1E-3 the - is a minus)
            if (syntax.Numbers && char.IsDigit(c) && (i == 0 || !char.IsLetter(line[i - 1])))
            {
                FlushNormal(i);
                int start = i;
                bool hex = c == '0' && c1 is 'x' or 'X';
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '.'
                    || (!hex && line[i] is '+' or '-' && line[i - 1] is 'e' or 'E'
                        && i + 1 < line.Length && char.IsDigit(line[i + 1])))) i++;
                Emit(TokenKind.Number, start, i - start);
                pend = i;
                continue;
            }

            // Identifiers and keywords (C# @keyword escapes are identifiers too)
            if (char.IsLetter(c) || c == '_' || (c == '@' && syntax.VerbatimStrings))
            {
                FlushNormal(i);
                int start = i;
                if (c == '@') i++; // consume it, or the scan below makes no progress
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                if (i < line.Length && line[i] == '?' && hasKeywords && kwLookup.Contains(line[start..(i + 1)])) i++; // Ruby's defined?
                var word = line[start..i];
                bool isKeyword = c != '@' && normal == TokenKind.Normal && hasKeywords && kwLookup.Contains(word);
                Emit(isKeyword ? TokenKind.Keyword : normal, start, i - start);
                pend = i;
                continue;
            }

            i++;
        }
        if (tokens == null) return;
        FlushNormal(Math.Min(i, limit));
        if (pend < line.Length) Emit(TokenKind.Normal, pend, line.Length - pend); // past the visible part
    }

    /// <summary>Just past the ` that closes a `…` string whose text starts at i; -1 — it goes on past the line.</summary>
    private static int BackticksEnd(ReadOnlySpan<char> line, int i, Syntax syntax)
    {
        for (; i < line.Length; i++)
        {
            if (line[i] == '\\' && !syntax.RawBackticks) i++;
            else if (line[i] == '`') return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// Whether a / at i may start a regular expression rather than divide: at the line start, after
    /// an operator, an opening bracket or a keyword such as return; not after a name, a number, ) or ].
    /// </summary>
    private static bool RegexMayStart(ReadOnlySpan<char> line, int i)
    {
        int p = i - 1;
        while (p >= 0 && line[p] is ' ' or '\t') p--;
        if (p < 0) return true;
        char prev = line[p];
        if (char.IsLetterOrDigit(prev) || prev is '_' or '$')
        {
            int s = p;
            while (s > 0 && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] is '_' or '$')) s--;
            return line[s..(p + 1)] is "return" or "typeof" or "instanceof" or "in" or "of" or "case" or "delete"
                or "void" or "throw" or "new" or "else" or "do" or "yield" or "await";
        }
        return prev is not (')' or ']' or '}' or '.' or '<' or '"' or '\'' or '`');
    }

    /// <summary>
    /// Just past a regular expression that starts with the / at i (and its flags); -1 — none ends on
    /// this line, or not within MaxRegex characters (so a long minified line stays quick to scan).
    /// </summary>
    private static int RegexEnd(ReadOnlySpan<char> line, int i)
    {
        const int MaxRegex = 2000;
        bool inClass = false; // a / inside [...] does not end it
        for (int j = i + 1; j < line.Length && j - i <= MaxRegex; j++)
        {
            char ch = line[j];
            if (ch == '\\') j++;
            else if (ch == '[') inClass = true;
            else if (ch == ']') inClass = false;
            else if (ch == '/' && !inClass)
            {
                if (j == i + 1) return -1; // "//" is a comment
                for (j++; j < line.Length && char.IsAsciiLetter(line[j]); j++) { } // flags: g, i, m…
                return j;
            }
        }
        return -1;
    }
}
