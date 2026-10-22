"""Editing: typing, Enter and auto-indent, Backspace/Delete and joining lines, Tab, word delete,
Ctrl+D (duplicate lines), and whole characters (emoji, flags, combining marks) — checked on screen
and in the saved file."""
import pytest

from editor_helpers import CLUSTERS, burst, line_col, mark_cursor, open_text, save
from prompts_helpers import rename

# ---------------------------------------------------------------- typing


def test_typing_inserts_at_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, *["right"] * 5)
    s = app.type(",")
    assert s.editor_lines[0] == "hello, world"
    assert line_col(s) == (1, 7)
    assert s.title_dirty and s.tabs[0].dirty
    assert save(app, ws) == "hello, world\n"


def test_a_fast_burst_of_typing_arrives_whole(ltt, ws):
    # more keys than ltt handles between two frames (64): none may be lost or reordered
    text = "".join(chr(ord("a") + i % 26) for i in range(300))
    app = open_text(ltt, ws, "\n")
    app.type(text)
    assert save(app, ws) == text + "\n"


def test_typing_non_ascii_letters(ltt, ws):
    app = open_text(ltt, ws, "\n")
    s = app.type("Привет, ünïcödé")
    assert s.editor_lines[0] == "Привет, ünïcödé"
    assert line_col(s) == (1, 16)
    assert save(app, ws) == "Привет, ünïcödé\n"


def test_a_typed_emoji_is_inserted_whole(ltt, ws):
    # D5: an emoji arrives as two UTF-16 halves; the file must get the character, never U+FFFD
    app = open_text(ltt, ws, "ab\n")
    app.press("right")
    s = app.type("👀")
    assert line_col(s) == (1, 4)  # two cells wide
    assert save(app, ws) == "a👀b\n"


def test_typing_then_deleting_it_leaves_the_tab_clean(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    s = app.type("x")
    assert s.title_dirty
    s = app.press("backspace")
    assert not s.title_dirty and not s.tabs[0].dirty  # compared with the saved text, not "was edited"


# ---------------------------------------------------------------- Enter


def test_enter_splits_the_line_at_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "abcdef\n")
    burst(app, "right", "right", "right")
    s = app.press("enter")
    assert s.editor_lines == ["abc", "def", ""]
    assert line_col(s) == (2, 1)
    assert save(app, ws) == "abc\ndef\n"


def test_enter_repeats_the_indentation_and_adds_none(ltt, ws):
    app = open_text(ltt, ws, "    if x:\n", name="a.py")
    app.press("end", "enter")
    s = app.type("y")
    assert s.editor_lines[:2] == ["    if x:", "    y"]  # the same indent, nothing more after the colon
    assert line_col(s) == (2, 6)
    assert save(app, ws, "a.py") == "    if x:\n    y\n"


def test_enter_repeats_tabs_and_spaces_exactly(ltt, ws):
    app = open_text(ltt, ws, "\t  x\n")
    app.press("end", "enter")
    app.type("y")
    assert save(app, ws) == "\t  x\n\t  y\n"


def test_enter_inside_the_indentation_takes_only_the_part_before_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "    foo\n")
    burst(app, "right", "right")
    s = app.press("enter")
    assert line_col(s) == (2, 3)
    assert save(app, ws) == "  \n    foo\n"


def test_enter_at_a_line_start_pushes_the_line_down(ltt, ws):
    app = open_text(ltt, ws, "  abc\n")
    s = app.press("enter")
    assert s.editor_lines == ["", "  abc", ""]
    assert line_col(s) == (2, 1)
    assert save(app, ws) == "\n  abc\n"


def test_enter_at_the_end_of_a_file_adds_a_line(ltt, ws):
    app = open_text(ltt, ws, "abc")  # no final newline
    app.press("end")
    s = app.press("enter")
    assert s.pos[:3] == (2, 2, 1)
    assert save(app, ws) == "abc\n"


def test_enter_in_a_crlf_file_makes_a_crlf_line_break(ltt, ws):
    app = open_text(ltt, ws, "one\r\ntwo\r\n")
    app.press("end", "enter")
    app.type("new")
    assert save(app, ws) == "one\r\nnew\r\ntwo\r\n"


# ---------------------------------------------------------------- Backspace and Delete


def test_backspace_deletes_the_character_before_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    app.press("end")
    s = app.press("backspace")
    assert s.editor_lines[0] == "ab" and line_col(s) == (1, 3)
    assert save(app, ws) == "ab\n"


