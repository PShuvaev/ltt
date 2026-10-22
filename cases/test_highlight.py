"""Syntax colours come from vim when it is on PATH: ltt keeps a hidden vim with a copy of each tab's
text and draws the highlight groups vim finds in its own colours (GROUP_COLOURS in vim_helpers).
These tests put the real vim on ltt's PATH and a language of their own in ~/.vim (*.ltttest);
elsewhere the tests run without vim, on plain text."""
import os
import signal
import time

import pytest

from editor_helpers import burst, line_colours, open_text
from prompts_helpers import rename, select_in_tree
from startup_helpers import foreign_controls, session_output
from vim_helpers import (GROUP_COLOURS, SYNTAX, colour_of, fake_vim, install_syntax, logging_vim, pids_of, slow_vim,
                         stop, wait_colours, wait_coloured, wait_gone, with_vim)


def vim_text(ltt, ws, text, name="a.ltttest", **kw):
    """The real vim on PATH, then `name` with `text` open in ltt."""
    with_vim(ws)
    return open_text(ltt, ws, text, name=name, **kw)


# ---------------------------------------------------------------- groups and colours

def test_each_vim_highlight_group_has_its_colour(ltt, ws):
    words = [f"k{g}" for g in GROUP_COLOURS] + ["kOwn", "kDeep"]
    app = vim_text(ltt, ws, "\n".join(words) + "\n", rows=len(words) + 8)
    expected = {(f"k{g}", i + 1): colour for i, (g, colour) in enumerate(GROUP_COLOURS.items())}
    expected[("kOwn", len(words) - 1)] = "Gray"  # a group with colours of its own and no link: plain
    expected[("kDeep", len(words))] = "Yellow"  # linked to String through a group of the syntax
    wait_colours(app, expected)


def test_keywords_strings_numbers_comments_and_directives(ltt, ws):
    # nothing pressed: vim's answer is drawn as it comes
    text = 'alpha plain "s\\"t\\n" 42 -- a note\n@define gamma {- a block\nstill -} beta\n'
    app = vim_text(ltt, ws, text)
    wait_colours(app, {("alpha", 1): "Cyan", ("plain", 1): "Gray", ('"s\\"t\\n"', 1): "Yellow", ("42", 1): "Magenta",
                       ("-- a note", 1): "DarkGreen", ("@define", 2): "DarkCyan", ("gamma", 2): "Cyan",
                       ("{- a block", 2): "DarkGreen", ("still -}", 3): "DarkGreen", ("beta", 3): "Cyan"})


def test_a_c_file_gets_the_colours_of_vims_c_syntax(ltt, ws):
    text = '#include <stdio.h>\nint main(void) { return 0; } // done\nchar *s = "a\\n"; /* c */\n'
    app = vim_text(ltt, ws, text, name="a.c")
    wait_colours(app, {("#include", 1): "DarkCyan", ("<stdio.h>", 1): "Yellow", ("int", 2): "Cyan",
                       ("main", 2): "Gray", ("return", 2): "Cyan", ("0", 2): "Magenta", ("// done", 2): "DarkGreen",
                       ("char", 3): "Cyan", ('"a\\n"', 3): "Yellow", ("/* c */", 3): "DarkGreen"})


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


def test_selection_colours_win_over_vims_colours(ltt, ws):
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    s = burst(app, "shift+right", "shift+right", "shift+right")
    y = s.editor_row(1).y
    assert [(s.fg(s.text_x + i, y), s.bg(s.text_x + i, y)) for i in range(5)] == \
        [("White", "DarkBlue")] * 3 + [("Cyan", "Black")] * 2


# ---------------------------------------------------------------- editing and moving around

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


def test_colours_follow_inserted_and_deleted_lines(ltt, ws):
    app = vim_text(ltt, ws, 'plain\nalpha here\n"str" beta\n')
    wait_colours(app, {("alpha", 2): "Cyan", ('"str"', 3): "Yellow"})
    app.press("enter", "enter", "enter")
    wait_colours(app, {("plain", 4): "Gray", ("alpha", 5): "Cyan", ('"str"', 6): "Yellow", ("beta", 6): "Cyan"})
    s = app.press("ctrl+home", "shift+down", "shift+down", "shift+down", "delete")
    assert s.editor_lines[:3] == ["plain", "alpha here", '"str" beta']
    wait_colours(app, {("plain", 1): "Gray", ("alpha", 2): "Cyan", ('"str"', 3): "Yellow", ("beta", 3): "Cyan"})


