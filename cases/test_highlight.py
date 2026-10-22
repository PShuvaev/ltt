"""Syntax colours on screen: the language a file name picks, keywords, strings, comments, numbers and
preprocessor lines, block comments over lines, edits and scrolling, and the review's edge cases
(H1, H2, V15). Colours: keyword Cyan, string Yellow, comment DarkGreen, number Magenta,
directive DarkCyan, the rest Gray."""
import os
import signal
import time

import pytest

from editor_helpers import burst, fg, line_colours, open_text
from prompts_helpers import rename, select_in_tree
from startup_helpers import foreign_controls, session_output
from vim_helpers import (GROUP_COLOURS, SYNTAX, colour_of, fake_vim, install_syntax, logging_vim, pids_of, slow_vim,
                         stop, wait_colours, wait_coloured, wait_gone, with_vim)

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


def test_a_number_with_a_signed_exponent_is_one_number(ltt, ws):
    app = open_text(ltt, ws, "x = 1e-3 + 2.5E+4;\n", name="a.c")
    s = app.snap()
    assert fg(s, "1e-3") == "Magenta" and fg(s, "2.5E+4") == "Magenta"


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

def test_rust_lifetimes_are_not_strings(ltt, ws):  # Kakoune's test/highlight/rust
    text = "fn f<'a>(x: &'a str) -> &'a str { x } // note\nlet c = 'x'; let q = '\\''; let d = '\"';\n"
    app = open_text(ltt, ws, text, name="a.rs")
    s = app.snap()
    assert fg(s, "// note") == "DarkGreen" and fg(s, "a>(x") == "Gray"
    assert [fg(s, c, line=2) for c in ("'x'", "'\\''", "'\"'")] == ["Yellow"] * 3


def test_shell_hash_inside_a_word_and_single_quotes(ltt, ws):  # Kakoune's sh.kak
    text = "if [ $# -eq 0 ]; then\n  n=${#a[@]}; b=${p##*/}; echo 'C:\\' # x\nfi\n"
    app = open_text(ltt, ws, text, name="a.sh")
    s = app.snap()
    assert fg(s, "then") == "Cyan" and fg(s, "[@]}; b=", line=2) == "Gray"
    assert fg(s, "'C:\\'", line=2) == "Yellow" and fg(s, "# x", line=2) == "DarkGreen"


def test_yaml_hash_inside_a_value_is_not_a_comment(ltt, ws):  # Kakoune's yaml.kak
    app = open_text(ltt, ws, "url: http://x/#top\nk: v # c\n", name="a.yaml")
    s = app.snap()
    assert fg(s, "http://x/#top") == "Gray" and fg(s, "# c", line=2) == "DarkGreen"



@pytest.mark.parametrize("text, line", [
    ("const re = /\\/*$/;\nlet x = 1;\n", 2),
    ("const help = `\nusage: lint src/*.js\n`;\nlet x = 1;\n", 4),
], ids=["regex", "template"])
def test_slash_star_in_a_js_regex_or_template_opens_no_comment(ltt, ws, text, line):  # Textadept's lexer tests
    app = open_text(ltt, ws, text, name="a.js")
    assert fg(app.snap(), "let", line=line) == "Cyan"


def test_a_division_or_a_closing_tag_is_no_regex(ltt, ws):  # the other side of the regex test
    text = "let a = b / c / d;\nconst el = <a>x</a>, w = <b>y</b>;\nlet z = 1; // c\n"
    app = open_text(ltt, ws, text, name="a.jsx")
    s = app.snap()
    assert fg(s, "/ c /", line=1) == "Gray" and fg(s, "</a>, w = <b>y</b>", line=2) == "Gray"
    assert fg(s, "let", line=3) == "Cyan" and fg(s, "// c", line=3) == "DarkGreen"