def test_backspace_at_a_line_start_joins_the_line_to_the_one_above(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\n")
    app.press("down")
    s = app.press("backspace")
    assert s.editor_lines == ["abcdef", ""]
    assert s.pos[:3] == (1, 2, 4)  # at the joint
    assert save(app, ws) == "abcdef\n"


def test_backspace_at_the_document_start_does_nothing(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    s = app.press("backspace")
    assert s.editor_lines[0] == "abc" and line_col(s) == (1, 1)
    assert not s.title_dirty
    s = app.press("ctrl+z")
    assert s.status == "Nothing to undo."  # not even an empty step in the history


def test_delete_removes_the_character_under_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    app.press("right")
    s = app.press("delete")
    assert s.editor_lines[0] == "ac" and line_col(s) == (1, 2)
    assert save(app, ws) == "ac\n"


def test_delete_at_a_line_end_joins_the_next_line(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\n")
    app.press("end")
    s = app.press("delete")
    assert s.editor_lines == ["abcdef", ""]
    assert line_col(s) == (1, 4)
    assert save(app, ws) == "abcdef\n"


def test_delete_at_the_document_end_does_nothing(ltt, ws):
    app = open_text(ltt, ws, "abc")
    app.press("ctrl+end")
    s = app.press("delete")
    assert s.editor_lines == ["abc"] and line_col(s) == (1, 4)
    assert not s.title_dirty


@pytest.mark.parametrize("cluster", list(CLUSTERS.values()), ids=list(CLUSTERS))
def test_backspace_deletes_a_whole_character(ltt, ws, cluster):
    # D5: one Backspace after an emoji left half a surrogate pair (saved as U+FFFD)
    app = open_text(ltt, ws, f"a{cluster}b\n")
    app.press("end", "left", "backspace")
    assert save(app, ws) == "ab\n"


@pytest.mark.parametrize("cluster", list(CLUSTERS.values()), ids=list(CLUSTERS))
def test_delete_removes_a_whole_character(ltt, ws, cluster):
    app = open_text(ltt, ws, f"a{cluster}b\n")
    app.press("right", "delete")
    assert save(app, ws) == "ab\n"


FLAGS = ["🇺🇸", "🇩🇪", "🇫🇷", "🇯🇵"] * 10  # 40 flags, 160 UTF-16 units


def test_backspace_in_a_long_run_of_flags_deletes_one_whole_flag(ltt, ws):
    # V8: a long run of flag emoji could be split between two flags' letters
    app = open_text(ltt, ws, "".join(FLAGS) + "\n")
    s = burst(app, "end", *["left"] * 5)
    assert line_col(s) == (1, 71)  # each flag is two cells
    app.press("backspace")
    assert save(app, ws) == "".join(FLAGS[:34] + FLAGS[35:]) + "\n"


def test_delete_in_a_long_run_of_flags_deletes_one_whole_flag(ltt, ws):
    # V8
    app = open_text(ltt, ws, "".join(FLAGS) + "\n")
    burst(app, "end", *["left"] * 7)
    app.press("delete")
    assert save(app, ws) == "".join(FLAGS[:33] + FLAGS[34:]) + "\n"


# ---------------------------------------------------------------- Tab


def test_tab_inserts_spaces_up_to_the_next_tab_stop(ltt, ws):
    app = open_text(ltt, ws, "x\n  indented\n")
    s = app.press("tab")
    assert line_col(s) == (1, 5)
    app.type("ab")
    s = app.press("tab")
    assert line_col(s) == (1, 9)  # two spaces: from column 7 to the stop at 9
    assert save(app, ws) == "    ab  x\n  indented\n"


def test_tab_stops_count_the_cells_of_wide_characters(ltt, ws):
    app = open_text(ltt, ws, "日\n")
    app.press("end")
    s = app.press("tab")
    assert line_col(s) == (1, 5)
    assert save(app, ws) == "日  \n"


@pytest.mark.parametrize("text", ["a\n  b\n", "a\n\tb\n  c\n", ""],
                         ids=["space-indented", "as-many-tabs-as-spaces", "empty"])
def test_tab_inserts_spaces_in_files_not_indented_with_tabs(ltt, ws, text):
    app = open_text(ltt, ws, text)
    app.press("tab")
    assert save(app, ws) == "    " + text


@pytest.mark.parametrize("name, text", [
    ("tabbed.txt", "a\n\tb\n\tc\n  d\n"),   # its indented lines mostly start with a tab
    ("Makefile", "all:\n"),                  # by name, before any recipe exists
    ("GNUmakefile", "all:\n"),
    ("rules.mk", "x:\n"),
    ("main.go", "package main\n"),
])
def test_tab_inserts_a_tab_in_files_indented_with_tabs(ltt, ws, name, text):
    app = open_text(ltt, ws, text, name=name)
    app.press("tab")
    assert save(app, ws, name) == "\t" + text


def test_makefile_recipes_keep_their_tabs(ltt, ws):
    # D2: tabs became 4 spaces on open and were saved that way, breaking Makefile recipes
    app = open_text(ltt, ws, "all:\n\techo hi\n", name="Makefile")
    app.press("down", "end", "enter")
    s = app.type("ls")
    assert s.editor_lines[:3] == ["all:", "    echo hi", "    ls"]  # a tab is only drawn as spaces
    assert save(app, ws, "Makefile") == "all:\n\techo hi\n\tls\n"


def test_tabs_in_the_text_are_saved_as_tabs(ltt, ws):
    # D2
    app = open_text(ltt, ws, "a\tb\n\tc\n")
    app.type("X")
    assert save(app, ws) == "Xa\tb\n\tc\n"


# ---------------------------------------------------------------- word delete


def test_alt_backspace_deletes_the_word_before_the_cursor(ltt, ws):
    app = open_text(ltt, ws, "hello brave world\n")
    app.press("end")
    s = app.press("alt+backspace")  # Esc DEL, as Option+Backspace arrives
    assert s.editor_lines[0] == "hello brave" and line_col(s) == (1, 13)
    s = app.press("alt+backspace")
    assert line_col(s) == (1, 7)
    assert save(app, ws) == "hello \n"


@pytest.mark.parametrize("keyboard, key", [
    ("legacy", b"\x1b\x08"),           # Esc ^H: Option+Backspace where Backspace sends ^H
    ("kitty", "alt+backspace"),
    ("kitty", "ctrl+backspace"),
], ids=["esc-ctrl-h", "kitty-alt", "kitty-ctrl"])
def test_other_word_delete_keys(ltt, ws, keyboard, key):
    app = open_text(ltt, ws, "hello world\n", keyboard=keyboard)
    app.press("end", key)
    assert save(app, ws) == "hello \n"


def test_ctrl_backspace_of_a_legacy_terminal_deletes_one_character(ltt, ws):
    # A legacy terminal sends ^H for Ctrl+Backspace, the same byte as some send for Backspace.
    app = open_text(ltt, ws, "hello world\n")
    app.press("end")
    s = app.press("ctrl+backspace")
    assert s.editor_lines[0] == "hello worl"


def test_word_delete_takes_the_spaces_before_the_cursor_too(ltt, ws):
    app = open_text(ltt, ws, "foo bar   \n")
    app.press("end", "alt+backspace")
    assert save(app, ws) == "foo \n"


def test_word_delete_in_the_middle_of_a_word_deletes_its_first_part(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, *["right"] * 8)
    s = app.press("alt+backspace")
    assert s.editor_lines[0] == "hello rld" and line_col(s) == (1, 7)


def test_word_delete_takes_a_run_of_punctuation_as_one_word(ltt, ws):
    app = open_text(ltt, ws, "foo.bar();\n")
    app.press("end")
    s = app.press("alt+backspace")
    assert s.editor_lines[0] == "foo.bar"
    s = app.press("alt+backspace")
    assert s.editor_lines[0] == "foo."
    s = app.press("alt+backspace")
    assert s.editor_lines[0] == "foo"


def test_word_delete_at_a_line_start_joins_the_lines(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\n")
    app.press("down")
    s = app.press("alt+backspace")
    assert s.editor_lines[0] == "abcdef" and line_col(s) == (1, 4)


def test_word_delete_takes_a_word_with_combining_accents_whole(ltt, ws):
    # V3: Alt+Backspace deleted only the accent of a decomposed (NFD) letter
    app = open_text(ltt, ws, "voila\u0300 cafe\u0301\n")
    app.press("end", "alt+backspace")
    assert save(app, ws) == "voila\u0300 \n"


def test_word_delete_takes_a_word_with_indic_vowel_signs_whole(ltt, ws):
    # V3: vowel signs and the virama are combining marks inside the word
    app = open_text(ltt, ws, "नमस्ते दुनिया\n")
    app.press("end", "alt+backspace")
    assert save(app, ws) == "नमस्ते \n"


def test_word_delete_takes_a_cjk_run_as_one_word(ltt, ws):
    app = open_text(ltt, ws, "abc 日本語\n")
    app.press("end", "alt+backspace")
    assert save(app, ws) == "abc \n"


def test_word_delete_never_splits_an_emoji(ltt, ws):
    app = open_text(ltt, ws, "go 👀👀\n")
    app.press("end", "alt+backspace")
    assert save(app, ws) == "go \n"


# ---------------------------------------------------------------- Ctrl+D: duplicate lines


@pytest.mark.parametrize("keyboard", ["legacy", "kitty"])
def test_ctrl_d_duplicates_the_line_and_the_cursor_moves_onto_the_copy(ltt, ws, keyboard):
    app = open_text(ltt, ws, "one\ntwo\nthree\n", keyboard=keyboard)
    burst(app, "down", "right", "right")
    s = app.press("ctrl+d")
    assert s.editor_lines == ["one", "two", "two", "three", ""]
    assert line_col(s) == (3, 3) and s.title_dirty
    assert mark_cursor(app, ws) == "one\ntwo\ntw|o\nthree\n"


def test_ctrl_d_again_adds_another_copy(ltt, ws):
    app = open_text(ltt, ws, "a\nb\n")
    s = app.press("ctrl+d", "ctrl+d")
    assert s.editor_lines == ["a", "a", "a", "b", ""] and line_col(s) == (3, 1)


def test_ctrl_d_duplicates_the_lines_a_selection_touches_and_moves_it_onto_the_copy(ltt, ws):
    app = open_text(ltt, ws, "a\nbb\ncc\nd\n")
    burst(app, "down", "right", "shift+down")  # from b|b to c|c
    s = app.press("ctrl+d")
    assert s.editor_lines == ["a", "bb", "cc", "bb", "cc", "d", ""]
    assert s.pos == (5, 7, 2, 3)  # the same three characters selected, in the copy
    app.type("X")  # replaces them
    assert save(app, ws) == "a\nbb\ncc\nbXc\nd\n"


def test_a_selection_that_ends_at_a_line_start_leaves_that_line_out(ltt, ws):
    app = open_text(ltt, ws, "a\nb\nc\n")
    app.press("shift+down", "shift+down")  # "a" and "b" with their line breaks
    s = app.press("ctrl+d")
    assert s.editor_lines == ["a", "b", "a", "b", "c", ""]
    assert s.pos == (5, 6, 1, 4)
    app.type("X")
    assert save(app, ws) == "a\nb\nXc\n"


@pytest.mark.parametrize("text, keys, want", [
    ("one\r\ntwo\r\n", [], "one\r\none\r\ntwo\r\n"),
    ("one\ntwo", ["ctrl+end"], "one\ntwo\ntwo"),
    ("one\ntwo\n", ["ctrl+a"], "one\ntwo\none\ntwo\n"),
], ids=["crlf", "no-final-newline", "whole-text"])
def test_ctrl_d_keeps_the_line_breaks_of_the_file(ltt, ws, text, keys, want):
    app = open_text(ltt, ws, text)
    app.press(*keys, "ctrl+d")
    assert save(app, ws) == want


@pytest.mark.parametrize("keys", [["esc"], ["ctrl+f"], ["ctrl+l"]], ids=["tree", "search", "go-to"])
def test_ctrl_d_outside_the_editor_duplicates_nothing(ltt, ws, keys):
    app = open_text(ltt, ws, "one\ntwo\n")
    before = app.press(*keys)
    s = app.press("ctrl+d")
    assert (s.mode, s.status_line) == (before.mode, before.status_line)
    assert s.editor_lines == ["one", "two", ""] and not s.title_dirty


# ---------------------------------------------------------------- the cursor after an edit, tabs after a rename


@pytest.mark.parametrize("text, keys, want", [
    ("cafe\n\u0301 noir\n", ["down", "backspace"], "cafe\u0301| noir\n"),
    ("cafe\n\u0301 noir\n", ["end", "delete"], "cafe\u0301| noir\n"),
    ("cafe\n\u0301 noir\n", ["end", "shift+right", "delete"], "cafe\u0301| noir\n"),
    ("\U0001F44D\n\U0001F3FD ok\n", ["down", "backspace"], "\U0001F44D\U0001F3FD| ok\n"),
    ("\u0301x\n", ["a"], "a\u0301|x\n"),
], ids=["backspace-join", "delete-join", "selection-join", "skin-tone-join", "typed-before-a-mark"])
def test_the_cursor_never_stops_inside_a_character_after_an_edit(ltt, ws, text, keys, want):
    # Ox's kaolinite document_splitting_splicing
    app = open_text(ltt, ws, text)
    for k in keys:
        app.press(k)
    assert mark_cursor(app, ws) == want


@pytest.mark.parametrize("name", ["Makefile", "main.go"])
def test_tab_inserts_a_tab_after_a_rename_to_a_tab_indented_kind(ltt, ws, name):
    # Textadept's file_io_test.lua 'buffer.save_as should update the lexer'
    app = open_text(ltt, ws, "all:\n", name="build.txt")
    rename(app, name)
    app.wait(lambda s: s.title_path.endswith("/" + name), what="the rename")
    app.press("down", "tab")
    assert save(app, ws, name) == "all:\n\t"


def test_lines_of_spaces_alone_do_not_outvote_tab_indentation(ltt, ws):
    # Textadept's file_io_test.lua 'io.open_file should detect and switch to tabs'
    app = open_text(ltt, ws, "f {\n\tone\n    \n\ttwo\n    \n    \n}\n", name="a.c")
    app.press("down", "end", "tab")
    assert save(app, ws, "a.c").split("\n")[1] == "\tone\t"
