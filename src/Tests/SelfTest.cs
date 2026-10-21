#if DEBUG
using System.Text;

/// <summary>Quick checks without a terminal: dotnet run -- --self-test</summary>
internal static class SelfTest
{
    private static int _failures, _passes;
    private static readonly List<string> TempDirs = new();

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (ok) _passes++;
        else _failures++;
    }

    public static int Run()
    {
        _failures = _passes = 0;
        // Recovery files of the tests must never reach the user's next session.
        Environment.SetEnvironmentVariable("LTT_RECOVERY_DIR", TempDir("recovery"));
        var groups = new (string Name, Action Test)[]
        {
            ("scoring", TestScoring), ("highlight", TestHighlight), ("input", TestInput),
            ("cells", TestCells), ("screen", TestScreen), ("round-trip", TestRoundTrip),
            ("save", TestSaveSafety), ("editing", TestEditing), ("undo", TestUndo),
            ("disk", TestDiskChanges), ("scrolling", TestScrolling), ("search", TestSearch), ("refine", TestSearchRefine),
            ("git search", TestGitSearch), ("tree", TestTree), ("git tree", TestGitTree), ("sizes", TestSizes),
            ("copy/move", TestCopyMove), ("watcher", TestWatcher), ("misc", TestMisc),
        };
        try
        {
            foreach (var (name, test) in groups)
            {
                try { test(); }
                catch (Exception ex) { Check(false, $"{name}: threw {ex}"); }
            }
        }
        finally
        {
            foreach (string d in TempDirs)
                try { Directory.Delete(d, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? $"ALL PASS ({_passes})" : $"FAILED: {_failures} of {_passes + _failures}");
        return _failures == 0 ? 0 : 1;
    }

    // ---- Helpers ----

    /// <summary>A fresh temporary folder, removed after the run.</summary>
    private static string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ltt-test-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        TempDirs.Add(dir);
        return dir;
    }

    private static string Put(string dir, string rel, string text) => PutBytes(dir, rel, Encoding.UTF8.GetBytes(text));

    private static string PutBytes(string dir, string rel, byte[] bytes)
    {
        string path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static TextEditor Open(string path, int width = 80, int height = 20)
    {
        var ed = new TextEditor { ViewWidth = width, ViewHeight = height };
        ed.Open(path);
        return ed;
    }

    private static string Text(TextEditor ed) => string.Join("\n", ed.Lines);

    private static void Type(TextEditor ed, string s)
    {
        foreach (char c in s) ed.InsertChar(c);
    }

    private static bool Run(string dir, string file, string args) => Proc.Run(file, args, dir, timeoutMs: 15000) != null;

    /// <summary>A git repository with a committed file, or null when git is unavailable.</summary>
    private static string? GitRepo(string name)
    {
        string dir = TempDir(name);
        bool ok = Run(dir, "git", "init -q")
                  && Run(dir, "git", "-c user.email=t@t -c user.name=t commit -q --allow-empty -m init");
        return ok ? dir : null;
    }

    // ---- Groups ----

    private static void TestScoring()
    {
        Check(FuzzySearch.ScoreQuery("manager", "var manager = 1;", out _, out _) > 900,
            "fuzzy: an exact substring scores high");
        Check(FuzzySearch.ScoreQuery("mngr", "manager lines", out int st, out int len) > 0 && st == 0 && len == 7,
            "fuzzy: subsequence \"mngr\" finds \"manager lines\"");
        Check(FuzzySearch.ScoreQuery("xyzq", "manager lines", out _, out _) < 0,
            "fuzzy: absent characters do not match");
        Check(FuzzySearch.ScoreQuery("Read File", "File.ReadAllLines(path)", out _, out _) > 0,
            "fuzzy: several words, case-insensitive, word order free");
        Check(FuzzySearch.ScoreQuery("read zzz", "File.ReadAllLines(path)", out _, out _) < 0,
            "fuzzy: if even one word fails — no result");
        int exact = FuzzySearch.ScoreQuery("open", "OpenFile()", out _, out _);
        int fuzzy = FuzzySearch.ScoreQuery("opnf", "OpenFile()", out _, out _);
        Check(exact > fuzzy && fuzzy > 0, "fuzzy: an exact hit ranks above a fuzzy one");
        Check(FuzzySearch.ScoreQuery("anchor", "a" + new string(' ', 30) + "nchor", out _, out _) < 0,
            "fuzzy: characters spread far apart are noise, not a match");
        Check(FuzzySearch.ScoreQuery("anhr", "a big nice anchor", out int cst, out int clen) > 0 && cst == 11 && clen == 6,
            "fuzzy: the match is the most compact window, not the leftmost letters");

        string cyrLine = "say \u043F\u0440\u0438\u0432\u0435\u0442 loudly"; // "say privet loudly" in Cyrillic
        Check(FuzzySearch.ScoreQuery("\u043F\u0440\u0438\u0432\u0435\u0442", cyrLine, out int yst, out int ylen) > 900 && yst == 4 && ylen == 6,
            "fuzzy: a Cyrillic exact substring matches");
        Check(FuzzySearch.ScoreQuery("\u043F\u0440\u0432\u0442", cyrLine, out _, out _) > 0,
            "fuzzy: a Cyrillic subsequence matches");

        var fileHits = FuzzySearch.SearchLines("/tmp/current.txt", "current.txt",
            new[] { "a manager in prose", "nothing here", "manager exact" }, "manager");
        Check(fileHits.Hits.Select(h => h.LineNumber).SequenceEqual(new[] { 1, 3 }) && !fileHits.Truncated,
            "file search: in-file hits stay in document order, not score order");
        var many = FuzzySearch.SearchLines("x", "x", Enumerable.Repeat("match", 600), "match");
        Check(many.Hits.Count == 500 && many.Truncated, "file search: a capped result says it is truncated");
        Check(NormalizeSeed(), "search: selected text becomes a tidy query");

        static bool NormalizeSeed() => App.NormalizeSearchSeed("  hello\n brave\tworld  ") == "hello brave world"
                                       && App.NormalizeSearchSeed(" \n\t ") == "";
    }

    private static void TestHighlight()
    {
        var cs = Syntax.For("x.cs");
        bool inBlock = false;
        const string srcLine = "class Foo { string s = \"hi\"; int n = 42; } // note";
        var toks = Highlighter.Tokenize(srcLine, cs, ref inBlock);
        string Txt(Token t) => srcLine.Substring(t.Start, t.Length);
        Check(toks.Any(t => t.Kind == TokenKind.Keyword && Txt(t) == "class")
              && toks.Any(t => t.Kind == TokenKind.Str && Txt(t).Contains("hi"))
              && toks.Any(t => t.Kind == TokenKind.Number && Txt(t) == "42")
              && toks.Any(t => t.Kind == TokenKind.Comment && Txt(t).Contains("note")),
            "highlight: keywords, strings, numbers and // comments");
        Check(string.Concat(toks.Select(Txt)) == srcLine, "highlight: token ranges add up to the original line");
        inBlock = false;
        Highlighter.Tokenize("int a; /* start", cs, ref inBlock);
        var mid = Highlighter.Tokenize("middle of the comment", cs, ref inBlock);
        Check(inBlock && mid.All(t => t.Kind == TokenKind.Comment), "highlight: multi-line /* */ comment");

        var md = Syntax.For("README.md");
        bool b = false;
        var prose = Highlighter.Tokenize("Don't panic: see https://x.dev/a, it's fine. /* not a comment", md, ref b);
        Check(md.IsPlain && prose.Count == 1 && prose[0].Kind == TokenKind.Normal && !b,
            "highlight: Markdown and other unknown files are plain text — no strings or comments from prose");
        b = false;
        Highlighter.Tokenize("clean:\n\trm -rf build/*", Syntax.For("Makefile"), ref b);
        Check(!b && Syntax.For("Dockerfile").HashComments && !Syntax.For(".gitignore").SlashComments,
            "highlight: a /* glob in a Makefile never opens a comment");
        var c = Syntax.For("x.c");
        Check(Highlighter.EndState("/*/ still a comment", c, false), "highlight: \"/*/\" does not close itself");
        Check(Highlighter.EndState("#define X 1 /* starts here", c, false),
            "highlight: a comment opened on a #define line carries on");
        b = false;
        const string verbatim = "var p = @\"C:\\dir\\\"; /* c */ x";
        var vt = Highlighter.Tokenize(verbatim, cs, ref b);
        Check(!b && vt.Any(t => t.Kind == TokenKind.Str && verbatim.Substring(t.Start, t.Length) == "@\"C:\\dir\\\"")
              && vt.Any(t => t.Kind == TokenKind.Comment && verbatim.Substring(t.Start, t.Length) == "/* c */"),
            "highlight: a C# verbatim string ends at its quote; a backslash does not escape it");

        // Fuzz: tokenizing always terminates, tiles the line, and agrees with the fast state scan.
        var rnd = new Random(12345);
        const string alphabet = "/*\"'`#@$\\ ab1_.\t";
        bool tiles = true, agrees = true;
        foreach (var syntax in new[] { cs, c, Syntax.For("x.py"), Syntax.For("x.sh"), md, Syntax.For("x.css") })
            for (int n = 0; n < 2000; n++)
            {
                var chars = new char[rnd.Next(0, 24)];
                for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[rnd.Next(alphabet.Length)];
                string line = new(chars);
                bool start = rnd.Next(2) == 0, s1 = start;
                var t = Highlighter.Tokenize(line, syntax, ref s1);
                int pos = 0;
                foreach (var tok in t) { tiles &= tok.Start == pos; pos += tok.Length; }
                tiles &= pos == line.Length;
                agrees &= s1 == (syntax.SlashComments && Highlighter.EndState(line, syntax, start));
            }
        Check(tiles, "highlight fuzz: tokens always tile the line (and tokenizing terminates)");
        Check(agrees, "highlight fuzz: the fast comment-state scan agrees with full tokenizing");
        foreach (var tricky in new[] { "@docs/agents/rules-all-agents.md", "@", "@@", "a@b", "@\"verbatim\"", "$@\"x\"", "@class @if x = @" })
        {
            bool s = false;
            var tt = Highlighter.Tokenize(tricky, cs, ref s);
            Check(string.Concat(tt.Select(t => tricky.Substring(t.Start, t.Length))) == tricky,
                $"highlight: tokenization terminates and tiles \"{tricky}\"");
        }
    }

    private static void TestInput()
    {
        var wheel = RawTerminalInput.ParseCsi("<65;40;5", 'M') as MouseInput;
        Check(wheel is { IsWheel: true, WheelDown: true, X: 39, Y: 4, Pressed: true },
            "input: SGR wheel down, coordinates converted to 0-based");
        Check(RawTerminalInput.ParseCsi("<67;40;5", 'M') is MouseInput { IsWheel: true, IsHorizontalWheel: true, WheelDown: true }
              && RawTerminalInput.ParseCsi("<66;40;5", 'M') is MouseInput { IsHorizontalWheel: true, WheelDown: false }
              && wheel is { IsHorizontalWheel: false, HasShift: false }
              && RawTerminalInput.ParseCsi("<69;40;5", 'M') is MouseInput { IsHorizontalWheel: false, HasShift: true, WheelDown: true },
            "input: SGR 66/67 is the sideways wheel (67 — right), 68/69 is Shift+wheel");
        Check(RawTerminalInput.ParseCsi("<0;10;3", 'M') is MouseInput { IsLeftClick: true, X: 9, Y: 2 }
              && RawTerminalInput.ParseCsi("<0;10;3", 'm') is MouseInput { Pressed: false, IsLeftClick: false },
            "input: SGR left click and release");
        Check(RawTerminalInput.ParseCsi("<128;10;3", 'M') == null && RawTerminalInput.ParseCsi("<129;10;3", 'M') == null,
            "input: mouse back/forward buttons are not clicks");
        Check(RawTerminalInput.ParseCsi("1;5", 'C') is KeyInput { Key: { Key: ConsoleKey.RightArrow } cr }
              && cr.Modifiers.HasFlag(ConsoleModifiers.Control),
            "input: CSI 1;5C — Ctrl+→");
        Check(RawTerminalInput.ParseCsi("17", '~') is KeyInput { Key.Key: ConsoleKey.F6 }
              && RawTerminalInput.ParseCsi("3", '~') is KeyInput { Key.Key: ConsoleKey.Delete }
              && RawTerminalInput.ParseCsi("?25", 'l') == null,
            "input: CSI ~ keys; unknown CSI is ignored");
        Check(RawTerminalInput.ParseSs3('q') is KeyInput { Key.KeyChar: '1' } && RawTerminalInput.ParseSs3('M') is KeyInput { Key.Key: ConsoleKey.Enter }
              && RawTerminalInput.ParseSs3('A') is KeyInput { Key.Key: ConsoleKey.UpArrow } && RawTerminalInput.ParseSs3('k') is KeyInput { Key.KeyChar: '+' },
            "input: application-mode keypad digits, operators and Enter arrive");

        static ConsoleKeyInfo? U(string p) => (RawTerminalInput.ParseCsi(p, 'u') as KeyInput)?.Key;
        Check(U("111;5") is { Key: ConsoleKey.O } o && App.IsCtrlKey(o, ConsoleKey.O), "input: CSI u 111;5 — Ctrl+O (kitty)");
        Check(U("122;6") is { Key: ConsoleKey.Z } z && z.Modifiers.HasFlag(ConsoleModifiers.Control) && z.Modifiers.HasFlag(ConsoleModifiers.Shift),
            "input: CSI u 122;6 — Ctrl+Shift+Z");
        Check(U("1092::102;5") is { } kf && App.IsCtrlF(kf),
            "input: a Cyrillic layout falls back to the physical key — Ctrl+F works");
        Check(U("97:65:113;5") is { Key: ConsoleKey.A } azerty && App.IsCtrlKey(azerty, ConsoleKey.A),
            "input: AZERTY Ctrl+A is Ctrl+A, not Ctrl+Q (the US position)");
        Check(U("122:90:121;1") is { Key: ConsoleKey.Z, KeyChar: 'z' },
            "input: QWERTZ z is z, not y (no stray yes at a prompt)");
        Check(U("115;9") is { Key: ConsoleKey.S } cmdS && App.IsCtrlKey(cmdS, ConsoleKey.S)
              && U("115;9;115") is { } cmdText && cmdText.Modifiers.HasFlag(ConsoleModifiers.Control),
            "input: Cmd/Super+S acts as Ctrl+S and never types an s");
        Check(RawTerminalInput.ParseCsi("1;9", 'D') is KeyInput { Key: { Key: ConsoleKey.Home } h } && !h.Modifiers.HasFlag(ConsoleModifiers.Control)
              && RawTerminalInput.ParseCsi("1;9", 'B') is KeyInput { Key: { Key: ConsoleKey.End } e } && e.Modifiers.HasFlag(ConsoleModifiers.Control),
            "input: Cmd+← is Home, Cmd+↓ is the document end");
        Check(U("97;2;65") is { Key: ConsoleKey.A, KeyChar: 'A' }, "input: kitty associated text carries the typed character");
        Check(U("27") is { Key: ConsoleKey.Escape } && U("57417") is { Key: ConsoleKey.LeftArrow, KeyChar: '\0' },
            "input: kitty Escape and keypad arrows");
        // Fuzz: no parameter string or final byte makes the parsers throw.
        var rnd = new Random(7);
        const string paramChars = "0123456789;:<?>=u~ ";
        bool noThrow = true;
        for (int n = 0; n < 20000 && noThrow; n++)
        {
            var p = new string(Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => paramChars[rnd.Next(paramChars.Length)]).ToArray());
            try
            {
                RawTerminalInput.ParseCsi(p, (char)rnd.Next(0x40, 0x7F));
                RawTerminalInput.ParseCsi(p, 'u');
                RawTerminalInput.ParseSs3((char)rnd.Next(0x20, 0x7F));
            }
            catch (Exception) { noThrow = false; }
        }
        Check(noThrow, "input fuzz: random CSI parameters never throw");
        // Raw bytes: a broken UTF-8 sequence never swallows the key after it; Esc + a character is Alt+it.
        static List<InputEvent> Bytes(params byte[] b)
        {
            using var input = new RawTerminalInput(b);
            var list = new List<InputEvent>();
            while (input.TryRead(0, out var ev) && ev != null) list.Add(ev);
            return list;
        }
        Check(Bytes(0x80, 0x1B, (byte)'[', (byte)'A') is [KeyInput { Key.Key: ConsoleKey.UpArrow }]
              && Bytes(0xE2, 0x1B, (byte)'[', (byte)'B') is [KeyInput { Key.Key: ConsoleKey.DownArrow }],
            "input: a broken UTF-8 sequence is dropped and the next key arrives whole");
        Check(Bytes(0x1B, 0xC3, 0xB6, 0x1B, 0xC3, 0xB6) is [KeyInput { Key: { KeyChar: 'ö' } a1 }, KeyInput { Key.KeyChar: 'ö' }]
              && a1.Modifiers.HasFlag(ConsoleModifiers.Alt) && App.Typed(a1) == '\0',
            "input: Esc + a UTF-8 character is Alt+that character, every time");
        Check(Bytes(0xF0, 0x9F, 0x91, 0x80) is [KeyInput { Key.KeyChar: '\uD83D' } hi, KeyInput { Key.KeyChar: '\uDC40' } lo]
              && App.Typed(hi.Key) == '\uD83D' && App.Typed(lo.Key) == '\uDC40',
            "input: an emoji arrives as both halves of its surrogate pair");
        Check(Bytes(0xC2, 0x9B) is [KeyInput { Key: var c1 }] && App.Typed(c1) == '\0'
              && U("55296") is not { KeyChar: >= '\uD800' and <= '\uDFFF' },
            "input: C1 controls and lone surrogates are never typed into the text");

        Check(App.SearchJumpDelta(new ConsoleKeyInfo((char)7, ConsoleKey.G, false, false, true)) == +1
              && App.SearchJumpDelta(new ConsoleKeyInfo((char)7, ConsoleKey.G, true, false, true)) == -1
              && App.SearchJumpDelta(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false)) == 0,
            "keys: Ctrl+G / Ctrl+Shift+G jump to the next/previous hit");
    }

    private static void TestCells()
    {
        Check(Cells.Width('a') == 1 && Cells.Width('界') == 2 && Cells.Width(0x26A1) == 2 && Cells.Width(0x1F440) == 2
              && Cells.Width(0x301) == 0 && Cells.Width(0x200D) == 0 && Cells.Width('é') == 1,
            "cells: narrow, wide (CJK, ⚡, emoji) and zero-width characters");
        Check(Cells.ColumnOf("\tx", 1) == 4 && Cells.ColumnOf("ab\tx", 3) == 4 && Cells.ColumnOf("abcd\tx", 5) == 8
              && Cells.ColumnOf("界a", 1) == 2 && Cells.ColumnOf("👀a", 2) == 2 && Cells.WidthOf("a\u0301b") == 2,
            "cells: tabs run to the next stop, wide characters take two cells, marks none");
        Check(Cells.IndexAt("\tx", 2) == 0 && Cells.IndexAt("\tx", 4) == 1 && Cells.IndexAt("界a", 1) == 0
              && Cells.IndexAt("界a", 2) == 1 && Cells.IndexAt("ab", 9) == 2 && Cells.IndexAt("ab", -3) == 0,
            "cells: a display column maps back to the character drawn there");
        string s = "a👀b";
        Check(Cells.PrevBoundary(s, 3) == 1 && Cells.NextBoundary(s, 1) == 3 && Cells.FloorBoundary(s, 2) == 1,
            "cells: emoji are one character to the cursor, never half a surrogate pair");
        Check(Cells.Visible('\x1b') == '␛' && Cells.Visible('\a') == '␇' && Cells.Visible('\x9b') == '\uFFFD' && Cells.Visible('x') == 'x',
            "cells: control characters get visible stand-ins");
        string flags = "ab" + string.Concat(Enumerable.Repeat("🇺🇸", 20));
        Check(Enumerable.Range(2, flags.Length - 2).All(i => (Cells.FloorBoundary(flags, i) - 2) % 4 == 0),
            "cells: a long run of flags is never split between two flags' letters");
    }

    private static void TestScreen()
    {
        var sc = new Screen();
        sc.Begin(20, 4);
        sc.Put(0, 0, "a\x1b]0;PWN\ab", ConsoleColor.Gray, ConsoleColor.Black, 20);
        Check(sc.RowText(0).StartsWith("a␛]0;PWN␇b"), "screen: escape sequences in text are shown, never executed");
        sc.Put(0, 1, "\tx", ConsoleColor.Gray, ConsoleColor.Black, 20);
        Check(sc.RowText(1).StartsWith("    x"), "screen: a tab becomes spaces to the next stop");
        int used = sc.Put(0, 2, "ab界", ConsoleColor.Gray, ConsoleColor.Black, 3);
        Check(used == 2 && sc.RowText(2).StartsWith("ab "), "screen: a wide character that does not fit is left out, not cut");
        sc.Put(0, 3, "界", ConsoleColor.Gray, ConsoleColor.Black, 2);
        sc.Put(1, 3, "x", ConsoleColor.Gray, ConsoleColor.Black, 1);
        Check(sc.RowText(3).StartsWith(" x"), "screen: overwriting half of a wide character blanks the other half");
        var row = new Screen();
        string Draw(string line, int scrollX)
        {
            row.Begin(10, 1);
            App.WriteHighlighted(row, 0, 0, line, new List<Token> { new(0, line.Length, TokenKind.Normal) }, scrollX, 10);
            return row.RowText(0);
        }
        Check(Draw(new string('a', 30), 0) == "aaaaaaaaa›" && Draw(new string('a', 30), 25) == "‹aaaa     "
              && Draw(new string('a', 10), 0) == "aaaaaaaaaa" && Draw("界界界界界界", 1) == "‹界界界界›",
            "screen: ‹ › flag text cut at the edges, and only then");
        Check(App.FitTail("/very/long/path/to/file.cs", 12) == "…/to/file.cs"
              && App.BuildTitle("/a/very/long/path/to/the/file.cs", false, "", 30).EndsWith("file.cs"),
            "title: a long path loses its beginning, never the file name");
    }

    private static void TestRoundTrip()
    {
        string dir = TempDir("roundtrip");
        byte[] U8(string s) => Encoding.UTF8.GetBytes(s);
        byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("lf.txt", U8("a\nb\n")),
            ("crlf.txt", U8("a\r\nb\r\n")),
            ("cr.txt", U8("a\rb\r")),
            ("noeol.txt", U8("a\nb")),
            ("empty.txt", Array.Empty<byte>()),
            ("Makefile", U8("all:\n\techo hi\n\t\tx = 1\n")),
            ("bom.txt", Cat(new byte[] { 0xEF, 0xBB, 0xBF }, U8("x\r\n"))),
            ("utf16le.txt", Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("héllo\r\nwörld\r\n"))),
            ("utf16be.txt", Cat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes("x\n"))),
            ("latin1.txt", new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', 0x80, (byte)'\n' }),
            ("emoji.txt", U8("👀 ok\n界\n")),
            ("trailing-blank.txt", U8("x\n\n\n")),
        };
        foreach (var (name, bytes) in cases)
        {
            string path = PutBytes(dir, name, bytes);
            var ed = Open(path);
            bool clean = !ed.Dirty;
            ed.Save();
            Check(clean && File.ReadAllBytes(path).SequenceEqual(bytes), $"round-trip: {name} is saved byte for byte");
        }

        var crlf = Open(Path.Combine(dir, "crlf.txt"));
        crlf.InsertChar('X');
        crlf.InsertNewline();
        crlf.Save();
        Check(File.ReadAllText(Path.Combine(dir, "crlf.txt")) == "X\r\na\r\nb\r\n", "round-trip: an edit keeps CRLF line endings");
        var mixed = Open(PutBytes(dir, "mixed.txt", U8("a\r\nb\nc\r\n")));
        bool flagged = mixed.Format.MixedNewLines && mixed.Format.NewLine == "\r\n";
        mixed.Save();
        Check(flagged && File.ReadAllText(Path.Combine(dir, "mixed.txt")) == "a\r\nb\r\nc\r\n",
            "round-trip: mixed line endings are reported and saved as the majority (CRLF)");
        var latin = Open(Path.Combine(dir, "latin1.txt"));
        Check(latin.Format.Encoding == TextEncoding.Latin1 && latin.LineText(0) == "café",
            "round-trip: a file that is not UTF-8 opens as Latin-1, é intact");
        var utf16 = Open(Path.Combine(dir, "utf16le.txt"));
        Check(utf16.LineText(1) == "wörld" && utf16.Format.Encoding == TextEncoding.Utf16LE, "round-trip: UTF-16 is decoded");

        var badBomBytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0xE9, (byte)'\n' };
        string badBom = PutBytes(dir, "badbom.txt", badBomBytes);
        var bb = Open(badBom);
        bb.Save();
        Check(bb.Format.Encoding == TextEncoding.Latin1 && File.ReadAllBytes(badBom).SequenceEqual(badBomBytes),
            "round-trip: a stray byte after a UTF-8 BOM opens as Latin-1 and saves byte for byte");
        int refused = 0;
        foreach (var bytes in new[] { new byte[] { 0xFF, 0xFE, 0x61, 0, 0, 0xD8, 0x62, 0 }, new byte[] { 0xFF, 0xFE, 0x61, 0, 0x62 } })
            try { Open(PutBytes(dir, "bad16.txt", bytes)); }
            catch (InvalidDataException) { refused++; }
        Check(refused == 2, "round-trip: broken UTF-16 (a lone surrogate, an odd byte) is refused, never altered");
    }

    private static void TestSaveSafety()
    {
        string dir = TempDir("save");
        string latinPath = PutBytes(dir, "l1.txt", new byte[] { (byte)'a', 0xE9, (byte)'\n' });
        var latin = Open(latinPath);
        latin.InsertChar('€');
        bool refused = false;
        try { latin.Save(); } catch (IOException) { refused = true; }
        Check(refused && File.ReadAllBytes(latinPath).SequenceEqual(new byte[] { (byte)'a', 0xE9, (byte)'\n' }) && latin.Dirty,
            "save: a character Latin-1 cannot hold is refused, the file untouched");

        string bin = PutBytes(dir, "bin.dat", new byte[] { 1, 0, 2, 3 });
        bool binRefused = false;
        try { Open(bin); } catch (InvalidDataException) { binRefused = true; }
        Check(binRefused, "open: a binary file (NUL bytes) is refused");

        if (OperatingSystem.IsWindows()) return;
        string modePath = Put(dir, "mode.sh", "echo hi\n");
        File.SetUnixFileMode(modePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var me = Open(modePath);
        me.InsertChar('#');
        me.Save();
        Check(File.GetUnixFileMode(modePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead)
              && File.ReadAllText(modePath) == "#echo hi\n",
            "save: the file's permissions survive the atomic replace");
        string target = Put(dir, "target.txt", "t\n");
        string link = Path.Combine(dir, "link.txt");
        File.CreateSymbolicLink(link, target);
        var le = Open(link);
        le.InsertChar('L');
        le.Save();
        Check(new FileInfo(link).LinkTarget != null && File.ReadAllText(target) == "Lt\n",
            "save: through a symlink — the link stays a link, the target gets the text");
        string ro = Put(dir, "ro.txt", "ro\n");
        File.SetUnixFileMode(ro, UnixFileMode.UserRead);
        var re = Open(ro);
        re.InsertChar('x');
        bool roRefused = false;
        try { re.Save(); } catch (UnauthorizedAccessException) { roRefused = true; }
        Check(roRefused && File.ReadAllText(ro) == "ro\n", "save: a read-only file is refused, not replaced");
        File.SetUnixFileMode(ro, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Check(!Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories).Any(), "save: no temporary files are left behind");

        string longName = Put(dir, new string('n', 240) + ".txt", "x\n");
        string? Inode(string p) => Proc.Run("ls", $"-i \"{p}\"")?.Trim().Split(' ')[0];
        string? inodeBefore = Inode(longName);
        var lnEd = Open(longName);
        lnEd.InsertChar('L');
        lnEd.Save();
        Check(File.ReadAllText(longName) == "Lx\n" && inodeBefore != null && Inode(longName) != inodeBefore,
            "save: a file with a name as long as names go is still replaced atomically");

        string roDir = Path.Combine(dir, "rodir");
        string inRo = Put(roDir, "f.txt", "r\n");
        File.SetUnixFileMode(roDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var ir = Open(inRo);
        ir.InsertChar('W');
        try { ir.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        File.SetUnixFileMode(roDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Check(File.ReadAllText(inRo) == "Wr\n" && !ir.Dirty, "save: in a read-only folder the file itself is overwritten");

        string hard = Put(dir, "hard.txt", "h\n");
        if (Run(dir, "ln", "hard.txt hard2.txt"))
        {
            var he = Open(hard);
            he.InsertChar('H');
            he.Save();
            Check(File.ReadAllText(Path.Combine(dir, "hard2.txt")) == "Hh\n",
                "save: a hard-linked file is written in place, so its other name has the edit too");
        }

        string fifo = Path.Combine(dir, "pipe");
        if (Run(dir, "mkfifo", "pipe"))
        {
            var opening = Task.Run(() =>
            {
                try { Open(fifo); return false; }
                catch (IOException) { return true; }
            });
            Check(opening.Wait(3000) && opening.Result, "open: a FIFO is refused as not a regular file, never waited on");
            var hits = Task.Run(() => FuzzySearch.Search(dir, "anything"));
            Check(hits.Wait(5000), "search: a folder with a FIFO finishes");
        }
    }

    private static void TestEditing()
    {
        string dir = TempDir("edit");
        var ed = Open(Put(dir, "anchor.txt", "abcdefg\n"));
        ed.SetCursor(0, 5);
        ed.EnsureAnchor(); // Shift+← …
        ed.MoveLeft();
        ed.MoveRight();    // … Shift+→: back where it started, nothing selected
        Type(ed, "xy");
        Check(ed.LineText(0) == "abcdexyfg", "selection: an empty Shift-selection never eats typed characters");
        var end = Open(Put(dir, "end.txt", "abc"));
        end.DocEnd();
        end.EnsureAnchor(); // Shift+→ at the very end moves nothing
        end.MoveRight();
        end.Backspace();
        end.Backspace();
        Check(end.LineText(0) == "a" && !end.HasSelection, "selection: Ctrl+End, Shift+→, Backspace ×2 — no crash, two characters gone");

        var emoji = Open(Put(dir, "emoji.txt", "a👀b\n"));
        emoji.SetCursor(0, 3);
        emoji.Backspace();
        Check(emoji.LineText(0) == "ab" && emoji.Cx == 1, "editing: Backspace removes a whole emoji, not half of it");
        emoji.Undo();
        emoji.SetCursor(0, 4);
        emoji.MoveLeft();
        emoji.MoveLeft();
        Check(emoji.Cx == 1, "editing: the cursor steps over an emoji in one move");
        emoji.Delete();
        Check(emoji.LineText(0) == "ab", "editing: Delete removes a whole emoji");

        var tabs = Open(Put(dir, "Makefile", "all:\n\techo hi\n"));
        tabs.SetCursor(1, 0);
        tabs.End();
        Check(tabs.LineText(1) == "\techo hi" && tabs.CursorColumn == 11 && tabs.Cx == 8,
            "tabs: a tab stays a tab; the cursor column counts it to the next stop");
        tabs.InsertNewline();
        Check(tabs.LineText(2) == "\t", "tabs: auto-indent repeats the leading tab");
        tabs.InsertTab();
        Check(tabs.IndentWithTabs && tabs.LineText(2) == "\t\t", "tabs: Tab inserts a tab in a Makefile");
        var spaces = Open(Put(dir, "s.py", "def f():\n  x = 1\n"));
        spaces.SetCursor(1, 2);
        spaces.InsertTab();
        Check(!spaces.IndentWithTabs && spaces.LineText(1) == "    x = 1", "tabs: Tab inserts spaces to the next stop in a space-indented file");

        var cols = Open(Put(dir, "cols.txt", "\tab\n        cd\n界界x\n"));
        cols.SetCursor(0, 3);
        cols.MoveDown();
        Check(cols.Cx == 6, "tabs: moving down keeps the display column, not the character index");
        cols.SetCursorAtCell(2, 3);
        Check(cols.Cx == 1, "cells: a click on the right half of a wide character puts the cursor before it");

        var nfd = Open(Put(dir, "nfd.txt", "cafe\u0301 bar\n"));
        var stops = new List<int>();
        nfd.WordRight(); stops.Add(nfd.Cx);
        nfd.WordRight(); stops.Add(nfd.Cx);
        nfd.WordLeft(); stops.Add(nfd.Cx);
        nfd.WordLeft(); stops.Add(nfd.Cx);
        Check(stops.SequenceEqual(new[] { 5, 9, 6, 0 }), "words: an accent written as a combining mark belongs to its word");
        nfd.SetCursor(0, 5);
        nfd.DeleteWordBackward();
        Check(nfd.LineText(0) == " bar", "words: Alt+Backspace deletes the whole word, accent included");
        var hindi = Open(Put(dir, "hi.txt", "हिन्दी text\n"));
        hindi.WordRight();
        Check(hindi.Cx == 6, "words: an Indic word with vowel signs is one word");
        var wide = Open(Put(dir, "wide.txt", "a" + string.Concat(Enumerable.Repeat("界", 60)) + "\n"), 40, 10);
        wide.End();
        wide.SetCursorAtCell(0, wide.ScrollX);
        Check(wide.ScrollX > 0 && wide.CursorColumn > wide.ScrollX, "click: on the ‹ cell the cursor lands on the first character in view");

        var paste = Open(Put(dir, "paste.txt", "hello world\n"));
        paste.SetCursor(0, 5);
        int pasted = paste.InsertText("AB\r\nC\tD");
        Check(pasted == 6 && paste.LineText(0) == "helloAB" && paste.LineText(1) == "C\tD world" && paste.Cy == 1 && paste.Cx == 3,
            "paste: CRLF becomes a line break, a tab stays, the text is spliced in at the cursor");
        paste.Undo();
        Check(paste.LineCount == 2 && paste.LineText(0) == "hello world", "paste: one undo removes the whole paste");
        paste.SelectAll();
        Check(paste.SelectedText() == "hello world\n", "select: Ctrl+A selects the whole text");

        var words = Open(Put(dir, "words.txt", "foo bar  \nsecond line\n"));
        words.End();
        words.DeleteWordBackward();
        Check(words.LineText(0) == "foo ", "word delete: trailing spaces + the word before the cursor");
        words.DeleteWordBackward();
        words.Undo();
        Check(words.LineText(0) == "foo bar  ", "word delete: a run of deletions is a single undo step");

        var sel = Open(Put(dir, "sel.txt", "hello brave new world\nsecond line here\n\nlast para\n"));
        sel.SetCursor(0, 8);
        sel.SelectWord();
        Check(sel.SelectedText() == "brave", "select: double-click selects the whole word");
        sel.InsertChar('X');
        Check(sel.LineText(0) == "hello X new world" && !sel.HasSelection, "select: typing replaces the selection");
        sel.SetCursor(0, 0);
        sel.SetAnchorHere();
        sel.SetCursor(1, 6);
        sel.Backspace();
        Check(sel.LineText(0) == " line here" && sel.Cy == 0 && sel.Cx == 0, "select: Backspace deletes a multi-line selection");
        sel.ParagraphDown();
        Check(sel.Cy == 2, "nav: Option+↓ — to the next paragraph start");
    }

    private static void TestUndo()
    {
        string dir = TempDir("undo");
        var ed = Open(Put(dir, "undo.txt", "one\ntwo\n"));
        Type(ed, "XY");
        Check(ed.LineText(0) == "XYone" && ed.Dirty && ed.CanUndo, "undo: typing is marked dirty and can be undone");
        ed.Undo();
        Check(ed.LineText(0) == "one" && !ed.Dirty && ed.CanRedo, "undo: continuous typing is one step, back to clean");
        ed.Redo();
        Check(ed.LineText(0) == "XYone" && ed.Dirty, "undo: redo brings the edit back");
        ed.Undo();
        ed.SetCursor(1, 0);
        ed.InsertChar('Z');
        Check(!ed.CanRedo && ed.LineText(1) == "Ztwo", "undo: a new edit after undo clears redo");
        ed.Undo();
        ed.SetCursor(0, 0);
        ed.SelectWord();
        ed.DeleteSelection();
        ed.Undo();
        Check(ed.LineText(0) == "one" && ed.SelectedText() == "one", "undo: rolling back a deletion restores text and selection");

        string rp = Put(dir, "reload.txt", "abcd");
        var re = Open(rp);
        re.SetCursor(0, 2);
        File.WriteAllText(rp, "a😀b");
        File.SetLastWriteTimeUtc(rp, DateTime.UtcNow.AddSeconds(5));
        re.Reload();
        re.Undo();
        re.Redo();
        re.InsertChar('x');
        re.Save();
        Check(File.ReadAllText(rp) == "ax😀b", "undo: redoing a reload puts the cursor on a character boundary");

        string concPath = Put(dir, "conc.txt", string.Join("\n", Enumerable.Range(0, 200_000).Select(i => $"line {i}")));
        var conc = Open(concPath);
        var model = Open(concPath);
        model.SetCursor(100, 0);
        model.InsertText("A\nB\nC");
        var stateA = conc.Lines.ToArray();
        var stateB = model.Lines.ToArray();
        using (var stop = new CancellationTokenSource())
        {
            int edits = 0;
            var editing = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    conc.SetCursor(100, 0);
                    conc.InsertText("A\nB\nC");
                    conc.Undo();
                    Interlocked.Increment(ref edits);
                }
            });
            while (Volatile.Read(ref edits) == 0) Thread.Yield();
            bool consistent = true;
            int taken = 0;
            for (int i = 0; i < 50; i++)
                if (conc.SnapshotLines() is { } snap)
                {
                    taken++;
                    consistent &= snap.SequenceEqual(stateA) || snap.SequenceEqual(stateB);
                }
            stop.Cancel();
            editing.Wait();
            Check(consistent && taken == 50, $"recovery: copies taken during edits on another thread are real states of the text ({taken}/50)");
        }

        var saved = Open(Put(dir, "saved.txt", "\n"));
        Type(saved, "ab");
        saved.Save();
        Type(saved, "c");
        saved.Undo();
        Check(saved.LineText(0) == "ab" && !saved.Dirty, "undo: a save ends the undo step — undo stops at the saved text");

        // Memory: the history keeps touched lines only, never copies of the whole file.
        string big = Put(dir, "big.txt", string.Join("\n", Enumerable.Range(0, 200_000).Select(i => $"line {i}")));
        var bigEd = Open(big);
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 200; i++)
        {
            bigEd.SetCursor(i * 997, 0);
            bigEd.InsertChar('#');
        }
        long grown = GC.GetTotalMemory(true) - before;
        Check(grown < 20_000_000 && bigEd.LineText(997).StartsWith('#'),
            $"undo: 200 edits in a 200k-line file keep the history small ({grown / 1024} KB)");
        for (int i = 0; i < 200; i++) bigEd.Undo();
        Check(!bigEd.Dirty, "undo: all 200 steps undo back to the saved text");
    }

    private static void TestDiskChanges()
    {
        string dir = TempDir("disk");
        string path = Put(dir, "f.txt", "1\n2\n3\n4\n5\n");
        var ed = Open(path);
        ed.SetCursor(3, 0);
        Check(ed.CheckDisk() == TextEditor.DiskState.Same, "disk: an untouched file is the same version");
        File.WriteAllText(path, "new a\nnew b\n1\n2\n3\n4\n5\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Check(ed.CheckDisk() == TextEditor.DiskState.Changed, "disk: a change by someone else is noticed");
        ed.Reload();
        Check(ed.LineCount == 8 && ed.Cy == 5 && ed.LineText(ed.Cy) == "4" && !ed.Dirty && ed.CheckDisk() == TextEditor.DiskState.Same,
            "disk: reload takes the new text, the cursor stays on its line");
        ed.Undo();
        Check(ed.LineText(0) == "1" && ed.Dirty, "disk: undo after a reload brings the old text back as an edit");
        File.Delete(path);
        Check(ed.CheckDisk() == TextEditor.DiskState.Missing, "disk: a deleted file is noticed");

        string ra = Put(dir, "ra.txt", "line1 original\nline2\n");
        var moved = Open(ra);
        string rb = Path.Combine(dir, "rb.txt");
        File.Move(ra, rb);
        File.AppendAllText(rb, "agent edit\n");
        moved.RenameTo(rb);
        Check(moved.FilePath == rb && moved.CheckDisk() == TextEditor.DiskState.Changed,
            "disk: a file moved and then edited outside — the tab follows and still sees the edit");

        var recovered = new TextEditor();
        recovered.OpenMissing(Path.Combine(dir, "gone.txt"));
        recovered.ReplaceContent(new List<string> { "kept", "" });
        Check(recovered.Dirty && recovered.MissingOnDisk && Recovery.Save(new[] { recovered }) == 1,
            "recovery: a dirty buffer is written to the recovery folder");
        var loaded = Recovery.Load();
        Check(loaded.Count == 1 && loaded[0].Path == recovered.FilePath && loaded[0].Lines.SequenceEqual(new[] { "kept", "" }),
            "recovery: the next start reads it back — path and text");
        foreach (var r in loaded) Recovery.Delete(r.File);
        Check(Recovery.Load().Count == 0, "recovery: restored files are removed");

        var longOne = new TextEditor();
        longOne.OpenMissing(Path.Combine(dir, new string('n', 240) + ".txt"));
        longOne.ReplaceContent(new List<string> { "long" });
        Check(Recovery.Save(new[] { longOne, recovered }) == 2 && Recovery.HasFiles
              && Recovery.Save(new[] { recovered }) == 1 && Recovery.Load().Count == 1,
            "recovery: any file name fits; a new backup replaces the session's earlier ones");
        Recovery.Clear();
        Check(!Recovery.HasFiles && Recovery.Load().Count == 0, "recovery: a normal exit leaves no backups behind");

        string rdir = Recovery.Dir;
        File.WriteAllText(Path.Combine(rdir, "20200101-000000-000-aaaaaaaa-000001.txt"), $"ltt-recovery 1\n1\n{rb}\nolder");
        File.WriteAllText(Path.Combine(rdir, "20200101-000001-000-aaaaaaaa-000002.txt"), $"ltt-recovery 1\n1\n{rb}\nnewer");
        File.WriteAllText(Path.Combine(rdir, "20200101-000002-000-aaaaaaaa-000003.txt"), $"ltt-recovery 1\n3\n{ra}\ncut\nshort");
        var picked = Recovery.Load();
        Check(picked.Count == 1 && picked[0].Lines.SequenceEqual(new[] { "newer" })
              && Directory.GetFiles(rdir, "*.txt").Length == 2,
            "recovery: only the newest backup of a file comes back; a backup cut short is never restored");
        foreach (var f in Directory.GetFiles(rdir)) File.Delete(f);
    }

    private static void TestScrolling()
    {
        string dir = TempDir("scroll");
        var ed = Open(Put(dir, "big.txt", string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"))));
        ed.ScrollView(30);
        Check(ed.ScrollY == 30 && ed.Cy == 0, "editor: the wheel scrolls the view, cursor stays");
        ed.ScrollView(999);
        Check(ed.ScrollY == ed.LineCount - ed.ViewHeight, "editor: scrolling stops with the last line at the bottom edge");

        int wheelNum = 0;
        var perNotch = new List<int>();
        for (int i = 0; i < 5; i++) perNotch.Add(App.TreeWheelSteps(ref wheelNum, true));
        Check(perNotch.SequenceEqual(new[] { 0, 1, 0, 1, 1 }) && wheelNum == 0, "tree wheel: 5 notches down give exactly 3 rows");

        var wheelEd = Open(Put(dir, "wheel.txt", string.Join("\n", Enumerable.Range(1, 5000).Select(i => $"line {i}"))), height: 10);
        var perNotchY = new List<int>();
        for (int i = 0; i < 5; i++) { wheelEd.WheelScroll(true); perNotchY.Add(wheelEd.ScrollY); }
        Check(perNotchY.SequenceEqual(new[] { 1, 2, 3, 4, 6 }), "wheel: five notches move exactly six lines");
        for (int i = 0; i < 5; i++) wheelEd.WheelScroll(false);
        Check(wheelEd.ScrollY == 0, "wheel: symmetric upward");

        var hz = Open(Put(dir, "long.txt", new string('a', 200) + "\nshort\n" + new string('b', 100) + "\n\tx = find(me);\n界" + new string('c', 100) + "\n"), 80, 10);
        hz.End();
        Check(hz.ScrollX == 122 && hz.CursorColumn - hz.ScrollX == 78, "hscroll: End keeps the cursor off the › cell");
        hz.MoveDown();
        Check(hz.Cx == 5 && hz.ScrollX == 0, "hscroll: on a short line the view returns to column 0");
        hz.MoveDown();
        Check(hz.Cx == 100 && hz.ScrollX == 22, "hscroll: past the first screen the cursor sits at the right edge");
        hz.GoToLine(3, 5);
        Check(hz.Cx == 5 && hz.CursorColumn == 8 && hz.LineText(3)[hz.Cx] == 'f', "hit jump: an index into the line lands on the match, tabs counted");
        hz.GoToLine(4, 101);
        Check(hz.CursorColumn == 102 && hz.ScrollX == 102 - 40, "hit jump: columns count the wide character as two cells");
        hz.DocHome();
        for (int i = 0; i < 100; i++) hz.WheelSideways(right: true);
        Check(hz.ScrollX == 120 && hz.Cx == 0, "hscroll: sideways scrolling stops at the longest on-screen line");
    }

    private static void TestSearch()
    {
        string tmp = TempDir("search");
        Put(tmp, "a.txt", "first line\nline manager here\n");
        Put(tmp, "sub/b.cs", "// code\nclass LineManager { }\n");
        Put(tmp, "packages/web/src/app.ts", "const manager = 1;\n");
        Put(tmp, "bin/notes.txt", "manager notes\n");
        var hits = FuzzySearch.Search(tmp, "mngr");
        Check(hits.Hits.Count == 4 && hits.Hits.Any(h => h.RelativePath.StartsWith("packages")) && hits.Hits.Any(h => h.RelativePath.StartsWith("bin")),
            "search: folders named packages/bin are searched (only .git, node_modules & co are skipped)");
        Check(FuzzySearch.Search(Path.Combine(tmp, "sub"), "mngr").Hits.Single().RelativePath == "b.cs",
            "folder search: a selected folder scopes results to its descendants");
        Check(FuzzySearch.SearchMany(new[] { tmp, Path.Combine(tmp, "sub") }, "mngr").Hits.Count == 4,
            "search: a root inside another root is searched once — no duplicate hits");

        var broad = FuzzySearch.Search(tmp, "m");
        static string Key(SearchHit h) => h.RelativePath + ":" + h.LineNumber;
        Check(!broad.Truncated && FuzzySearch.Refine(broad.Hits, "mngr").Select(Key).Order().SequenceEqual(hits.Hits.Select(Key).Order()),
            "search: refining complete results equals a full search");

        string many = TempDir("search-many");
        Put(many, "noise.txt", string.Join("\n", Enumerable.Range(0, 6000).Select(i => $"m{i} a n a g e r")));
        Put(many, "zz.txt", "the manager is here\n");
        var top = FuzzySearch.Search(many, "manager");
        Check(top.Truncated && top.Hits.Count == 500 && top.Hits[0].RelativePath == "zz.txt",
            "search: the exact match outranks thousands of fuzzy ones and the cap is reported");

        string huge = TempDir("search-huge");
        Put(huge, "big.log", "zebra\n" + new string('x', 9_000_000));
        Put(huge, "small.txt", "zebra\n");
        var hugeHits = FuzzySearch.Search(huge, "zebra");
        Check(hugeHits.Hits.Count == 1 && hugeHits.SkippedLarge == 1
              && FuzzySearch.SearchFile(Path.Combine(huge, "big.log"), "big.log", "zebra").Hits.Count == 1,
            "search: a folder search reports files skipped for size; searching that file itself finds the match");

        var buffers = new Dictionary<string, string[]>(Paths.Comparer) { [Path.Combine(tmp, "a.txt")] = new[] { "unsaved zebra" } };
        Check(FuzzySearch.Search(tmp, "zebra", buffers: buffers).Hits.Count == 1 && FuzzySearch.Search(tmp, "zebra").Hits.Count == 0,
            "search: files with unsaved edits are searched in their buffers");

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Check(FuzzySearch.Search(tmp, "mngr", ct: cts.Token).Hits.Count == 0, "search: a pre-cancelled scan returns at once");
        }

        if (!OperatingSystem.IsWindows())
        {
            string loop = TempDir("search-loop");
            Put(loop, "x.txt", "needle\n");
            Directory.CreateSymbolicLink(Path.Combine(loop, "self"), loop);
            Directory.CreateSymbolicLink(Path.Combine(loop, "again"), loop);
            var t = Task.Run(() => FuzzySearch.Search(loop, "needle"));
            Check(t.Wait(5000) && t.Result.Hits.Count == 1, "search: symlinked folders are not followed — no loops, no duplicates");
        }
    }

    private static void TestSearchRefine()
    {
        // Refining keeps only hits of the shorter query, so a longer token must never
        // match a line the shorter one did not.
        var rnd = new Random(11);
        bool monotonic = true;
        for (int n = 0; n < 20_000 && monotonic; n++)
        {
            string line = new string(Enumerable.Range(0, rnd.Next(1, 40)).Select(_ => "abcx "[rnd.Next(5)]).ToArray());
            string token = new string(Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => "abc"[rnd.Next(3)]).ToArray());
            char more = "abc"[rnd.Next(3)];
            if (FuzzySearch.ScoreToken(token + more, line, out _, out _) >= 0)
                monotonic = FuzzySearch.ScoreToken(token, line, out _, out _) >= 0;
        }
        Check(monotonic && FuzzySearch.ScoreToken("ab", "axxxxxxxxxxbc", out _, out _) >= 0,
            "search: one more typed character never finds a line the shorter query missed");
    }

    private static void TestGitSearch()
    {
        if (GitRepo("gitsearch") is not { } repo) { Check(true, "git search: skipped (git unavailable)"); return; }
        Put(repo, ".gitignore", "ignored/\n*.log\n");
        Put(repo, "src/keep.cs", "needle\n");
        Put(repo, "ignored/x.cs", "needle\n");
        Put(repo, "debug.log", "needle\n");
        Put(repo, "packages/lib/index.js", "needle\n");
        FuzzySearch.InvalidateFileLists();
        var hits = FuzzySearch.Search(repo, "needle").Hits.Select(h => h.RelativePath.Replace('\\', '/')).Order().ToList();
        Check(hits.SequenceEqual(new[] { "packages/lib/index.js", "src/keep.cs" }),
            "git search: .gitignore decides what is searched (untracked files included)");

        // A root inside another is searched for what the outer listing leaves out.
        string inner = Path.Combine(repo, "inner");
        Directory.CreateDirectory(inner);
        if (Run(inner, "git", "init -q"))
        {
            Put(inner, "i.txt", "needle\n");
            FuzzySearch.InvalidateFileLists();
            var both = FuzzySearch.SearchMany(new[] { repo, inner }, "needle").Hits;
            var ignoredRoot = FuzzySearch.SearchMany(new[] { Path.Combine(repo, "ignored") }, "needle").Hits;
            Check(both.Count(h => h.FilePath.EndsWith("i.txt")) == 1 && both.Count(h => h.FilePath.EndsWith("keep.cs")) == 1
                  && ignoredRoot.Count == 1,
                "git search: a nested repository and a folder git ignores are searched when they are roots, each file once");
        }

        // A plain folder holding a repository: that repository's .gitignore still counts.
        string plain = TempDir("plain");
        string proj = Path.Combine(plain, "proj");
        Directory.CreateDirectory(proj);
        if (Run(proj, "git", "init -q"))
        {
            Put(proj, ".gitignore", "secret.txt\n");
            Put(proj, "secret.txt", "needle\n");
            Put(proj, "open.txt", "needle\n");
            Put(plain, "top.txt", "needle\n");
            var found = FuzzySearch.Search(plain, "needle").Hits.Select(h => Path.GetFileName(h.FilePath)).Order().ToList();
            Check(found.SequenceEqual(new[] { "open.txt", "top.txt" }),
                "search: a repository inside a plain folder keeps its .gitignore");
        }
    }

    private static void TestTree()
    {
        string tmp = TempDir("tree");
        Put(tmp, "b.txt", "b");
        Put(tmp, "A.txt", "a");
        Put(tmp, "zdir/inner/deep.txt", "d");
        Put(tmp, "adir/x.txt", "x");
        Put(tmp, "..foo", "dots");
        var tree = new FileTree(tmp + Path.DirectorySeparatorChar);
        var names = tree.Roots[0].Children.Select(c => c.Name).ToList();
        Check(names.SequenceEqual(new[] { "adir", "zdir", "..foo", "A.txt", "b.txt" }),
            "tree: folders first, then files; equal sizes by name");
        Check(!tree.Roots[0].FullPath.EndsWith(Path.DirectorySeparatorChar) && !tree.AddRoot(tmp),
            "tree: a root given with a trailing slash is the same root");
        tree.SelectPath(Path.Combine(tmp, "..foo"));
        Check(tree.Selected?.Name == "..foo", "tree: a file named ..foo is inside the root");

        tree.SelectPath(Path.Combine(tmp, "zdir", "inner", "deep.txt"));
        tree.SelectPath(Path.Combine(tmp, "zdir"));
        tree.CollapseOrAscend(); // zdir collapsed, zdir/inner still expanded inside it
        Put(tmp, "new.txt", "n");
        tree.Refresh();
        var zdir = tree.Roots[0].Children.First(c => c.Name == "zdir");
        Check(tree.Visible.Any(v => v.Name == "new.txt") && zdir.Children.First(c => c.Name == "inner").Expanded,
            "tree: a refresh keeps folders expanded inside collapsed ones");
        Put(tmp, "adir/y.txt", "y");
        Check(!tree.RefreshDirs(new[] { Path.Combine(tmp, "adir") }), "tree: re-reading a folder that was never loaded does nothing");

        var tree2 = new FileTree(new[] { tmp, Path.Combine(tmp, "adir") });
        Check(tree2.Roots.Count == 2 && !tree2.AddRoot(tmp), "multiroot: two hierarchies, no duplicate roots");
        Check(tree2.RemoveRoot(tree2.Roots[1]) && !tree2.RemoveRoot(tree2.Roots[0]), "multiroot: a root is removed, the last one stays");

        string sep = Path.DirectorySeparatorChar.ToString();
        var (c1, g1) = PathCompletion.Compute(tmp + sep + "zd", tmp);
        Check(c1.Count == 1 && c1[0] == tmp + sep + "zdir" + sep && g1 == "ir" + sep, "complete: fragment completes to the folder with ghost text");
        Check(PathCompletion.Compute("/no/such/folder/x", tmp).Candidates.Count == 0, "complete: a nonexistent directory — empty");
    }

    private static void TestGitTree()
    {
        if (GitRepo("gittree") is not { } repo) { Check(true, "git tree: skipped (git unavailable)"); return; }
        Put(repo, ".gitignore", "*.log\nbin/\n");
        Put(repo, "tracked.txt", "v1\n");
        Run(repo, "git", "add -A");
        Run(repo, "git", "-c user.email=t@t -c user.name=t commit -q -m files");
        Put(repo, "tracked.txt", "v2\n");
        Put(repo, "src/new.cs", "x\n");
        Put(repo, "debug.log", "log\n");
        Put(repo, "bin/out.dll", "b");
        Put(repo, "keep/debug.log", "log\n");
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(repo, "linkdir"), Path.Combine(repo, "src"));

        var tree = new FileTree(repo);
        bool Ignored(string name) => tree.Visible.First(v => v.Name == name).Ignored;
        Check(Ignored("bin") && Ignored("debug.log") && Ignored(".git") && Ignored(".gitignore") && !Ignored("tracked.txt"),
            "gitignore: ignored entries (and .git, .gitignore) are dimmed, a symlinked folder does not break it");
        var order = tree.Roots[0].Children.Select(c => c.Name).ToList();
        Check(order.IndexOf("bin") > order.IndexOf("tracked.txt"), "gitignore: dimmed entries come last");
    }

    private static void TestSizes()
    {
        string tmp = TempDir("size");
        Put(tmp, "small.txt", "1");
        PutBytes(tmp, "mid.txt", new byte[100]);
        PutBytes(tmp, "big.txt", new byte[1000]);
        PutBytes(tmp, "tinyDir/f", new byte[10]);
        PutBytes(tmp, "hugeDir/f", new byte[5000]);
        PutBytes(tmp, "hugeDir/nested/g", new byte[500]);
        // .gitignore is always a dimmed (ignored) entry — biggest of all, it must still come last.
        PutBytes(tmp, ".gitignore", new byte[10_000]);

        var tree = new FileTree(tmp);
        var kids = tree.Roots[0].Children;
        TreeNode Kid(string name) => tree.Roots[0].Children.First(c => c.Name == name);
        Check(kids.Select(c => c.Name).SequenceEqual(new[] { "hugeDir", "tinyDir", "big.txt", "mid.txt", "small.txt", ".gitignore" }),
            "size sort: folders first, largest first in each block, dimmed last");
        Check(Kid("hugeDir").Size == 5500 && Kid("big.txt").Size == 1000 && Kid("big.txt").SizeExact && Kid("hugeDir").SizeExact,
            "size: a folder's recursive total, a file's length — exact right away for small trees");
        Check(App.FormatSize(0, true) == "0" && App.FormatSize(0, false) == "…"
              && App.FormatSize(99, true) == "99" && App.FormatSize(512, true) == "510"
              && App.FormatSize(999, true) == "1K" && App.FormatSize(1536, true) == "1.5K"
              && App.FormatSize(999_000, true) == "980K" && App.FormatSize(1_048_576, true) == "1M"
              && App.FormatSize(1_610_612_736, true) == "1.5G" && App.FormatSize(1536, false) == "≥1.5K",
            "size display: two significant digits with a lower-bound marker");

        using (var sizer = new FolderSizer(new[] { (Kid("hugeDir"), Kid("hugeDir").WriteUtc) }))
        {
            sizer.Step(1);
            bool lower = sizer.Snapshot()[0].Size <= 5500;
            while (!sizer.Step(2)) { }
            Check(lower && sizer.Snapshot()[0] is { Size: 5500, Complete: true },
                "sizing: partial steps give lower bounds and converge to the exact total");
        }

        static void Settle(FileTree t)
        {
            while (t.TakeUnfinishedSizing() is { } job)
            {
                job.Sizer.Step(int.MaxValue);
                t.CompleteSizing(job.Parent, job.Sizer);
                job.Sizer.Dispose();
            }
        }
        // A change next to a folder keeps its cached size; a change anywhere below it
        // counts it again (shown approximate meanwhile) and re-sorts.
        Put(tmp, "beside.txt", "b");
        tree.RefreshDirs(new[] { tmp });
        Check(Kid("hugeDir") is { Size: 5500, SizeExact: true } && tree.TakeUnfinishedSizing() == null,
            "sizing cache: a change beside a folder keeps its counted size");
        PutBytes(tmp, "tinyDir/deep/blob", new byte[9000]);
        tree.RefreshDirs(new[] { Path.Combine(tmp, "tinyDir", "deep") });
        bool counting = !Kid("tinyDir").SizeExact;
        Settle(tree);
        Check(counting && Kid("tinyDir") is { Size: 9010, SizeExact: true } && kids[0].Name == "tinyDir",
            "sizing: a change deep inside a folder counts it again, up to the root, and re-sorts");
        PutBytes(tmp, "hugeDir/nested/h", new byte[700]);
        tree.Refresh();
        Settle(tree);
        Check(Kid("hugeDir").Size == 6200, "sizing: a full refresh counts every folder again");

        PutBytes(tmp, "tinyDir/c", new byte[7]);
        tree.RefreshDirs(new[] { Path.Combine(tmp, "tinyDir") });
        var early = tree.TakeUnfinishedSizing()!.Value;
        early.Sizer.Step(int.MaxValue); // counted before the next change
        PutBytes(tmp, "tinyDir/d", new byte[9]);
        tree.RefreshDirs(new[] { Path.Combine(tmp, "tinyDir") });
        Settle(tree);
        tree.CompleteSizing(early.Parent, early.Sizer); // lands late
        early.Sizer.Dispose();
        Check(Kid("tinyDir").Size == 9026, "sizing: a count that began earlier never overwrites a newer one");

        // A folder too big for the synchronous budget is counted in the background;
        // re-reading its parent replaces the queued count instead of piling up another.
        string bulk = TempDir("size-bulk");
        for (int i = 0; i < 2000; i++) PutBytes(bulk, $"many/f{i}", new byte[1]);
        var bt = new FileTree(bulk);
        bool approximate = !bt.Roots[0].Children[0].SizeExact;
        bt.Refresh();
        var job = bt.TakeUnfinishedSizing();
        bool single = job != null && bt.TakeUnfinishedSizing() == null;
        job?.Sizer.Step(int.MaxValue);
        if (job is { } j) bt.CompleteSizing(j.Parent, j.Sizer);
        Check(approximate && single && bt.Roots[0].Children[0] is { Size: 2000, SizeExact: true },
            "sizing: a big folder is counted in the background, once, and settles exact");
        job?.Sizer.Dispose();
    }

    private static void TestCopyMove()
    {
        string tmp = TempDir("copy");
        string Free(string name, bool isDir = false) => Path.GetFileName(FileOps.FreeName(tmp, name, isDir));
        Put(tmp, "a.txt", "a");
        Put(tmp, "a 2.txt", "a");
        Put(tmp, ".env", "e");
        Directory.CreateDirectory(Path.Combine(tmp, "v1.2"));
        Check(Free("a.txt") == "a 3.txt" && Free("new.txt") == "new.txt" && Free(".env") == ".env 2" && Free("v1.2", isDir: true) == "v1.2 2",
            "paste names: a taken name gets a number, before a file's extension");

        // Nested files, an executable script; on Unix also links (copied as links) and a pipe (skipped).
        string src = Path.Combine(tmp, "src");
        Put(src, "top.txt", "top");
        Put(src, "deep/er/leaf.txt", "leaf");
        string script = Put(src, "run.sh", "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(src, "link.txt"), "top.txt");
            Directory.CreateSymbolicLink(Path.Combine(src, "loop"), src); // followed, the copy would never end
            Run(src, "mkfifo", "pipe");
            File.CreateSymbolicLink(Path.Combine(tmp, "dangling.txt"), "nowhere");
            Check(Free("dangling.txt") == "dangling 2.txt", "paste names: a dangling link takes its name too");
        }
        string dst = Path.Combine(tmp, "dst", "src");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var progress = new FileOps.CopyProgress();
        FileOps.Copy(src, dst, progress, CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(dst, "deep", "er", "leaf.txt")) == "leaf" && File.ReadAllText(Path.Combine(dst, "top.txt")) == "top"
              && progress.Failed == 0,
            "copy: a folder with everything inside");
        if (!OperatingSystem.IsWindows())
            Check(new FileInfo(Path.Combine(dst, "link.txt")).LinkTarget == "top.txt" && new DirectoryInfo(Path.Combine(dst, "loop")).LinkTarget == src
                  && File.GetUnixFileMode(Path.Combine(dst, "run.sh")).HasFlag(UnixFileMode.UserExecute)
                  && progress.Skipped == 1 && !Path.Exists(Path.Combine(dst, "pipe")),
                "copy: links stay links, modes are kept, a pipe is skipped");

        bool refused = false;
        try { FileOps.Copy(src, dst, new FileOps.CopyProgress(), CancellationToken.None); }
        catch (IOException) { refused = true; }
        var stopped = new FileOps.CopyProgress();
        FileOps.Copy(src, Path.Combine(tmp, "stopped"), stopped, new CancellationToken(canceled: true));
        Check(refused && !Path.Exists(Path.Combine(tmp, "stopped")) && stopped.Copied == 0,
            "copy: never onto an existing path; a stopped copy makes nothing more");

        FileOps.Relocate(dst, Path.Combine(tmp, "moved"), isDir: true, new FileOps.CopyProgress(), CancellationToken.None);
        FileOps.Relocate(Path.Combine(tmp, "a.txt"), Path.Combine(tmp, "moved", "a.txt"), isDir: false, new FileOps.CopyProgress(), CancellationToken.None);
        Check(!Path.Exists(dst) && File.ReadAllText(Path.Combine(tmp, "moved", "deep", "er", "leaf.txt")) == "leaf"
              && !Path.Exists(Path.Combine(tmp, "a.txt")) && File.ReadAllText(Path.Combine(tmp, "moved", "a.txt")) == "a",
            "move: a folder and a file go to another folder");
    }

    private static void TestWatcher()
    {
        string tmpW = TempDir("watch");
        Put(tmpW, "seed.txt", "seed\n");
        static WatchBatch? WaitDue(FolderWatcher w, Func<WatchBatch, bool>? until = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                w.PollIfDue();
                if (w.TryConsumeDue(out var b) && (until == null || until(b))) return b;
                Thread.Sleep(20);
            }
            return null;
        }

        using var watcher = new FolderWatcher(debounceMs: 50, maxDelayMs: 500, pollIntervalMs: 100);
        watcher.SyncRoots(new[] { tmpW });
        Check(watcher.WatchedRoots.Contains(tmpW) && watcher.UnwatchableRoots.Count == 0, "watch: the root is being watched");
        for (int i = 0; i < 25; i++) { watcher.PollIfDue(); Thread.Sleep(20); }
        while (watcher.TryConsumeDue(out _)) { }

        Put(tmpW, "watch-new.txt", "came from outside\n");
        var created = WaitDue(watcher, b => b.Dirs.Count > 0 || b.Overflow);
        Check(created != null && (created.Overflow || created.Dirs.Contains(tmpW, Paths.Comparer)),
            "watch: an external file creation names the folder that changed");
        var tree = new FileTree(tmpW);
        Check(tree.Visible.Any(v => v.Name == "watch-new.txt"), "watch: the tree shows the new file");

        File.Move(Path.Combine(tmpW, "watch-new.txt"), Path.Combine(tmpW, "watch-renamed.txt"));
        Check(WaitDue(watcher, b => b.Dirs.Count > 0 || b.Overflow) != null, "watch: an external rename is noticed");
        tree.Refresh();
        Check(tree.Visible.Any(v => v.Name == "watch-renamed.txt") && !tree.Visible.Any(v => v.Name == "watch-new.txt"),
            "watch: the tree shows the new name");

        Directory.CreateDirectory(Path.Combine(tmpW, ".git"));
        while (watcher.TryConsumeDue(out _)) { }
        Thread.Sleep(200);
        while (watcher.TryConsumeDue(out _)) { }
        File.WriteAllText(Path.Combine(tmpW, ".git", "index.lock"), "x");
        var gitOnly = WaitDue(watcher);
        Check(gitOnly == null || (!gitOnly.Overflow && gitOnly.Dirs.All(d => Paths.IsSameOrInside(d, Path.Combine(tmpW, ".git"))))
              || watcher.WatchedRoots.Count == 0,
            "watch: git's own files (index.lock) name only the .git folder, which the tree has not loaded");
        var burst = new HashSet<string>(Enumerable.Range(0, 3000).Select(i => $"/r/node_modules/p{i}/lib"), Paths.Comparer) { "/r/src" };
        var collapsed = FolderWatcher.Collapse(burst, 500);
        Check(collapsed.SetEquals(new[] { "/r/node_modules", "/r/src" }),
            "watch: a burst of thousands of folders shrinks to the folders above it");

        watcher.MarkDirty("test mark");
        Check(!watcher.TryConsumeDue(out _), "watch: no batch is handed out before the debounce");
        Check(WaitDue(watcher) != null && !watcher.TryConsumeDue(out _), "watch: a due batch is handed out exactly once");
        watcher.SyncRoots(Array.Empty<string>());
        Check(watcher.WatchedRoots.Count == 0, "watch: removing roots stops the watching");
    }

    private static void TestMisc()
    {
        string tmp = TempDir("misc");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(tmp, "proj", "src") };
        Check(App.IsDeleteProtected(roots[0], roots) && App.IsDeleteProtected(Path.Combine(tmp, "proj"), roots)
              && !App.IsDeleteProtected(Path.Combine(tmp, "proj", "src", "a.cs"), roots)
              && !App.IsDeleteProtected(Path.Combine(tmp, "other"), roots),
            "delete guard: a root and every folder above it are protected, files inside are not");
        Check(App.IsDeleteProtected(home, Array.Empty<string>()) && App.IsDeleteProtected(Path.GetPathRoot(home)!, Array.Empty<string>())
              && !App.IsDeleteProtected(Path.Combine(home, "some-file.txt"), Array.Empty<string>()),
            "delete guard: home, its ancestors and the filesystem root are protected");
        Check(App.IsDeleteProtected(Path.Combine(Paths.Real(tmp), "proj"), roots),
            "delete guard: a protected folder reached through a symlinked path is still protected");
        if (OperatingSystem.IsMacOS())
            Check(Paths.Same("/x/caf\u00E9.txt", "/x/cafe\u0301.txt") && Paths.IsInside("/x/cafe\u0301/a", "/x/caf\u00E9")
                  && Paths.Comparer.GetHashCode("/x/caf\u00E9.txt") == Paths.Comparer.GetHashCode("/x/cafe\u0301.txt"),
                "paths: on macOS both Unicode forms of a name are one file");
        Check(Paths.IsInside(Path.Combine(tmp, "a", "b"), Path.Combine(tmp, "a")) && !Paths.IsInside(Path.Combine(tmp, "ab"), Path.Combine(tmp, "a"))
              && Paths.IsInside("/x", "/") && !Paths.IsInside(Path.Combine(tmp, "a"), Path.Combine(tmp, "a")),
            "paths: inside means below the folder, not a longer name");

        string mvOld = Put(tmp, "mv-old.txt", "x");
        FileOps.Move(mvOld, Path.Combine(tmp, "mv-new.txt"), isDir: false);
        Check(!File.Exists(mvOld) && File.ReadAllText(Path.Combine(tmp, "mv-new.txt")) == "x", "rename: file moved with its contents");
        string caseOld = Put(tmp, "CaseName.txt", "y");
        FileOps.Move(caseOld, Path.Combine(tmp, "casename.txt"), isDir: false);
        Check(Directory.GetFiles(tmp).Select(Path.GetFileName).Contains("casename.txt")
              && !Directory.GetFiles(tmp).Any(f => Path.GetFileName(f).StartsWith(".rename-")),
            "rename: a case-only name change works and leaves nothing behind");

        Check(App.ParseLocation("src/a.cs:12") == ("src/a.cs", 11, -1) && App.ParseLocation("a.cs:12:5") == ("a.cs", 11, 4)
              && App.ParseLocation("plain.txt") == ("plain.txt", 0, -1),
            "go to: file:line and file:line:col are understood");

        var tabs = new TabManager();
        TextEditor Ed(string name) { var e = new TextEditor(); e.Open(Put(tmp, name, name)); return e; }
        var t1 = tabs.Add(Ed("t1.txt"));
        var t2 = tabs.Add(Ed("t2.txt"));
        var t3 = tabs.Add(Ed("t3.txt"));
        tabs.Close(t3);
        Check(tabs.Active == t2 && tabs.IndexOfPath(Path.Combine(tmp, "t1.txt")) == 0, "tabs: closing the active last tab activates its left neighbour");
        tabs.ActivateRelative(1);
        Check(tabs.Active == t1, "tabs: cyclic switching");

        Check(Clipboard.HelperCandidates().Count > 0 && Clipboard.PasteCandidates().Count > 0,
            "clipboard: the current OS has clipboard utilities to try");
        Check(new TextFormat(TextEncoding.Utf8, false, "\n").Describe() == ""
              && new TextFormat(TextEncoding.Latin1, false, "\r\n").Describe().Contains("CRLF"),
            "format: plain UTF-8 + LF needs no note; others are described");
    }
}
#endif