def test_a_go_raw_string_runs_over_lines_and_takes_no_escapes(ltt, ws):
    text = 'var p = `C:\\`\nvar q = `one\n/* not a comment\n`\nvar r int\n'
    app = open_text(ltt, ws, text, name="a.go")
    s = app.snap()
    assert fg(s, "`C:\\`", line=1) == "Yellow" and fg(s, "/* not a comment", line=3) == "Yellow"
    assert fg(s, "var", line=5) == "Cyan"


# ================================================================ colours from vim
# vim, when it is on PATH, colours the text: ltt keeps a hidden vim running with a copy of each tab's
# text and draws the highlight groups vim finds in its own colours (GROUP_COLOURS). These tests put
# the real vim on ltt's PATH and a language of their own in ~/.vim (*.ltttest, see vim_helpers).

FROM_VIM = pytest.mark.xfail(strict=True, reason="FEATURE: syntax colours come from vim")


def vim_text(ltt, ws, text, name="a.ltttest", **kw):
    """The real vim on PATH, then `name` with `text` open in ltt."""
    with_vim(ws)
    return open_text(ltt, ws, text, name=name, **kw)


# ---------------------------------------------------------------- groups and colours

@FROM_VIM
def test_each_vim_highlight_group_has_its_colour(ltt, ws):
    words = [f"k{g}" for g in GROUP_COLOURS] + ["kOwn", "kDeep"]
    app = vim_text(ltt, ws, "\n".join(words) + "\n", rows=len(words) + 8)
    expected = {(f"k{g}", i + 1): colour for i, (g, colour) in enumerate(GROUP_COLOURS.items())}
    expected[("kOwn", len(words) - 1)] = "Gray"  # a group with colours of its own and no link: plain
    expected[("kDeep", len(words))] = "Yellow"  # linked to String through a group of the syntax
    wait_colours(app, expected)


@FROM_VIM
def test_keywords_strings_numbers_comments_and_directives(ltt, ws):
    # nothing pressed: vim's answer is drawn as it comes
    text = 'alpha plain "s\\"t\\n" 42 -- a note\n@define gamma {- a block\nstill -} beta\n'
    app = vim_text(ltt, ws, text)
    wait_colours(app, {("alpha", 1): "Cyan", ("plain", 1): "Gray", ('"s\\"t\\n"', 1): "Yellow", ("42", 1): "Magenta",
                       ("-- a note", 1): "DarkGreen", ("@define", 2): "DarkCyan", ("gamma", 2): "Cyan",
                       ("{- a block", 2): "DarkGreen", ("still -}", 3): "DarkGreen", ("beta", 3): "Cyan"})


@FROM_VIM
def test_a_c_file_gets_the_colours_of_vims_c_syntax(ltt, ws):
    text = '#include <stdio.h>\nint main(void) { return 0; } // done\nchar *s = "a\\n"; /* c */\n'
    app = vim_text(ltt, ws, text, name="a.c")
    wait_colours(app, {("#include", 1): "DarkCyan", ("<stdio.h>", 1): "Yellow", ("int", 2): "Cyan",
                       ("main", 2): "Gray", ("return", 2): "Cyan", ("0", 2): "Magenta", ("// done", 2): "DarkGreen",
                       ("char", 3): "Cyan", ('"a\\n"', 3): "Yellow", ("/* c */", 3): "DarkGreen"})


@FROM_VIM
def test_vim_picks_the_language_by_name_and_by_the_first_line(ltt, ws):
    with_vim(ws)
    ws.write("p/Makefile", "all: x\n\t@echo hi # a note\n")
    ws.write("p/a.py", "def f():  # a note\n    return 1\n")
    ws.write("p/run", "#!/bin/sh\nif true; then echo 'q'; fi\n")  # no extension: the #! line tells
    app = ltt(ws.path("p/Makefile"), ws.path("p/a.py"), ws.path("p/run"))
    app.wait_mode("EDITOR")
    wait_colours(app, {("# a note", 2): "DarkGreen"})
    app.press("ctrl+pgdn")
    wait_colours(app, {("def", 1): "Cyan", ("# a note", 1): "DarkGreen", ("return", 2): "Cyan", ("1", 2): "Magenta"})
    app.press("ctrl+pgdn")
    wait_colours(app, {("if", 2): "Cyan", ("then", 2): "Cyan", ("fi", 2): "Cyan"})


