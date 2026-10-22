"""What the editor shows: wide characters, tabs and combining marks in the right cells, long lines
scrolled sideways with dim ‹ › markers, the gutter, the current line, ~ rows, the cursor, selection
colours and the position in the status line. At 100x30 the text area is 63 cells wide."""
import pytest

from editor_helpers import burst, line_col, open_text, row_y
from harness import keys as K

WIDE = "ascii line\n日本語のテキスト ⚡ text\n\tx = find(me);\n"
LONG = "".join(chr(ord("a") + i % 26) for i in range(200))

# ---------------------------------------------------------------- wide characters, tabs, marks


def test_the_cursor_after_cjk_and_emoji_sits_right_after_the_text(ltt, ws):
    # U1: widths were counted in UTF-16 units: the cursor sat 3 cells short of the line end
    app = open_text(ltt, ws, WIDE)
    s = app.press("down", "end")
    y = row_y(s, 2)
    assert line_col(s) == (2, 25)
    assert s.cursor == (s.text_x + 24, y) and not s.cursor_hidden
    assert s.text(y, s.cursor[0] - 4, s.cursor[0]) == "text"
    assert s.cell(*s.cursor).ch == " "


def test_the_cursor_steps_over_the_two_cells_of_a_wide_character(ltt, ws):
    # U1
    app = open_text(ltt, ws, WIDE)
    s = burst(app, "down", "end", *["left"] * 6)
    assert line_col(s) == (2, 18)
    assert s.cursor[0] == s.text_x + 17 and s.cell(*s.cursor).ch == "⚡"
    s = app.press("left", "left")
    assert s.cursor[0] == s.text_x + 14 and s.cell(*s.cursor).ch == "ト"


def test_the_cursor_at_the_end_of_a_line_with_a_tab_sits_after_the_text(ltt, ws):
    app = open_text(ltt, ws, WIDE)
    s = app.press("down", "down", "end")
    assert line_col(s) == (3, 18)
    assert s.cursor == (s.text_x + 17, row_y(s, 3))
    assert s.text(s.cursor[1], s.cursor[0] - 4, s.cursor[0]) == "me);"


def test_a_click_on_the_right_half_of_a_wide_character_puts_the_cursor_before_it(ltt, ws):
    # U1: clicks were placed by UTF-16 index too
    app = open_text(ltt, ws, WIDE)
    s = app.snap()
    y = row_y(s, 2)
    s = app.click(s.text_x + 3, y)  # the right half of 本
    assert line_col(s) == (2, 3) and s.cursor == (s.text_x + 2, y)
    s = app.click(s.text_x + 18, y)  # the right half of ⚡
    assert line_col(s) == (2, 18) and s.cursor == (s.text_x + 17, y)


def test_tabs_are_drawn_up_to_the_next_tab_stop(ltt, ws):
    app = open_text(ltt, ws, "a\tb\nabcd\te\n\t\tf\n")
    s = app.snap()
    assert s.editor_lines[:3] == ["a   b", "abcd    e", "        f"]
    s = app.press("right", "right")
    assert line_col(s) == (1, 5) and s.cursor[0] == s.text_x + 4


def test_a_tab_after_a_wide_character_ends_at_the_same_tab_stop(ltt, ws):
    app = open_text(ltt, ws, "日\tx\n")
    s = app.press("end")
    assert line_col(s) == (1, 6)
    assert s.cell(s.text_x + 4, row_y(s, 1)).ch == "x"


def test_combining_marks_take_no_cell_of_their_own(ltt, ws):
    app = open_text(ltt, ws, "cafe\u0301 ok\n")
    s = app.press("end")
    y = row_y(s, 1)
    assert line_col(s) == (1, 8)
    assert s.cell(s.text_x + 3, y).ch == "é"  # the accent is drawn on its e
    assert s.text(y, s.text_x + 5, s.text_x + 7) == "ok"
    assert s.cursor == (s.text_x + 7, y)


def test_a_combining_mark_at_the_start_of_a_line_stays_out_of_the_gutter(ltt, ws):  # micro's line_array tests
    app = open_text(ltt, ws, "\u0301abc\n")
    s = app.snap()
    y = row_y(s, 1)
    assert s.cell(s.text_x - 1, y).ch == " "
    assert s.text(y, s.text_x, s.text_x + 3) == "abc"


