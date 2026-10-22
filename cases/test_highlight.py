"""Syntax colours on screen: the language a file name picks, keywords, strings, comments, numbers and
preprocessor lines, block comments over lines, edits and scrolling, and the review's edge cases
(H1, H2, V15). Colours: keyword Cyan, string Yellow, comment DarkGreen, number Magenta,
directive DarkCyan, the rest Gray."""
import pytest

from editor_helpers import burst, fg, line_colours, open_text

# ---------------------------------------------------------------- the language of a file

PROBE = 'if x == "s" then 1 // c # d'
PROBE_PARTS = ("if", "then", '"s"', "1", "//", "# d")
# What each kind of file makes of PROBE's parts: keywords, strings, numbers, // and # comments.
LOOKS = {
    "c-like": ("Cyan", "Gray", "Yellow", "Magenta", "DarkGreen", "DarkGreen"),
    "css": ("Gray", "Gray", "Yellow", "Magenta", "DarkGreen", "DarkGreen"),
    "python": ("Cyan", "Gray", "Yellow", "Magenta", "Gray", "DarkGreen"),
    "shell-and-ruby": ("Cyan", "Cyan", "Yellow", "Magenta", "Gray", "DarkGreen"),
    "config": ("Gray", "Gray", "Yellow", "Magenta", "Gray", "DarkGreen"),
    "hash-comments-only": ("Gray", "Gray", "Gray", "Gray", "Gray", "DarkGreen"),
    "plain": ("Gray",) * 6,
}
NAMES = {
    "c-like": ["a.c", "a.h", "a.cpp", "a.hpp", "a.java", "a.js", "a.mjs", "a.ts", "a.tsx", "a.jsx", "a.go",
               "a.rs", "a.swift", "a.kt", "a.php", "a.dart", "a.scala", "a.json", "a.cs", "UPPER.CS"],
    "css": ["a.css", "a.scss", "a.less"],
    "python": ["a.py", "a.pyi"],
    "shell-and-ruby": ["a.sh", "a.bash", "a.zsh", ".bashrc", "a.rb", "Gemfile", "Rakefile"],
    "config": ["a.yml", "a.yaml", "a.toml", "a.conf", "a.tf", ".env", ".env.local", "CMakeLists.txt"],
    "hash-comments-only": ["Makefile", "a.mk", "Dockerfile", "Dockerfile.dev", "a.dockerfile", ".gitignore",
                           ".dockerignore"],
    "plain": ["a.md", "a.txt", "NOTES", "a.log", "a.csv", "a.html", "LICENSE"],
}


@pytest.mark.parametrize("kind", list(LOOKS))
def test_a_file_name_picks_the_rules_of_its_language(ltt, ws, kind):
    names = NAMES[kind]
    for name in names:
        ws.write(f"p/{name}", PROBE + "\n")
    app = ltt(*[ws.path(f"p/{name}") for name in names])
    app.wait_mode("EDITOR")
    for i, name in enumerate(names):
        if i:
            app.press("ctrl+pgdn")
        s = app.wait(lambda s: s.title_path.endswith("/" + name), what=f"the tab of {name}")
        assert (name, tuple(fg(s, part) for part in PROBE_PARTS)) == (name, LOOKS[kind])


def test_keywords_depend_on_the_language(ltt, ws):
    for name in ("a.py", "a.go", "a.rb"):
        ws.write(f"k/{name}", "def func end\n")
    app = ltt(ws.path("k/a.py"), ws.path("k/a.go"), ws.path("k/a.rb"))
    app.wait_mode("EDITOR")
    looks = []
    for i in range(3):
        s = app.press("ctrl+pgdn") if i else app.snap()
        looks.append(tuple(fg(s, w) for w in ("def", "func", "end")))
    assert looks == [("Cyan", "Gray", "Gray"), ("Gray", "Cyan", "Gray"), ("Cyan", "Gray", "Cyan")]