@FROM_VIM
def test_selection_colours_win_over_vims_colours(ltt, ws):
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    s = burst(app, "shift+right", "shift+right", "shift+right")
    y = s.editor_row(1).y
    assert [(s.fg(s.text_x + i, y), s.bg(s.text_x + i, y)) for i in range(5)] == \
        [("White", "DarkBlue")] * 3 + [("Cyan", "Black")] * 2


# ---------------------------------------------------------------- editing and moving around

@FROM_VIM
def test_typing_a_comment_start_recolours_the_lines_below_and_undo_brings_them_back(ltt, ws):
    app = vim_text(ltt, ws, "alpha\nbeta\ngamma\n")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan", ("gamma", 3): "Cyan"})
    app.type("{-")
    wait_colours(app, {("{-alpha", 1): "DarkGreen", ("beta", 2): "DarkGreen", ("gamma", 3): "DarkGreen"})
    app.press("down", "end")
    app.type(" -}")
    wait_colours(app, {("beta -}", 2): "DarkGreen", ("gamma", 3): "Cyan"})
    app.press("ctrl+z")
    wait_colours(app, {("beta", 2): "DarkGreen", ("gamma", 3): "DarkGreen"})
    app.press("ctrl+z")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan", ("gamma", 3): "Cyan"})


@FROM_VIM
def test_colours_follow_inserted_and_deleted_lines(ltt, ws):
    app = vim_text(ltt, ws, 'plain\nalpha here\n"str" beta\n')
    wait_colours(app, {("alpha", 2): "Cyan", ('"str"', 3): "Yellow"})
    app.press("enter", "enter", "enter")
    wait_colours(app, {("plain", 4): "Gray", ("alpha", 5): "Cyan", ('"str"', 6): "Yellow", ("beta", 6): "Cyan"})
    s = app.press("ctrl+home", "shift+down", "shift+down", "shift+down", "delete")
    assert s.editor_lines[:3] == ["plain", "alpha here", '"str" beta']
    wait_colours(app, {("plain", 1): "Gray", ("alpha", 2): "Cyan", ('"str"', 3): "Yellow", ("beta", 3): "Cyan"})


@FROM_VIM
def test_a_paste_of_many_lines_is_coloured(ltt, ws):
    app = vim_text(ltt, ws, "plain\n")
    ws.set_clipboard("".join(f"alpha {i} -- a note of some length\n" for i in range(4000)))  # ~150 KB
    app.press("ctrl+v")
    s = app.wait(lambda s: s.pos[0] == 4001, what="the paste")
    n = s.editor_rows[0].number
    wait_colours(app, {("alpha", n): "Cyan", ("-- a note", n): "DarkGreen", ("alpha", n + 5): "Cyan"})
    app.press("ctrl+home")
    wait_colours(app, {("alpha", 1): "Cyan", ("5", 6): "Magenta"})


@FROM_VIM
def test_a_comment_opened_far_above_colours_the_lines_in_view(ltt, ws):
    lines = ["{- start"] + [f"in comment {i}" for i in range(2, 300)] + ["end -} alpha"] + \
        [f"beta {i}" for i in range(302, 400)]
    app = vim_text(ltt, ws, "\n".join(lines) + "\n")
    for line, expected in ((200, {("in comment 200", 200): "DarkGreen"}),
                           (301, {("end -}", 301): "DarkGreen", ("alpha", 301): "Cyan", ("beta", 302): "Cyan"})):
        app.press("ctrl+l")
        app.type(str(line))
        app.press("enter")
        wait_colours(app, expected)