# Bidi controls ("Trojan Source": a bidi-aware terminal would reorder the line around them) and
# characters that show nothing (ideas from Helix's adversarial buffer tests, dte's u_is_zero_width
# and Neovim's <200b>).
INVISIBLE = {
    "RLO": "\u202e", "PDF": "\u202c", "LRI": "\u2066", "PDI": "\u2069", "LRM": "\u200e", "ALM": "\u061c",
    "zero-width space": "\u200b", "word joiner": "\u2060", "BOM in the text": "\ufeff", "soft hyphen": "\u00ad",
    "line separator": "\u2028",
}


@pytest.mark.parametrize("name", INVISIBLE)
def test_an_invisible_character_shows_as_a_stand_in(ltt, ws, name):
    ch = INVISIBLE[name]
    app = open_text(ltt, ws, f"ab{ch}cd\n")
    s = app.snap()
    assert s.text(row_y(s, 1), s.text_x, s.text_x + 5) == "ab\ufffdcd"
    assert ch.encode() not in app.output
    s = burst(app, "right", "right", "right")  # over a, b and the stand-in
    assert line_col(s) == (1, 4)


def test_joiners_and_variation_selectors_still_join(ltt, ws):
    # The stand-ins above are not for ZWJ, ZWNJ or VS16: they build emoji and words.
    app = open_text(ltt, ws, "a\u2764\ufe0f \U0001F468\u200d\U0001F469 \u0646\u200c\u0645 z\n")
    out = app.output
    assert all(c.encode() in out for c in ("\ufe0f", "\u200d", "\u200c"))
    assert "\ufffd" not in app.snap().editor_lines[0]


# ---------------------------------------------------------------- long lines


def test_a_long_line_shows_a_dim_marker_where_the_view_cuts_it(ltt, ws):
    app = open_text(ltt, ws, LONG + "\nshort\n")
    s = app.snap()
    y = row_y(s, 1)
    right = s.text_x + 62  # the last cell of the view
    assert s.text(y, s.text_x, right) == LONG[:62]
    assert s.cell(right, y).ch == "›" and s.fg(right, y) == "DarkGray"
    assert s.editor_row(2).text == "short"


def test_a_line_that_just_fits_has_no_marker(ltt, ws):
    app = open_text(ltt, ws, LONG[:63] + "\n" + LONG[:64] + "\n")
    s = app.snap()
    assert s.editor_row(1).text == LONG[:63]
    assert s.editor_row(2).text == LONG[:62] + "›"


def test_end_on_a_long_line_scrolls_the_view_sideways(ltt, ws):
    app = open_text(ltt, ws, LONG + "\nshort\n\n")
    s = app.press("end")
    y = row_y(s, 1)
    assert line_col(s) == (1, 201)
    assert s.cursor == (s.text_x + 61, y) and not s.cursor_hidden
    assert s.cell(s.text_x, y).ch == "‹" and s.fg(s.text_x, y) == "DarkGray"
    assert s.text(y, s.text_x + 1, s.text_x + 61) == LONG[140:]
    assert s.editor_row(1).text == "‹" + LONG[140:]  # the end is in view: no ›
    assert s.editor_row(2).text == "‹"  # a line hidden entirely to the left shows only the marker
    assert s.editor_row(3).text == ""  # an empty line shows nothing


def test_home_brings_a_scrolled_view_back_to_the_line_start(ltt, ws):
    app = open_text(ltt, ws, LONG + "\n")
    app.press("end")
    s = app.press("home")
    assert s.cursor == (s.text_x, row_y(s, 1))
    assert s.editor_row(1).text == LONG[:62] + "›"


def test_moving_right_past_the_edge_scrolls_one_column(ltt, ws):
    app = open_text(ltt, ws, LONG + "\n")
    s = burst(app, *["right"] * 61)
    y = row_y(s, 1)
    assert s.cursor == (s.text_x + 61, y) and s.cell(s.text_x, y).ch == "a"
    s = app.press("right")
    assert s.cursor == (s.text_x + 61, y)  # the cursor keeps off the › cell
    assert s.editor_row(1).text == "‹" + LONG[2:63] + "›"


def test_moving_left_past_the_edge_shows_the_text_before_the_cursor(ltt, ws):
    app = open_text(ltt, ws, LONG + "\n")
    s = burst(app, "end", *["left"] * 60)
    y = row_y(s, 1)
    assert line_col(s) == (1, 141) and s.cursor == (s.text_x + 1, y)  # just right of the ‹ cell
    s = app.press("left")
    assert line_col(s) == (1, 140) and s.cursor == (s.text_x + 61, y)
    assert s.editor_row(1).text == "‹" + LONG[79:140] + "›"