def test_renaming_a_file_changes_its_rules(ltt, ws):
    app = open_text(ltt, ws, "def f(): return 1  # c\n", name="a.txt")
    assert fg(app.snap(), "def") == "Gray"
    app.press("ctrl+r", "ctrl+u")
    app.type("a.py")
    app.press("enter")
    s = app.wait(lambda s: s.mode == "EDITOR" and s.title_path.endswith("/a.py"), what="the tab renamed")
    assert fg(s, "def") == "Cyan" and fg(s, "1") == "Magenta" and fg(s, "# c") == "DarkGreen"


# ---------------------------------------------------------------- tokens


def test_a_csharp_line(ltt, ws):
    app = open_text(ltt, ws, 'public class A { int x = 42; string s = "hi"; } // done\n', name="a.cs")
    s = app.snap()
    assert [fg(s, w) for w in ("public", "class", "int", "string")] == ["Cyan"] * 4
    assert fg(s, " A { ") == "Gray" and fg(s, " x = ") == "Gray"
    assert fg(s, "42") == "Magenta"
    assert fg(s, '"hi"') == "Yellow"
    assert fg(s, "// done") == "DarkGreen"


def test_keywords_are_whole_words_only(ltt, ws):
    words = ["classic", "_class", "class_", "class2", "@class", "class"]
    app = open_text(ltt, ws, "\n".join(words) + "\n", name="a.cs")
    s = app.snap()
    assert [fg(s, w, line=i + 1) for i, w in enumerate(words)] == ["Gray"] * 5 + ["Cyan"]


def test_numbers_but_not_digits_inside_names(ltt, ws):
    app = open_text(ltt, ws, "x1 = 0xFF + 1.5 + 10_000 + a2b + 7;\n", name="a.c")
    s = app.snap()
    assert fg(s, "x1") == "Gray" and fg(s, "a2b") == "Gray"
    assert [fg(s, n) for n in ("0xFF", "1.5", "10_000", "7")] == ["Magenta"] * 4


def test_strings_in_all_three_quotes_with_escapes(ltt, ws):
    app = open_text(ltt, ws, 'a = "x\\"y" + b + \'c\' + `d\\`e` + f\n', name="a.js")
    s = app.snap()
    assert fg(s, '"x\\"y"') == "Yellow"
    assert fg(s, "'c'") == "Yellow" and fg(s, "`d\\`e`") == "Yellow"
    assert fg(s, " + b + ") == "Gray" and fg(s, " f") == "Gray"


def test_comment_marks_in_strings_and_quotes_in_comments_are_just_text(ltt, ws):
    app = open_text(ltt, ws, 'x = "http://a /* b"; int y; /* it\'s */ long z;\n', name="a.c")
    s = app.snap()
    assert fg(s, '"http://a /* b"') == "Yellow"
    assert fg(s, "int") == "Cyan"
    assert fg(s, "/* it's */") == "DarkGreen" and fg(s, "long") == "Cyan"


@pytest.mark.xfail(strict=True, reason="BUG: a signed exponent breaks a number: in 1e-3 the - is grey "
                                       "(the tokenizer's own comment lists 1e-3 as a number)")
def test_a_number_with_a_signed_exponent_is_one_number(ltt, ws):
    app = open_text(ltt, ws, "x = 1e-3 + 2.5E+4;\n", name="a.c")
    s = app.snap()
    assert fg(s, "1e-3") == "Magenta" and fg(s, "2.5E+4") == "Magenta"


@pytest.mark.xfail(strict=True, reason="BUG: Ruby's defined? is in the keyword list but never coloured: "
                                       "a word ends before the ?")
def test_ruby_defined_is_a_keyword(ltt, ws):
    app = open_text(ltt, ws, "x = defined?(y)\n", name="a.rb")
    assert fg(app.snap(), "defined?") == "Cyan"


def test_an_unclosed_string_ends_with_its_line(ltt, ws):
    app = open_text(ltt, ws, 'x = "abc def\nint y = 1;\n', name="a.c")
    s = app.snap()
    assert fg(s, '"abc def') == "Yellow"
    assert fg(s, "int", line=2) == "Cyan" and fg(s, "1", line=2) == "Magenta"