@FROM_VIM
@pytest.mark.parametrize("lines", [500, 5000])
def test_an_edit_far_above_recolours_the_end_of_the_file(ltt, ws, lines):
    # vim keeps what it worked out for every line; an edit above must not leave the lines below stale
    app = vim_text(ltt, ws, "alpha\n" * lines)
    app.press("ctrl+end")
    wait_colours(app, {("alpha", lines): "Cyan"})
    app.press("ctrl+home")
    app.type("{-")
    wait_colours(app, {("{-alpha", 1): "DarkGreen", ("alpha", 2): "DarkGreen"})
    app.press("ctrl+end")
    wait_colours(app, {("alpha", lines): "DarkGreen", ("alpha", lines - 10): "DarkGreen"})
    app.press("ctrl+home", "delete", "delete")
    app.press("ctrl+end")
    wait_colours(app, {("alpha", lines): "Cyan", ("alpha", lines - 10): "Cyan"})


@FROM_VIM
def test_colours_on_a_line_scrolled_sideways(ltt, ws):
    app = vim_text(ltt, ws, 'x "' + "s" * 150 + '" alpha -- end\n')
    s = app.press("end")
    assert s.editor_row(1).text.startswith("‹sss")
    wait_colours(app, {('sss"', 1): "Yellow", ("alpha", 1): "Cyan", ("-- end", 1): "DarkGreen"})
    app.press("home")
    wait_colours(app, {('x "sss', 1): "mixed", ('"sss', 1): "Yellow"})


@FROM_VIM
def test_characters_of_every_width_keep_their_colours_in_place(ltt, ws):
    # vim counts bytes, ltt characters and screen cells: wide characters, marks, tabs and controls between
    text = '"é中👀\x01e\u0301\\"" alpha\tbeta 中 gamma -- ок 👍🏽 note\n'
    app = vim_text(ltt, ws, text)
    wait_colours(app, {('"é中👀', 1): "Yellow", ('\\""', 1): "Yellow", ("alpha", 1): "Cyan", ("beta", 1): "Cyan",
                       ("gamma", 1): "Cyan", ("-- ок 👍🏽 note", 1): "DarkGreen"})
    app.press("end")
    app.type(' "中"')
    wait_colours(app, {('"中"', 1): "DarkGreen", ("gamma", 1): "Cyan"})
    app.press("home")
    app.type('中 "x"')
    wait_colours(app, {("中 ", 1): "Gray", ('"x"', 1): "Yellow", ("alpha", 1): "Cyan", ("gamma", 1): "Cyan"})


@FROM_VIM
def test_quotes_backslashes_and_bars_typed_reach_vim_as_they_are(ltt, ws):
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    app.press("end")
    app.type(' "a|b\\\\" beta "c\\"d" | gamma')  # \\ is one backslash, \" a quote in the string
    wait_colours(app, {('"a|b\\\\"', 1): "Yellow", ("beta", 1): "Cyan", ('"c\\"d"', 1): "Yellow", ("gamma", 1): "Cyan"})


@FROM_VIM
@pytest.mark.parametrize("length, coloured", [(20_000, True), (20_001, False)], ids=["20000", "20001"])
def test_lines_over_20000_characters_are_drawn_without_colours(ltt, ws, length, coloured):
    head = 'alpha "str" '
    app = vim_text(ltt, ws, head + "b" * (length - len(head)) + "\nbeta\n")
    wait_coloured(app, "beta", "Cyan", line=2)
    s = app.snap()
    assert (colour_of(s, "alpha"), colour_of(s, '"str"')) == (("Cyan", "Yellow") if coloured else ("Gray", "Gray"))


@FROM_VIM
def test_a_comment_opened_in_a_very_long_line_still_covers_the_next_lines(ltt, ws):
    app = vim_text(ltt, ws, "{- " + "a" * 20_001 + "\nstill -} beta\n")
    wait_colours(app, {("still -}", 2): "DarkGreen", ("beta", 2): "Cyan"})
    assert colour_of(app.snap(), "{- aaa") == "Gray"  # the long line itself has no colours


# ---------------------------------------------------------------- before vim answers (it is slow here)