def test_a_click_on_the_left_marker_keeps_the_cursor_in_view(ltt, ws):
    # V17: a click on the ‹ cell put the cursor outside the view
    app = open_text(ltt, ws, LONG + "\n")
    s = app.press("end")
    y = row_y(s, 1)
    s = app.click(s.text_x, y)
    assert line_col(s) == (1, 141)
    assert s.cursor == (s.text_x + 1, y) and not s.cursor_hidden
    assert s.editor_row(1).text == "‹" + LONG[140:]  # the view did not move


def test_a_click_on_the_right_marker_keeps_the_cursor_off_it(ltt, ws):
    app = open_text(ltt, ws, LONG + "\n")
    s = app.snap()
    y = row_y(s, 1)
    s = app.click(s.text_x + 62, y)  # the › cell
    col = line_col(s)[1]
    assert s.cursor[1] == y and s.cursor[0] < s.text_x + 62  # off the marker
    assert s.cell(*s.cursor).ch == LONG[col - 1]  # the character at the cursor is in view


def test_tabs_keep_their_stops_on_a_line_scrolled_sideways(ltt, ws):
    app = open_text(ltt, ws, "x" * 70 + "\tyz\n")  # the tab runs from column 70 to 72
    s = app.press("end")
    y = row_y(s, 1)
    assert line_col(s) == (1, 75)
    assert s.editor_row(1).text == "‹" + "x" * 56 + "  yz"  # scrolled by 13 columns
    assert s.cursor == (s.text_x + 61, y)


def test_typing_past_the_right_edge_scrolls_the_view(ltt, ws):
    app = open_text(ltt, ws, "\n")
    app.type("x" * 70)
    s = app.wait(lambda s: line_col(s) == (1, 71), what="70 characters typed")  # more than one frame's worth
    y = row_y(s, 1)
    assert s.cursor == (s.text_x + 61, y)
    assert s.editor_row(1).text == "‹" + "x" * 60


def test_markers_on_wide_text_sit_in_the_edge_cells(ltt, ws):
    # U1: the markers were placed by UTF-16 index, not by screen cell
    text = "日本語" * 40
    app = open_text(ltt, ws, text + "\n")
    s = app.snap()
    y = row_y(s, 1)
    assert s.editor_row(1).text == text[:31] + "›"  # 31 wide characters fill 62 of 63 cells
    assert s.cell(s.text_x + 62, y).ch == "›"
    s = app.press("end")
    assert line_col(s) == (1, 241)
    assert s.cursor == (s.text_x + 61, y)
    assert s.cell(s.text_x, y).ch == "‹"  # over the right half of a character cut by the edge
    assert s.editor_row(1).text == "‹" + text[90:]


def test_a_narrower_terminal_keeps_the_cursor_in_view(ltt, ws):
    app = open_text(ltt, ws, LONG + "\n")
    burst(app, *["right"] * 60)
    s = app.resize(80, 24)  # a 50-cell text area
    s = app.wait(lambda s: s.cursor_hidden is False and line_col(s) == (1, 61))
    y = row_y(s, 1)
    assert s.cursor == (s.text_x + 48, y)
    assert s.editor_row(1).text == "‹" + LONG[13:61] + "›"


def test_a_shorter_terminal_keeps_the_cursor_line_in_view(ltt, ws):
    app = open_text(ltt, ws, "".join(f"line {i}\n" for i in range(1, 61)))
    s = burst(app, *["down"] * 25)
    assert line_col(s) == (26, 1) and s.editor_rows[-1].number == 26  # the bottom row
    s = app.resize(100, 16)  # 12 rows of text
    s = app.wait(lambda s: s.editor_rows and s.editor_rows[-1].number == 26)
    assert s.cursor == (s.text_x, row_y(s, 26)) and not s.cursor_hidden


# ---------------------------------------------------------------- the gutter and the current line


def test_line_numbers_are_right_aligned_in_the_gutter(ltt, ws):
    app = open_text(ltt, ws, "".join(f"{i}\n" for i in range(1, 10)) + "x")  # 10 lines
    s = app.snap()
    assert s.gutter == 4 and s.text_x == s.editor_x + 4
    assert s.text(row_y(s, 9), s.editor_x, s.editor_x + 4) == "  9 "
    assert s.text(row_y(s, 10), s.editor_x, s.editor_x + 4) == " 10 "