def test_a_paste_of_many_lines_is_coloured(ltt, ws):
    app = vim_text(ltt, ws, "plain\n")
    ws.set_clipboard("".join(f"alpha {i} -- a note of some length\n" for i in range(4000)))  # ~150 KB
    app.press("ctrl+v")
    s = app.wait(lambda s: s.pos[0] == 4001, what="the paste")
    n = s.editor_rows[0].number
    wait_colours(app, {("alpha", n): "Cyan", ("-- a note", n): "DarkGreen", ("alpha", n + 5): "Cyan"})
    app.press("ctrl+home")
    wait_colours(app, {("alpha", 1): "Cyan", ("5", 6): "Magenta"})


def test_a_comment_opened_far_above_colours_the_lines_in_view(ltt, ws):
    lines = ["{- start"] + [f"in comment {i}" for i in range(2, 301)] + ["end -} alpha"] + \
        [f"beta {i}" for i in range(302, 400)]
    app = vim_text(ltt, ws, "\n".join(lines) + "\n")
    for line, expected in ((200, {("in comment 200", 200): "DarkGreen"}),
                           (310, {("end -}", 301): "DarkGreen", ("alpha", 301): "Cyan", ("beta", 302): "Cyan"})):
        app.press("ctrl+l")
        app.type(str(line))
        app.press("enter")
        wait_colours(app, expected)


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


def test_colours_on_a_line_scrolled_sideways(ltt, ws):
    app = vim_text(ltt, ws, 'x "' + "s" * 150 + '" alpha -- end\n')
    s = app.press("end")
    assert s.editor_row(1).text.startswith("‹sss")
    wait_colours(app, {('sss"', 1): "Yellow", ("alpha", 1): "Cyan", ("-- end", 1): "DarkGreen"})
    app.press("home")
    wait_colours(app, {('x "sss', 1): "mixed", ('"sss', 1): "Yellow"})


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


def test_quotes_backslashes_and_bars_typed_reach_vim_as_they_are(ltt, ws):
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    app.press("end")
    app.type(' "a|b\\\\" beta "c\\"d" | gamma')  # \\ is one backslash, \" a quote in the string
    wait_colours(app, {('"a|b\\\\"', 1): "Yellow", ("beta", 1): "Cyan", ('"c\\"d"', 1): "Yellow", ("gamma", 1): "Cyan"})


@pytest.mark.parametrize("length, coloured", [(20_000, True), (20_001, False)], ids=["20000", "20001"])
def test_lines_over_20000_characters_are_drawn_without_colours(ltt, ws, length, coloured):
    head = 'alpha "str" '
    app = vim_text(ltt, ws, head + "b" * (length - len(head)) + "\nbeta\n")
    wait_coloured(app, "beta", "Cyan", line=2)
    s = app.snap()
    assert (colour_of(s, "alpha"), colour_of(s, '"str"')) == (("Cyan", "Yellow") if coloured else ("Gray", "Gray"))


def test_a_comment_opened_in_a_very_long_line_still_covers_the_next_lines(ltt, ws):
    app = vim_text(ltt, ws, "{- " + "a" * 20_001 + "\nstill -} beta\n")
    wait_colours(app, {("still -}", 2): "DarkGreen", ("beta", 2): "Cyan"})
    assert colour_of(app.snap(), "{- aaa") == "Gray"  # the long line itself has no colours


def test_a_vim_before_9_is_asked_in_the_old_script_language(ltt, ws):
    # vim 8 has no vim9 script: the same questions in the old language (a vim 9 here says it is older)
    with_vim(ws)
    fake_vim(ws, '"$REAL_VIM" "$@" | while IFS= read -r line; do\n'
                 '  [ "$line" = "ltt V 9" ] && line="ltt V 8"\n  printf "%s\\n" "$line"\ndone')
    app = open_text(ltt, ws, 'alpha "s\\"t" 42 -- note\nbeta é中 gamma\n', name="a.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ('"s\\"t"', 1): "Yellow", ("42", 1): "Magenta", ("-- note", 1): "DarkGreen",
                       ("gamma", 2): "Cyan"})
    app.type("{-")
    wait_colours(app, {("{-alpha", 1): "DarkGreen", ("gamma", 2): "DarkGreen"})


# ---------------------------------------------------------------- before vim answers (it is slow here)

def test_colours_move_with_lines_at_once(ltt, ws):
    slow_vim(ws)
    app = open_text(ltt, ws, 'plain\nalpha here\n"str" beta\n', name="a.ltttest")
    wait_colours(app, {("alpha", 2): "Cyan", ('"str"', 3): "Yellow"})
    s = burst(app, "enter", "enter")  # the frame right after the keys, long before vim's answer
    assert [colour_of(s, w, n) for w, n in (("plain", 3), ("alpha", 4), ('"str"', 5), ("beta", 5))] == \
        ["Gray", "Cyan", "Yellow", "Cyan"]
    s = burst(app, "backspace", "backspace")
    assert [colour_of(s, w, n) for w, n in (("plain", 1), ("alpha", 2), ('"str"', 3))] == ["Gray", "Cyan", "Yellow"]


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