@FROM_VIM
def test_colours_move_with_lines_at_once(ltt, ws):
    slow_vim(ws)
    app = open_text(ltt, ws, 'plain\nalpha here\n"str" beta\n', name="a.ltttest")
    wait_colours(app, {("alpha", 2): "Cyan", ('"str"', 3): "Yellow"})
    s = burst(app, "enter", "enter")  # the frame right after the keys, long before vim's answer
    assert [colour_of(s, w, n) for w, n in (("plain", 3), ("alpha", 4), ('"str"', 5), ("beta", 5))] == \
        ["Gray", "Cyan", "Yellow", "Cyan"]
    s = burst(app, "backspace", "backspace")
    assert [colour_of(s, w, n) for w, n in (("plain", 1), ("alpha", 2), ('"str"', 3))] == ["Gray", "Cyan", "Yellow"]


@FROM_VIM
def test_typed_text_takes_the_colour_around_it_at_once(ltt, ws):
    slow_vim(ws)
    app = open_text(ltt, ws, '"abc" alpha -- note\n', name="a.ltttest")
    wait_colours(app, {('"abc"', 1): "Yellow", ("alpha", 1): "Cyan", ("-- note", 1): "DarkGreen"})
    app.press("right", "right")
    s = app.type("XY")
    assert [colour_of(s, w) for w in ('"aXYbc"', "alpha", "-- note")] == ["Yellow", "Cyan", "DarkGreen"]
    app.press("end")
    s = app.type(" more")
    assert colour_of(s, "-- note more") == "DarkGreen"