@pytest.mark.parametrize("lines, gutter", [(9, 4), (99, 4), (999, 5), (9999, 6)])
def test_the_gutter_grows_with_the_line_count(ltt, ws, lines, gutter):
    # at least 4 cells: up to 999 lines; then one more cell per digit
    app = open_text(ltt, ws, "x\n" * (lines - 1) + "x")
    assert app.snap().gutter == max(4, len(str(lines)) + 1)
    s = app.press("ctrl+end", "enter")
    assert s.pos[1] == lines + 1
    assert s.gutter == gutter and s.text_x == s.editor_x + gutter
    assert s.text(row_y(s, lines + 1), s.editor_x, s.editor_x + gutter - 1) == str(lines + 1).rjust(gutter - 1)
    assert s.editor_row(lines).text == "x"
    s = app.press("backspace")
    assert s.gutter == max(4, len(str(lines)) + 1)


def test_the_current_line_number_is_yellow_only_while_the_editor_has_focus(ltt, ws):
    app = open_text(ltt, ws, "a\nb\nc\n")
    s = app.press("down")
    assert [r.current for r in s.editor_rows] == [False, True, False, False]
    assert s.fg(s.editor_x + 2, row_y(s, 2)) == "Yellow"
    assert s.fg(s.editor_x + 2, row_y(s, 1)) == "DarkGray"
    s = app.press("esc")
    assert s.mode == "TREE" and not any(r.current for r in s.editor_rows)
    s = app.press("tab")
    assert s.mode == "EDITOR" and s.editor_row(2).current


def test_rows_past_the_end_of_the_file_show_a_tilde(ltt, ws):
    app = open_text(ltt, ws, "a\nb")
    s = app.snap()
    assert [r.number for r in s.editor_rows] == [1, 2]
    for y in range(row_y(s, 2) + 1, s.content_bottom):
        assert s.text(y, s.editor_x).rstrip() == " ~"
        assert s.fg(s.editor_x + 1, y) == "DarkGray"


def test_an_empty_file_shows_one_empty_line(ltt, ws):
    app = open_text(ltt, ws, "")
    s = app.snap()
    assert s.pos == (1, 1, 1, 0)
    assert [(r.number, r.text, r.current) for r in s.editor_rows] == [(1, "", True)]
    assert s.cursor == (s.text_x, 2) and not s.cursor_hidden
    assert s.text(3, s.editor_x).rstrip() == " ~"


# ---------------------------------------------------------------- the cursor and the status line