def test_a_line_split_in_two_keeps_both_halves_coloured_at_once(ltt, ws):
    slow_vim(ws)
    app = open_text(ltt, ws, 'alpha "str" beta\n', name="a.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ('"str"', 1): "Yellow", ("beta", 1): "Cyan"})
    app.press(*["right"] * 11)
    s = app.press("enter")
    assert s.editor_lines[:2] == ['alpha "str"', " beta"]
    assert [colour_of(s, w, n) for w, n in (("alpha", 1), ('"str"', 1), ("beta", 2))] == ["Cyan", "Yellow", "Cyan"]


# ---------------------------------------------------------------- files, names and tabs

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


def test_a_file_name_with_quotes_spaces_and_symbols(ltt, ws):
    app = vim_text(ltt, ws, "alpha -- note\n", name="a b%#|'\"$x\\y!.ltttest")
    wait_colours(app, {("alpha", 1): "Cyan", ("-- note", 1): "DarkGreen"})


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


def test_a_file_changed_on_disk_is_coloured_anew(ltt, ws):
    app = vim_text(ltt, ws, "alpha\nbeta\n")
    wait_colours(app, {("alpha", 1): "Cyan", ("beta", 2): "Cyan"})
    ws.write("a.ltttest", "{- alpha\nbeta -} gamma\n")
    wait_colours(app, {("{- alpha", 1): "DarkGreen", ("beta -}", 2): "DarkGreen", ("gamma", 2): "Cyan"})


# ---------------------------------------------------------------- what vim may not do

def test_a_modeline_in_the_file_is_not_obeyed(ltt, ws):
    # a modeline could set any option (CVE-2019-12735): ltt's vim reads none
    app = vim_text(ltt, ws, "alpha int\n-- vim: set filetype=c syntax=c :\n")
    wait_colours(app, {("alpha", 1): "Cyan", ("int", 1): "Gray"})


def test_the_vimrc_and_plugins_are_not_read(ltt, ws):
    marker = os.path.join(ws.sys, "vimrc-ran")
    for rel in (".vimrc", ".vim/vimrc", ".vim/plugin/p.vim"):
        ws.write(os.path.join(ws.home, rel), f"call writefile(['ran'], '{marker}')\nsyntax off\n")
    app = vim_text(ltt, ws, "alpha\n")
    wait_coloured(app, "alpha", "Cyan")
    assert not os.path.exists(marker)


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

def test_a_vim_that_starts_late_colours_the_text_when_it_is_ready(ltt, ws):
    go = os.path.join(ws.sys, "go")
    with_vim(ws)
    fake_vim(ws, f'while [ ! -e "{go}" ]; do sleep 0.05; done\nexec "$REAL_VIM" "$@"')
    app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
    s = app.type("beta ")  # ltt answers keys while its vim is not there yet
    assert s.editor_lines[0] == "beta alpha" and line_colours(s, 1) == {"Gray"}
    open(go, "w").close()
    wait_colours(app, {("beta", 1): "Cyan", ("alpha", 1): "Cyan"})


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


def test_what_vim_prints_never_reaches_the_terminal(ltt, ws):
    with_vim(ws)
    fake_vim(ws, "echo junk; echo junk >&2; printf '\\033]0;PWNED\\007\\033[2J'; printf '\\033]0;PWNED2\\007' >&2\n"
                 'exec "$REAL_VIM" "$@"')
    app = open_text(ltt, ws, "alpha\n", name="a.ltttest")
    wait_coloured(app, "alpha", "Cyan")
    s = app.snap()
    assert "junk" not in "".join(s.lines) and s.window_title == ""
    assert foreign_controls(session_output(app)) == []


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


def test_a_syntax_that_hangs_vim_leaves_ltt_working_and_quitting_stops_vim(ltt, ws):
    pids = logging_vim(ws)
    install_syntax(ws, "ltthang", "while 1\nendwhile\n", "*.ltthang")
    try:
        app = open_text(ltt, ws, "alpha\n", name="a.ltthang")
        app.wait(lambda s: pids_of(pids), what="vim started")
        time.sleep(0.5)
        s = app.type("beta ")
        assert s.editor_lines[0] == "beta alpha"
        for _ in range(3):  # edits pile up for a vim that reads nothing: more than a pipe holds
            app.paste("".join(f"line {i} of a paste\n" for i in range(2500)))
        assert app.press("ctrl+s").status == "Saved: a.ltthang"
        t = time.monotonic()
        assert app.quit() == 0 and time.monotonic() - t < 3
        assert wait_gone(pids_of(pids)) == []
    finally:
        stop(pids_of(pids))


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