def test_preprocessor_lines(ltt, ws):
    app = open_text(ltt, ws, '#include <stdio.h>\n  #define T int // the type\n#define N 10\n'
                             '#include "my.h"\nint n;\n', name="a.c")
    s = app.snap()
    assert fg(s, "#include <stdio.h>", 1) == "DarkCyan"
    assert fg(s, "#define T int ", 2) == "DarkCyan"  # no keyword colour inside a directive
    assert fg(s, "// the type", 2) == "DarkGreen"
    assert fg(s, "#define N ", 3) == "DarkCyan" and fg(s, "10", 3) == "Magenta"
    assert fg(s, "#include ", 4) == "DarkCyan" and fg(s, '"my.h"', 4) == "Yellow"
    assert fg(s, "int", 5) == "Cyan"


def test_python_strings_comments_and_keywords(ltt, ws):
    app = open_text(ltt, ws, "def f(x):  # note\n    return 'a' + \"b\" + None\nx = 1 /* no */\n", name="a.py")
    s = app.snap()
    assert fg(s, "def", 1) == "Cyan" and fg(s, "# note", 1) == "DarkGreen"
    assert fg(s, "return", 2) == "Cyan" and fg(s, "None", 2) == "Cyan"
    assert fg(s, "'a'", 2) == "Yellow" and fg(s, '"b"', 2) == "Yellow"
    assert fg(s, "/* no */", 3) == "Gray"  # no block comments in Python


def test_selection_colours_win_over_syntax_colours(ltt, ws):
    app = open_text(ltt, ws, "def f():\n", name="a.py")
    s = burst(app, "shift+right", "shift+right", "shift+right")
    y = s.editor_row(1).y
    assert [(s.fg(s.text_x + i, y), s.bg(s.text_x + i, y)) for i in range(4)] == \
        [("White", "DarkBlue")] * 3 + [("Gray", "Black")]


# ---------------------------------------------------------------- block comments


def test_a_block_comment_spans_lines(ltt, ws):
    app = open_text(ltt, ws, "int a; /* one\ntwo\nthree */ long b;\nchar c;\n", name="a.c")
    s = app.snap()
    assert fg(s, "int", 1) == "Cyan" and fg(s, "/* one", 1) == "DarkGreen"
    assert fg(s, "two", 2) == "DarkGreen"
    assert fg(s, "three */", 3) == "DarkGreen" and fg(s, "long", 3) == "Cyan"
    assert fg(s, "char", 4) == "Cyan"


def test_slash_star_slash_opens_a_comment_without_closing_it(ltt, ws):
    # H2: "/*/" counted as a comment opened and closed at once
    app = open_text(ltt, ws, "/*/ int a;\nchar b; */ long c;\n", name="a.c")
    s = app.snap()
    assert fg(s, "/*/ int a;", 1) == "DarkGreen"
    assert fg(s, "char b; */", 2) == "DarkGreen" and fg(s, "long", 2) == "Cyan"


def test_a_block_comment_opened_in_a_define_comments_the_next_lines(ltt, ws):
    # H2: "#define X /* …" did not open a comment
    app = open_text(ltt, ws, "#define X /* start\nchar b; */ long c;\n", name="a.c")
    s = app.snap()
    assert fg(s, "#define X ", 1) == "DarkCyan" and fg(s, "/* start", 1) == "DarkGreen"
    assert fg(s, "char b; */", 2) == "DarkGreen" and fg(s, "long", 2) == "Cyan"


def test_a_verbatim_string_ending_in_a_backslash_does_not_swallow_a_comment(ltt, ws):
    # H2: \" in a C# verbatim string was taken for an escape, so the string ate the /* after it
    app = open_text(ltt, ws, 'var p = @"C:\\dir\\"; /* c */ int z;\nvar q = $@"D:\\x\\"; long w;\n', name="a.cs")
    s = app.snap()
    assert fg(s, '@"C:\\dir\\"', 1) == "Yellow"
    assert fg(s, "/* c */", 1) == "DarkGreen" and fg(s, "int", 1) == "Cyan"
    assert fg(s, '$@"D:\\x\\"', 2) == "Yellow" and fg(s, "long", 2) == "Cyan"