def test_the_cursor_is_shown_only_while_the_editor_has_focus(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    s = app.press("right")
    assert not s.cursor_hidden and s.cursor == (s.text_x + 1, 2)
    s = app.press("esc")
    assert s.mode == "TREE" and s.cursor_hidden
    s = app.press("tab")
    assert s.mode == "EDITOR" and not s.cursor_hidden and s.cursor == (s.text_x + 1, 2)


def test_the_cursor_hides_while_a_question_is_asked(ltt, ws):
    app = open_text(ltt, ws, "abc\n")
    app.type("x")
    s = app.press("ctrl+w")
    assert s.mode == "CONFIRM" and s.cursor_hidden
    s = app.press("esc")
    assert s.mode == "EDITOR" and not s.cursor_hidden and s.cursor == (s.text_x + 1, 2)


def test_the_status_line_shows_the_line_count_and_the_column(ltt, ws):
    app = open_text(ltt, ws, "abc\ndef\nghi\n")
    s = app.press("down", "right", "right")
    assert s.status_line.endswith(" Ln 2/4, Col 3 ")
    s = app.press("enter")
    assert s.status_line.endswith(" Ln 3/5, Col 1 ")


# ---------------------------------------------------------------- selection colours


def test_selected_text_is_white_on_dark_blue(ltt, ws):
    app = open_text(ltt, ws, "abcdef\n")
    s = burst(app, "right", "shift+right", "shift+right")
    y = row_y(s, 1)
    cells = [s.cell(s.text_x + i, y) for i in range(4)]
    assert [(c.ch, c.fg, c.bg) for c in cells] == [
        ("a", "Gray", "Black"), ("b", "White", "DarkBlue"), ("c", "White", "DarkBlue"), ("d", "Gray", "Black")]


def test_a_selected_line_break_shows_as_one_cell(ltt, ws):
    app = open_text(ltt, ws, "abcd\nefgh\n")
    s = burst(app, "right", "right", "shift+down")
    x = s.text_x
    assert [s.bg(x + i, row_y(s, 1)) for i in range(6)] == ["Black", "Black"] + ["DarkBlue"] * 3 + ["Black"]
    assert [s.bg(x + i, row_y(s, 2)) for i in range(3)] == ["DarkBlue", "DarkBlue", "Black"]


def test_a_selected_line_break_of_a_line_scrolled_out_of_view_shows_in_its_marker(ltt, ws):
    app = open_text(ltt, ws, "ab\n" + LONG + "\n")
    s = burst(app, "end", "shift+down", "shift+end")  # from the end of "ab" to the end of the long line
    y = row_y(s, 1)
    assert s.editor_row(1).text == "‹"
    assert (s.fg(s.text_x, y), s.bg(s.text_x, y)) == ("White", "DarkBlue")
    assert s.bg(s.text_x + 1, y) == "Black"


# ---------------------------------------------------------------- a tab hidden while the layout changed


def test_a_tab_hidden_while_the_terminal_shrank_shows_its_cursor_line(ltt, ws):
    # U2: a tab that was not active kept its old view height and could hide the cursor line
    ws.write("a.txt", "".join(f"a{i}\n" for i in range(1, 61)))
    ws.write("b.txt", "b\n")
    app = ltt(ws.path("a.txt"), ws.path("b.txt"))
    app.wait_mode("EDITOR")
    s = burst(app, *["down"] * 24)
    assert line_col(s) == (25, 1) and s.editor_rows[0].number == 1
    s = app.press("ctrl+pgdn")
    assert s.active_tab.name == "b.txt"
    app.resize(100, 16)  # 12 rows of text
    s = app.press("ctrl+pgup")
    assert s.active_tab.name == "a.txt" and line_col(s) == (25, 1)
    row = s.editor_row(25)
    assert row is not None and row.current
    assert s.cursor == (s.text_x, row.y) and not s.cursor_hidden


# ---------------------------------------------------------------- the cursor at the markers, counts of characters


@pytest.mark.xfail(strict=True, reason="BUG: a wide character under the cursor in the last cell before the › "
                                       "marker does not fit and is drawn blank: the cursor sits on an empty cell")
@pytest.mark.parametrize("how", ["left_from_the_end", "right_from_the_start"])
def test_the_wide_character_under_the_cursor_at_the_right_edge_is_shown(ltt, ws, how):  # Ox's kaolinite widths
    if how == "left_from_the_end":
        app = open_text(ltt, ws, "日本語" * 40 + "\n")
        app.press("end")
        s = burst(app, *["left"] * 31)
    else:
        app = open_text(ltt, ws, "x" + "日本語" * 40 + "\n")
        s = burst(app, *["right"] * 31)
    x, y = s.cursor
    assert s.cell(x, y).ch in ("日", "本", "語") and s.cell(x + 1, y).ch != "›"


@pytest.mark.xfail(strict=True, reason="BUG: after a sideways wheel the cursor is drawn on the ‹ or › marker "
                                       "that covers its column")
@pytest.mark.parametrize("side", ["left", "right"])
def test_a_cursor_under_a_marker_after_a_sideways_wheel_is_hidden(ltt, ws, side):  # Ox's document_scrolling
    app = open_text(ltt, ws, LONG + "\n")
    if side == "left":
        s = burst(app, "right", "right", "right")
        wheel, marker = K.WHEEL_RIGHT, s.text_x  # three columns a notch: column 4 goes under the ‹
    else:
        s = burst(app, *["right"] * 62)  # the view is one column in; the wheel takes it back to 0
        wheel, marker = K.WHEEL_LEFT, s.text_x + 62
    y = s.cursor[1]
    app.write(K.mouse(wheel, s.text_x + 5, y))
    s = app.settle()
    assert s.cell(marker, y).ch in "‹›" and s.cursor_hidden


@pytest.mark.xfail(strict=True, reason="BUG: counts of characters are UTF-16 units: one emoji counts 2 in "
                                       "'selected:', in the copy, paste and select-all messages")
def test_an_emoji_counts_as_one_character(ltt, ws):  # Ox's kaolinite char_mapping
    app = open_text(ltt, ws, "a\U0001F440b\n")
    app.press("right")
    s = app.press("shift+right")
    assert s.pos[3] == 1
    app.press("ctrl+c")
    app.wait_status("Copied to the clipboard: 1 chars")
    s = app.press("ctrl+a")
    assert s.status == "Selected all: 4 chars."
    app.press("end")
    s = app.paste("\U0001F440e\u0301")
    assert s.status == "Pasted: 2 chars"