@FROM_VIM
def test_a_line_split_in_two_keeps_both_halves_coloured_at_once(ltt, ws):
    slow_vim(ws)
    app = open_text(ltt, ws, 'alpha "str" beta\n', name="a.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ('"str"', 1): "Yellow", ("beta", 1): "Cyan"})
    app.press(*["right"] * 11)
    s = app.press("enter")
    assert s.editor_lines[:2] == ['alpha "str"', " beta"]
    assert [colour_of(s, w, n) for w, n in (("alpha", 1), ('"str"', 1), ("beta", 2))] == ["Cyan", "Yellow", "Cyan"]


# ---------------------------------------------------------------- files, names and tabs

@FROM_VIM
def test_renaming_a_file_changes_its_language(ltt, ws):
    with_vim(ws)
    install_syntax(ws, "lttother", "syntax keyword lttOther beta\nhighlight default link lttOther Type\n"
                                   "let b:current_syntax = 'lttother'\n", "*.lttother")
    app = open_text(ltt, ws, "alpha beta\n", name="a.txt")
    rename(app, "a.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 1): "Cyan"})
    rename(app, "a.lttother")
    wait_colours(app, {("alpha", 1): "Gray", ("beta", 1): "Cyan"})
    rename(app, "NOTES")  # a name vim knows no language for: no colours, not the last language's
    wait_colours(app, {("alpha", 1): "Gray", ("beta", 1): "Gray"})
    time.sleep(1)
    assert [colour_of(app.snap(), w) for w in ("alpha", "beta")] == ["Gray", "Gray"]


@FROM_VIM
def test_two_tabs_keep_their_own_languages(ltt, ws):
    with_vim(ws)
    ws.write("a.ltttest", "alpha int\n")
    ws.write("b.c", "alpha int\n")
    app = ltt(ws.path("a.ltttest"), ws.path("b.c"))
    app.wait_mode("EDITOR")
    for _ in range(2):
        wait_colours(app, {("alpha", 1): "Cyan", ("int", 1): "Gray"})
        app.press("ctrl+pgdn")
        wait_colours(app, {("alpha", 1): "Gray", ("int", 1): "Cyan"})
        app.press("ctrl+pgdn")


@FROM_VIM
def test_two_names_of_one_file_are_two_tabs_with_their_own_text(ltt, ws):
    # a hard link: vim would take the second name for the first one's buffer
    with_vim(ws)
    ws.write("a.ltttest", "alpha\nbeta\n")
    os.link(ws.path("a.ltttest"), ws.path("b.ltttest"))
    app = ltt(ws.path("a.ltttest"), ws.path("b.ltttest"))
    app.wait_mode("EDITOR")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan"})
    app.press("ctrl+pgdn")
    app.wait(lambda s: s.title_path.endswith("/b.ltttest"), what="the second tab")
    app.type("{-")
    wait_colours(app, {("{-alpha", 1): "DarkGreen", ("beta", 2): "DarkGreen"})
    app.press("ctrl+pgdn")
    app.wait(lambda s: s.title_path.endswith("/a.ltttest"), what="the first tab")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan"})


@FROM_VIM
def test_a_file_name_with_quotes_spaces_and_symbols(ltt, ws):
    app = vim_text(ltt, ws, "alpha -- note\n", name="a b%#|'\"$x\\y!.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ("-- note", 1): "DarkGreen"})


@FROM_VIM
def test_a_tab_closed_and_opened_again_is_coloured(ltt, ws):
    with_vim(ws)
    ws.write("p/a.ltttest", "alpha\n")
    app = ltt(ws.path("p"), ws.path("p/a.ltttest"))
    app.wait_mode("EDITOR")
    wait_coloured(app, "alpha", "Cyan")
    app.press("ctrl+w")
    app.wait(lambda s: not s.tabs, what="the tab closed")
    select_in_tree(app, "a.ltttest")
    app.press("enter")
    app.wait_mode("EDITOR")
    wait_coloured(app, "alpha", "Cyan")


@FROM_VIM
def test_a_file_changed_on_disk_is_coloured_anew(ltt, ws):
    app = vim_text(ltt, ws, "alpha\nbeta\n")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan"})
    ws.write("a.ltttest", "{- alpha\nbeta -} gamma\n")
    wait_colours(app, {("{- alpha", 1): "DarkGreen", ("beta -}", 2): "DarkGreen", ("gamma", 2): "Cyan"})


# ---------------------------------------------------------------- what vim may not do

@FROM_VIM
def test_a_modeline_in_the_file_is_not_obeyed(ltt, ws):
    # a modeline could set any option (CVE-2019-12735): ltt's vim reads none
    app = vim_text(ltt, ws, "alpha int\n-- vim: set filetype=c syntax=c :\n")
    wait_colours(app, {("alpha", 1): "Cyan", ("int", 1): "Gray"})


@FROM_VIM
def test_the_vimrc_and_plugins_are_not_read(ltt, ws):
    marker = os.path.join(ws.sys, "vimrc-ran")
    for rel in (".vimrc", ".vim/vimrc", ".vim/plugin/p.vim"):
        ws.write(os.path.join(ws.home, rel), f"call writefile(['ran'], '{marker}')\nsyntax off\n")
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    assert not os.path.exists(marker)


@FROM_VIM
def test_without_vim_the_text_is_plain(ltt, ws):
    # vim is not on PATH: plain text, as the other files of the tests show it
    ws.write("a.c", "int main(void) { return 0; } // done\n")
    install_syntax(ws, "ltttest", SYNTAX, "*.ltttest")
    ws.write("b.ltttest", "alpha\n")
    app = ltt(ws.path("a.c"), ws.path("b.ltttest"))
    app.wait_mode("EDITOR")
    time.sleep(0.5)
    assert line_colours(app.snap(), 1) == {"Gray"}
    s = app.press("ctrl+pgdn")
    assert line_colours(s, 1) == {"Gray"}


# ---------------------------------------------------------------- a vim that misbehaves

@FROM_VIM
def test_a_vim_that_starts_late_colours_the_text_when_it_is_ready(ltt, ws):
    go = os.path.join(ws.sys, "go")
    with_vim(ws)
    fake_vim(ws, f'while [ ! -e "{go}" ]; do sleep 0.05; done\nexec "$REAL_VIM" "$@"')
    app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
    s = app.type("beta ")  # ltt answers keys while its vim is not there yet
    assert s.editor_lines[0] == "beta alpha" and line_colours(s, 1) == {"Gray"}
    open(go, "w").close()
    wait_colours(app, {("beta", 1): "Cyan", ("alpha", 1): "Cyan"})


@FROM_VIM
@pytest.mark.parametrize("body", ["exit 1", "echo junk; echo junk >&2; exit 0", "exec cat > /dev/null"],
                         ids=["dies", "prints-junk-and-ends", "never-answers"])
def test_a_vim_that_fails_leaves_the_text_plain_and_ltt_working(ltt, ws, body):
    with_vim(ws)
    pids = fake_vim(ws, body)
    try:
        app = open_text(ltt, ws, "int x;\n", name="a.c")
        s = app.type("int y; ")
        assert s.editor_lines[0] == "int y; int x;"
        assert app.press("ctrl+s").status == "Saved: a.c"
        time.sleep(0.5)
        s = app.snap()
        assert line_colours(s, 1) == {"Gray"} and "junk" not in "".join(s.lines)
        t = time.monotonic()
        assert app.quit() == 0 and time.monotonic() - t < 3
    finally:
        stop(pids_of(pids))


@FROM_VIM
def test_what_vim_prints_never_reaches_the_terminal(ltt, ws):
    with_vim(ws)
    fake_vim(ws, "echo junk; echo junk >&2; printf '\\033]0;PWNED\\007\\033[2J'; printf '\\033]0;PWNED2\\007' >&2\n"
                 'exec "$REAL_VIM" "$@"')
    app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
    wait_coloured(app, "alpha", "Cyan")
    s = app.snap()
    assert "junk" not in "".join(s.lines) and s.window_title == ""
    assert foreign_controls(session_output(app)) == []


@FROM_VIM
def test_a_syntax_with_errors_leaves_its_file_plain_and_vim_working(ltt, ws):
    with_vim(ws)
    install_syntax(ws, "lttbroken", 'syntax match lttBad "\\(unclosed"\ncall NoSuchFunction()\nechoerr "boom"\n'
                                    "syntax keyword lttBroken beta\nhighlight default link lttBroken Type\n",
                   "*.lttbroken")
    ws.write("a.lttbroken", "alpha beta\n")
    ws.write("b.ltttest", "alpha beta\n")
    app = ltt(ws.path("a.lttbroken"), ws.path("b.ltttest"))
    app.wait_mode("EDITOR")
    app.press("ctrl+pgdn")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 1): "Cyan"})
    app.press("ctrl+pgup")
    s = app.wait(lambda s: s.title_path.endswith("/a.lttbroken"), what="the first tab")
    assert colour_of(s, "alpha") == "Gray"