def test_doubled_quotes_stay_inside_a_verbatim_string(ltt, ws):
    app = open_text(ltt, ws, 'var q = @"say ""hi"" now"; int z;\n', name="a.cs")
    s = app.snap()
    assert fg(s, '@"say ""hi"" now"') == "Yellow" and fg(s, "int") == "Cyan"


def test_a_block_comment_keeps_its_colour_after_its_start_scrolls_away(ltt, ws):
    lines = ["/* start"] + [f"in comment {i}" for i in range(2, 80)] + ["end */ int x;"] + \
        [f"long y{i};" for i in range(81, 121)]
    app = open_text(ltt, ws, "\n".join(lines) + "\n", name="a.c")
    s = burst(app, "pgdn", "pgdn")
    assert s.editor_rows[0].number == 28
    assert {fg(s, f"in comment {n}", n) for n in (28, 40, 53)} == {"DarkGreen"}
    s = burst(app, "pgdn", "pgdn")
    assert s.editor_rows[0].number == 80
    assert fg(s, "end */", 80) == "DarkGreen" and fg(s, "int", 80) == "Cyan"
    assert fg(s, "long", 81) == "Cyan" and fg(s, "long", 105) == "Cyan"
    s = app.press("ctrl+home")
    assert fg(s, "/* start", 1) == "DarkGreen" and fg(s, "in comment 2", 2) == "DarkGreen"


def test_a_block_comment_left_open_runs_to_the_end_of_the_file(ltt, ws):
    app = open_text(ltt, ws, "/* open\n" + "int x;\n" * 200, name="a.c")
    s = app.press("ctrl+end")
    assert s.editor_rows[-1].number == 202
    assert fg(s, "int x;", 200) == "DarkGreen"


def test_typing_a_comment_start_recolours_the_lines_below(ltt, ws):
    app = open_text(ltt, ws, "int a;\nint b;\nint c;\n", name="a.c")
    s = app.type("/*")
    assert fg(s, "int b;", 2) == "DarkGreen" and fg(s, "int c;", 3) == "DarkGreen"
    s = burst(app, "backspace", "backspace")
    assert fg(s, "int", 2) == "Cyan" and fg(s, "int", 3) == "Cyan"


def test_typing_a_comment_end_recolours_the_rest(ltt, ws):
    app = open_text(ltt, ws, "/* a\nint b;\nint c;\n", name="a.c")
    assert fg(app.snap(), "int b;", 2) == "DarkGreen"
    app.press("down")
    s = app.type("*/")
    assert fg(s, "*/", 2) == "DarkGreen" and fg(s, "int", 2) == "Cyan" and fg(s, "int", 3) == "Cyan"


def test_colours_hold_on_a_line_scrolled_sideways(ltt, ws):
    # only the visible part is tokenized; a string that starts before the view keeps its colour
    app = open_text(ltt, ws, 'x = "' + "s" * 100 + '"; int y; // end\n', name="a.c")
    s = app.press("end")
    assert s.editor_row(1).text.startswith("‹sss")
    assert fg(s, 'sss"') == "Yellow"
    assert fg(s, "int") == "Cyan" and fg(s, "// end") == "DarkGreen"


# ---------------------------------------------------------------- no code rules for prose and globs (H1)


@pytest.mark.parametrize("name", ["notes.md", "notes.txt", "NOTES"])
def test_prose_gets_no_code_colours(ltt, ws, name):
    # H1: unknown kinds of files got C rules: an apostrophe opened a string, // in a URL a comment
    text = "Don't stop: see http://example.com/a /* not a comment\nIt's 42 if true, isn't it\n"
    app = open_text(ltt, ws, text, name=name)
    s = app.snap()
    assert line_colours(s, 1) == {"Gray"} and line_colours(s, 2) == {"Gray"}


