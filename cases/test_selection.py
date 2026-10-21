"""Selection with Shift: extending from the anchor, collapsing, replacing and deleting it, Ctrl+A,
the "selected: N" count, and the anchor bugs of the review (D4). What is selected is read back
through Ctrl+C (ltt sends the copy to the terminal as OSC 52)."""
import pytest

from editor_helpers import burst, line_col, open_text, save, selection

# ---------------------------------------------------------------- extending


def test_shift_right_selects_characters(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    s = burst(app, *["shift+right"] * 5)
    assert s.pos == (1, 2, 6, 5)
    assert s.status_line.endswith(" Ln 1/2, Col 6 · selected: 5 ")
    assert selection(app) == "hello"


def test_shift_left_selects_backwards(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    app.press("end")
    s = burst(app, *["shift+left"] * 5)
    assert s.pos == (1, 2, 7, 5)
    assert selection(app) == "world"


def test_shift_down_selects_to_the_same_column_on_the_next_line(ltt, ws):
    app = open_text(ltt, ws, "abcd\nefgh\n")
    app.press("right", "right")
    s = app.press("shift+down")
    assert s.pos == (2, 3, 3, 5)  # a line break counts as one character
    assert selection(app) == "cd\nef"


def test_shift_up_selects_to_the_same_column_on_the_line_above(ltt, ws):
    app = open_text(ltt, ws, "abcd\nefgh\n")
    burst(app, "down", "right", "right")
    s = app.press("shift+up")
    assert s.pos == (1, 3, 3, 5)
    assert selection(app) == "cd\nef"


def test_shift_end_selects_to_the_line_end(ltt, ws):
    app = open_text(ltt, ws, "foo bar\n")
    burst(app, *["right"] * 4)
    s = app.press("shift+end")
    assert s.pos == (1, 2, 8, 3)
    assert selection(app) == "bar"


def test_shift_home_selects_to_the_first_non_blank_then_to_the_line_start(ltt, ws):
    app = open_text(ltt, ws, "    foo bar\n")
    app.press("end")
    app.press("shift+home")
    assert selection(app) == "foo bar"
    app.press("shift+home")
    assert selection(app) == "    foo bar"


def test_ctrl_shift_home_and_end_select_to_the_document_ends(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\nef")
    app.press("down", "right")
    app.press("ctrl+shift+end")
    assert selection(app) == "d\nef"
    app.press("ctrl+shift+home")
    assert selection(app) == "ab\nc"


def test_shift_page_down_selects_a_screen_of_lines(ltt, ws):
    app = open_text(ltt, ws, "".join(f"line {i}\n" for i in range(1, 101)))
    s = app.press("shift+pgdn")
    page = "".join(f"line {i}\n" for i in range(1, 27))
    assert line_col(s) == (27, 1) and s.pos[3] == len(page)
    assert selection(app) == page
    s = app.press("shift+pgup")
    assert line_col(s) == (1, 1) and s.pos[3] == 0  # back at the anchor: nothing selected


def test_alt_shift_arrows_select_by_words(ltt, ws):
    app = open_text(ltt, ws, "foo bar baz\n")
    app.press("alt+shift+right", "alt+shift+right")
    assert selection(app) == "foo bar"
    app.press("alt+shift+left")
    assert selection(app) == "foo "


def test_alt_shift_down_selects_to_the_next_paragraph(ltt, ws):
    app = open_text(ltt, ws, "a\nb\n\nc\n")
    app.press("alt+shift+down")
    assert selection(app) == "a\nb\n\n"


def test_further_shift_moves_keep_the_anchor(ltt, ws):
    app = open_text(ltt, ws, "abcdef\n")
    burst(app, "right", "right", "shift+right", "shift+right", "shift+right")
    assert selection(app) == "cde"
    s = burst(app, *["shift+left"] * 5)  # back over the anchor: the selection flips to its other side
    assert s.pos == (1, 2, 1, 2)
    assert selection(app) == "ab"


# ---------------------------------------------------------------- collapsing and dropping


def test_left_collapses_the_selection_to_its_start(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, *["right"] * 6, *["shift+right"] * 3)
    s = app.press("left")
    assert s.pos == (1, 2, 7, 0)


def test_right_collapses_the_selection_to_its_end(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, "end", *["shift+left"] * 5)
    s = app.press("right")
    assert s.pos == (1, 2, 12, 0)


def test_left_collapses_a_selection_of_several_lines_to_its_start(ltt, ws):
    app = open_text(ltt, ws, "abcd\nefgh\nijkl\n")
    burst(app, "right", "shift+down", "shift+down", "shift+right")
    s = app.press("left")
    assert s.pos == (1, 4, 2, 0)


@pytest.mark.parametrize("key, where", [
    ("down", (3, 4)), ("up", (1, 4)), ("home", (2, 1)), ("end", (2, 5)), ("alt+right", (2, 5)),
])
def test_moving_without_shift_drops_the_selection(ltt, ws, key, where):
    app = open_text(ltt, ws, "abcd\nefgh\nijkl\n")
    burst(app, "down", "right", "shift+right", "shift+right")
    s = app.press(key)
    assert line_col(s) == where and s.pos[3] == 0
    assert selection(app) is None


@pytest.mark.parametrize("key", ["ctrl+shift+left", "ctrl+shift+right", "ctrl+shift+up", "ctrl+shift+down"])
def test_ctrl_shift_arrows_do_nothing(ltt, ws, key):
    app = open_text(ltt, ws, "abc def\nghi jkl\nmno\n")
    before = burst(app, "down", "right", "shift+right")
    s = app.press(key)
    assert s.pos == before.pos == (2, 4, 3, 1)
    assert s.editor_lines == before.editor_lines
    assert selection(app) == "h"


# ---------------------------------------------------------------- replacing and deleting


def test_typing_replaces_the_selection(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, *["right"] * 6, *["shift+right"] * 5)
    s = app.type("there")
    assert s.editor_lines[0] == "hello there" and s.pos == (1, 2, 12, 0)
    assert save(app, ws) == "hello there\n"


def test_typing_replaces_a_selection_of_several_lines(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\nghi\n")
    burst(app, "right", "shift+down", "shift+down")
    s = app.type("X")
    assert s.editor_lines == ["aXhi", ""] and s.pos == (1, 2, 3, 0)


def test_backspace_deletes_just_the_selection(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, *["right"] * 5, *["shift+right"] * 6)
    s = app.press("backspace")
    assert s.editor_lines[0] == "hello" and s.pos == (1, 2, 6, 0)
    assert save(app, ws) == "hello\n"


def test_delete_deletes_just_the_selection(ltt, ws):
    app = open_text(ltt, ws, "hello world\n")
    burst(app, "end", *["shift+left"] * 6)
    s = app.press("delete")
    assert s.editor_lines[0] == "hello" and s.pos == (1, 2, 6, 0)
    assert save(app, ws) == "hello\n"


def test_backspace_deletes_a_selection_of_several_lines(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\nghi\n")
    burst(app, "right", "shift+down", "shift+right")
    s = app.press("backspace")
    assert s.editor_lines == ["af", "ghi", ""] and s.pos == (1, 3, 2, 0)


def test_enter_replaces_the_selection_with_a_line_break(ltt, ws):
    app = open_text(ltt, ws, "  abcd\n")
    burst(app, "right", "right", "right", "shift+right", "shift+right")
    s = app.press("enter")
    assert s.editor_lines[:2] == ["  a", "  d"]  # and keeps the indentation
    assert line_col(s) == (2, 3)
    assert save(app, ws) == "  a\n  d\n"


def test_tab_replaces_the_selection(ltt, ws):
    app = open_text(ltt, ws, "abcdef\n")
    burst(app, "right", "shift+right", "shift+right")
    s = app.press("tab")
    assert line_col(s) == (1, 5)
    assert save(app, ws) == "a   def\n"


# ---------------------------------------------------------------- select all and the count


def test_ctrl_a_selects_the_whole_text(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\n")
    s = app.press("ctrl+a")
    assert s.status == "Selected all: 6 chars."
    assert s.pos == (3, 3, 1, 6)  # the cursor goes to the end
    assert selection(app) == "ab\ncd\n"


def test_typing_after_ctrl_a_replaces_everything(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\n")
    app.press("ctrl+a")
    s = app.type("new")
    assert s.editor_lines == ["new"] and s.pos == (1, 1, 4, 0)
    assert save(app, ws) == "new"


def test_a_crlf_line_break_counts_as_one_selected_character(ltt, ws):
    app = open_text(ltt, ws, "ab\r\ncd\r\n")
    s = app.press("ctrl+a")
    assert s.pos[3] == 6
    assert selection(app) == "ab\ncd\n"


def test_the_count_is_of_characters_not_cells(ltt, ws):
    app = open_text(ltt, ws, "日本語 abc\n")
    s = burst(app, *["shift+right"] * 4)
    assert s.pos[2:] == (8, 4)
    assert selection(app) == "日本語 "


# ---------------------------------------------------------------- the anchor (D4)


def test_an_emptied_selection_does_not_eat_text_typed_next(ltt, ws):
    # D4: Shift+← Shift+→ left an empty anchor that became a real selection after the next edit
    app = open_text(ltt, ws, "abc\n")
    app.press("right")
    s = app.press("shift+left", "shift+right")
    assert s.pos == (1, 2, 2, 0)
    app.press("X")
    app.press("Y")
    assert save(app, ws) == "aXYbc\n"


def test_shift_up_on_the_first_line_does_not_eat_text_typed_next(ltt, ws):
    # D4: a Shift move that goes nowhere sets an anchor at the cursor
    app = open_text(ltt, ws, "abc\n")
    app.press("right")
    s = app.press("shift+up")
    assert s.pos == (1, 2, 2, 0)
    app.press("X")
    app.press("Y")
    assert save(app, ws) == "aXYbc\n"


def test_ctrl_end_shift_right_and_two_backspaces_do_not_crash(ltt, ws):
    # D4: this threw ArgumentOutOfRangeException and took every unsaved tab down
    app = open_text(ltt, ws, "abc\n")
    app.press("ctrl+end", "shift+right", "backspace", "backspace")
    s = app.snap()
    assert s.editor_lines == ["ab"] and s.pos == (1, 1, 3, 0)
    assert save(app, ws) == "ab"