@FROM_VIM
def test_a_syntax_that_hangs_vim_leaves_ltt_working_and_quitting_stops_vim(ltt, ws):
    pids = logging_vim(ws)
    install_syntax(ws, "ltthang", "while 1\nendwhile\n", "*.ltthang")
    try:
        app = open_text(ltt, ws, "alpha\n", name="a.ltthang")
        app.wait(lambda s: pids_of(pids), what="vim started")
        time.sleep(0.5)
        s = app.type("beta ")
        assert s.editor_lines[0] == "beta alpha"
        assert app.press("ctrl+s").status == "Saved: a.ltthang"
        t = time.monotonic()
        assert app.quit() == 0 and time.monotonic() - t < 3
        assert wait_gone(pids_of(pids)) == []
    finally:
        stop(pids_of(pids))


@FROM_VIM
def test_quitting_stops_vim(ltt, ws):
    pids = logging_vim(ws)
    try:
        app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
        wait_coloured(app, "alpha", "Cyan")
        assert len(pids_of(pids)) == 1  # one vim for the session
        assert app.quit() == 0
        assert wait_gone(pids_of(pids)) == []
    finally:
        stop(pids_of(pids))


@FROM_VIM
def test_vim_goes_when_ltt_is_killed(ltt, ws):
    pids = logging_vim(ws)
    try:
        app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
        wait_coloured(app, "alpha", "Cyan")
        app.signal(signal.SIGKILL)
        app.wait_exit(timeout=5)
        assert wait_gone(pids_of(pids)) == []  # its input closed: vim ends by itself
    finally:
        stop(pids_of(pids))