@pytest.mark.parametrize("name", ["Dockerfile", ".gitignore"])
def test_a_glob_star_does_not_comment_out_the_rest_of_the_file(ltt, ws, name):
    # H1: /* in a Dockerfile or a .gitignore commented out everything after it
    app = open_text(ltt, ws, "COPY src/* /app/\nbuild/*\n# a comment\nRUN echo 'hi' 42\n", name=name)
    s = app.snap()
    assert [line_colours(s, n) for n in (1, 2, 4)] == [{"Gray"}] * 3
    assert line_colours(s, 3) == {"DarkGreen"}


# ---------------------------------------------------------------- very long lines (V15)


@pytest.mark.parametrize("length, coloured", [(20_000, True), (20_001, False)], ids=["20000", "20001"])
def test_lines_over_20000_characters_are_drawn_without_colours(ltt, ws, length, coloured):
    # V15: long lines were tokenized from their start on every frame (minified code, logs)
    head = 'var s = "str"; '
    app = open_text(ltt, ws, head + "a" * (length - len(head)) + "\nvar t;\n", name="a.js")
    s = app.snap()
    assert fg(s, "var", 1) == ("Cyan" if coloured else "Gray")
    assert fg(s, '"str"', 1) == ("Yellow" if coloured else "Gray")
    assert fg(s, "var", 2) == "Cyan"


def test_a_comment_opened_in_a_very_long_line_still_covers_the_next_lines(ltt, ws):
    app = open_text(ltt, ws, "/* " + "a" * 20_001 + "\nchar b; */ long c;\n", name="a.c")
    s = app.snap()
    assert fg(s, "/* aaa", 1) == "Gray"  # the long line itself has no colours
    assert fg(s, "char b; */", 2) == "DarkGreen" and fg(s, "long", 2) == "Cyan"


# ---------------------------------------------------------------- ideas from Kakoune's highlighters

@pytest.mark.xfail(strict=True, reason="BUG: a Rust lifetime ('a) opens a character string that runs to the end "
                                       "of the line and swallows the comment after it")
def test_rust_lifetimes_are_not_strings(ltt, ws):  # Kakoune's test/highlight/rust
    text = "fn f<'a>(x: &'a str) -> &'a str { x } // note\nlet c = 'x'; let q = '\\''; let d = '\"';\n"
    app = open_text(ltt, ws, text, name="a.rs")
    s = app.snap()
    assert fg(s, "// note") == "DarkGreen" and fg(s, "a>(x") == "Gray"
    assert [fg(s, c, line=2) for c in ("'x'", "'\\''", "'\"'")] == ["Yellow"] * 3


@pytest.mark.xfail(strict=True, reason="BUG: in shell a # inside a word ($#, ${#a[@]}, ${p##*/}) starts a comment, "
                                       "and a backslash escapes the closing quote of a single-quoted string")
def test_shell_hash_inside_a_word_and_single_quotes(ltt, ws):  # Kakoune's sh.kak
    text = "if [ $# -eq 0 ]; then\n  n=${#a[@]}; b=${p##*/}; echo 'C:\\' # x\nfi\n"
    app = open_text(ltt, ws, text, name="a.sh")
    s = app.snap()
    assert fg(s, "then") == "Cyan" and fg(s, "[@]}; b=", line=2) == "Gray"
    assert fg(s, "'C:\\'", line=2) == "Yellow" and fg(s, "# x", line=2) == "DarkGreen"


@pytest.mark.xfail(strict=True, reason="BUG: in YAML a # inside a value (the #anchor of a URL) starts a comment")
def test_yaml_hash_inside_a_value_is_not_a_comment(ltt, ws):  # Kakoune's yaml.kak
    app = open_text(ltt, ws, "url: http://x/#top\nk: v # c\n", name="a.yaml")
    s = app.snap()
    assert fg(s, "http://x/#top") == "Gray" and fg(s, "# c", line=2) == "DarkGreen"
