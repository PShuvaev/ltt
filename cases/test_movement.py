"""Cursor movement: arrows over whole characters, Home/End, the document ends, pages, the remembered
column, word and paragraph jumps, and the view following the cursor."""
import pytest

from editor_helpers import CLUSTERS, burst, line_col, mark_cursor, open_text

LINES_100 = "".join(f"line {i}\n" for i in range(1, 101))  # 101 lines; 26 rows of text at 100x30

# ---------------------------------------------------------------- arrows


def test_right_at_a_line_end_goes_to_the_next_line_start(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\n")
    s = burst(app, "right", "right")
    assert line_col(s) == (1, 3)
    s = app.press("right")
    assert line_col(s) == (2, 1)


def test_left_at_a_line_start_goes_to_the_previous_line_end(ltt, ws):
    app = open_text(ltt, ws, "abc\nd\n")
    app.press("down")
    s = app.press("left")
    assert line_col(s) == (1, 4)


def test_arrows_stop_at_the_document_edges(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd")
    s = app.press("left")
    assert line_col(s) == (1, 1)
    s = app.press("up")
    assert line_col(s) == (1, 1)
    s = app.press("ctrl+end", "right")
    assert line_col(s) == (2, 3)
    s = app.press("down")
    assert line_col(s) == (2, 3)


@pytest.mark.parametrize("cluster", list(CLUSTERS.values()), ids=list(CLUSTERS))
def test_right_steps_over_a_whole_character(ltt, ws, cluster):
    # D5, V8: arrows split surrogate pairs and flags
    app = open_text(ltt, ws, f"a{cluster}b\n")
    app.press("right", "right")
    assert mark_cursor(app, ws) == f"a{cluster}|b\n"


@pytest.mark.parametrize("cluster", list(CLUSTERS.values()), ids=list(CLUSTERS))
def test_left_steps_over_a_whole_character(ltt, ws, cluster):
    app = open_text(ltt, ws, f"a{cluster}b\n")
    app.press("end", "left", "left")
    assert mark_cursor(app, ws) == f"a|{cluster}b\n"


def test_arrows_step_over_whole_flags_in_a_long_run(ltt, ws):
    # V8: a run of flags longer than the boundary scan's look-back
    flags = ["🇺🇸", "🇩🇪", "🇫🇷", "🇯🇵"] * 10
    app = open_text(ltt, ws, "".join(flags) + "\n")
    s = burst(app, *["right"] * 25, "left", "left")
    assert line_col(s) == (1, 47)
    assert mark_cursor(app, ws) == "".join(flags[:23]) + "|" + "".join(flags[23:]) + "\n"


# ---------------------------------------------------------------- up and down keep the column


def test_up_and_down_keep_the_column_across_a_short_line(ltt, ws):
    app = open_text(ltt, ws, "a long line\nab\nanother line\n")
    burst(app, *["right"] * 8)
    s = app.press("down")
    assert line_col(s) == (2, 3)  # the end of the short line
    s = app.press("down")
    assert line_col(s) == (3, 9)  # back at the remembered column
    s = burst(app, "up", "up")
    assert line_col(s) == (1, 9)


def test_the_remembered_column_counts_cells_across_tabs(ltt, ws):
    app = open_text(ltt, ws, "abcdefgh\n\tx\nabcdefgh\n")
    burst(app, "right", "right")
    s = app.press("down")
    assert line_col(s) == (2, 1)  # cell 2 is inside the tab: the cursor stays before it
    s = app.press("down")
    assert line_col(s) == (3, 3)
    burst(app, "right", "right")
    s = app.press("up")
    assert line_col(s) == (2, 5)  # cell 4 is the x after the tab
    assert mark_cursor(app, ws) == "abcdefgh\n\t|x\nabcdefgh\n"


def test_the_remembered_column_counts_cells_of_wide_characters(ltt, ws):
    app = open_text(ltt, ws, "abcdefgh\n日本語\nabcdefgh\n")
    burst(app, "right", "right", "right")
    s = app.press("down")
    assert line_col(s) == (2, 3)  # cell 3 is the right half of 本: the cursor stays before it
    s = app.press("down")
    assert line_col(s) == (3, 4)
    app.press("right")
    s = app.press("up")
    assert line_col(s) == (2, 5)
    assert mark_cursor(app, ws) == "abcdefgh\n日本|語\nabcdefgh\n"


def test_up_and_down_never_land_inside_an_emoji(ltt, ws):
    app = open_text(ltt, ws, "abcd\n👀👀\n")
    burst(app, "right", "right", "right")
    s = app.press("down")
    assert line_col(s) == (2, 3)
    assert mark_cursor(app, ws) == "abcd\n👀|👀\n"


def test_a_sideways_move_replaces_the_remembered_column(ltt, ws):
    app = open_text(ltt, ws, "abcdefgh\nab\nabcdefgh\n")
    burst(app, *["right"] * 6)
    app.press("down")
    s = app.press("left")
    assert line_col(s) == (2, 2)
    s = app.press("down")
    assert line_col(s) == (3, 2)


# ---------------------------------------------------------------- Home, End, document ends


def test_home_goes_to_the_first_non_blank_then_to_the_line_start(ltt, ws):
    app = open_text(ltt, ws, "    foo bar\n")
    app.press("end")
    s = app.press("home")
    assert line_col(s) == (1, 5)
    s = app.press("home")
    assert line_col(s) == (1, 1)
    s = app.press("home")
    assert line_col(s) == (1, 5)


def test_home_inside_tab_indentation_goes_to_the_first_non_blank(ltt, ws):
    app = open_text(ltt, ws, "\t\tx = 1\n")
    s = app.press("right", "home")
    assert line_col(s) == (1, 9)


def test_home_on_a_line_without_indentation_goes_to_its_start(ltt, ws):
    app = open_text(ltt, ws, "foo\n")
    s = app.press("end", "home")
    assert line_col(s) == (1, 1)
    s = app.press("home")
    assert line_col(s) == (1, 1)


def test_end_goes_to_the_line_end(ltt, ws):
    app = open_text(ltt, ws, "abc\n\tdef\n")
    s = app.press("end")
    assert line_col(s) == (1, 4)
    s = app.press("down", "end")
    assert line_col(s) == (2, 8)


def test_ctrl_end_and_ctrl_home_go_to_the_document_ends(ltt, ws):
    app = open_text(ltt, ws, LINES_100 + "last")
    s = app.press("ctrl+end")
    assert s.pos[:3] == (101, 101, 5)
    assert [s.editor_rows[0].number, s.editor_rows[-1].number] == [76, 101]  # the last line at the bottom
    s = app.press("ctrl+home")
    assert line_col(s) == (1, 1) and s.editor_rows[0].number == 1


def test_cmd_arrows_go_to_the_line_and_document_ends(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\nef\n")
    app.press("down", "right")
    s = app.press("cmd+right")
    assert line_col(s) == (2, 3)
    s = app.press("cmd+left")
    assert line_col(s) == (2, 1)
    s = app.press("cmd+down")
    assert line_col(s) == (4, 1)
    s = app.press("cmd+up")
    assert line_col(s) == (1, 1)


# ---------------------------------------------------------------- pages


def test_page_down_moves_the_cursor_a_screen_down(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    s = app.press("pgdn")
    assert line_col(s) == (27, 1)
    assert [s.editor_rows[0].number, s.editor_rows[-1].number] == [2, 27]
    s = app.press("pgdn")
    assert line_col(s) == (53, 1)


def test_page_up_moves_the_cursor_a_screen_up(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    app.press("ctrl+end")
    s = app.press("pgup")
    assert line_col(s) == (75, 1)
    assert s.editor_rows[0].number == 75


def test_page_keys_stop_at_the_document_ends(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    burst(app, "down", "down")
    s = app.press("pgup")
    assert line_col(s) == (1, 1)
    burst(app, "pgdn", "pgdn", "pgdn")
    s = app.press("pgdn")
    assert line_col(s) == (101, 1)


def test_page_keys_keep_the_column(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    burst(app, *["right"] * 6)
    s = app.press("pgdn")
    assert line_col(s) == (27, 7)
    s = app.press("pgup")
    assert line_col(s) == (1, 7)


# ---------------------------------------------------------------- word jumps


def test_alt_arrows_jump_by_words(ltt, ws):
    app = open_text(ltt, ws, "foo bar  baz\n")
    stops = [line_col(app.press("alt+right"))[1] for _ in range(3)]
    assert stops == [4, 8, 13]  # word ends
    stops = [line_col(app.press("alt+left"))[1] for _ in range(3)]
    assert stops == [10, 5, 1]  # word starts


@pytest.mark.parametrize("left, right", [
    ("ctrl+left", "ctrl+right"),
    ("alt+b", "alt+f"),                   # Esc b / Esc f: Option+arrows in meta mode
    (b"\x1b\x1b[D", b"\x1b\x1b[C"),       # Esc + the arrow sequence: Option+arrows in other terminals
], ids=["ctrl-arrows", "esc-b-f", "esc-esc-arrows"])
def test_other_word_jump_keys(ltt, ws, left, right):
    app = open_text(ltt, ws, "foo bar baz\n")
    s = app.press(right, right)
    assert line_col(s) == (1, 8)
    s = app.press(left)
    assert line_col(s) == (1, 5)


def test_word_jumps_stop_at_each_run_of_punctuation(ltt, ws):
    app = open_text(ltt, ws, "foo.bar(baz);\n")
    stops = [line_col(app.press("alt+right"))[1] for _ in range(6)]
    assert stops == [4, 5, 8, 9, 12, 14]


def test_word_jumps_cross_line_ends(ltt, ws):
    app = open_text(ltt, ws, "ab\ncd\n")
    app.press("end")
    s = app.press("alt+right")
    assert line_col(s) == (2, 1)
    s = app.press("alt+left")
    assert line_col(s) == (1, 3)


def test_word_right_passes_combining_accents(ltt, ws):
    # V3: word jumps stalled on decomposed (NFD) accents
    app = open_text(ltt, ws, "un cafe\u0301 noir\n")
    s = app.press("alt+right", "alt+right")
    assert line_col(s) == (1, 8)
    assert mark_cursor(app, ws) == "un cafe\u0301| noir\n"


def test_word_left_passes_combining_accents(ltt, ws):
    # V3
    app = open_text(ltt, ws, "un cafe\u0301 noir\n")
    app.press("end")
    s = app.press("alt+left", "alt+left")
    assert line_col(s) == (1, 4)
    assert mark_cursor(app, ws) == "un |cafe\u0301 noir\n"


def test_word_jumps_pass_indic_vowel_signs(ltt, ws):
    # V3: vowel signs (spacing and non-spacing) and the virama belong to their word
    app = open_text(ltt, ws, "नमस्ते दुनिया\n")
    app.press("alt+right")
    app.type("|")
    app.press("end", "alt+left")
    assert mark_cursor(app, ws, marker="^") == "नमस्ते| ^दुनिया\n"


def test_word_jumps_take_a_cjk_run_as_one_word(ltt, ws):
    app = open_text(ltt, ws, "日本語 text\n")
    s = app.press("alt+right")
    assert line_col(s) == (1, 7)
    s = app.press("alt+right", "alt+left")
    assert line_col(s) == (1, 8)


def test_word_jumps_never_stop_inside_an_emoji(ltt, ws):
    app = open_text(ltt, ws, "hi 👀👀 yo\n")
    s = app.press("alt+right", "alt+right")
    assert line_col(s) == (1, 8)
    assert mark_cursor(app, ws) == "hi 👀👀| yo\n"


# ---------------------------------------------------------------- paragraph jumps

PARAGRAPHS = "p1a\np1b\n\n   \np2a\np2b\n\np3\n"  # 9 lines; line 4 holds only spaces


@pytest.mark.parametrize("key", ["alt+down", b"\x1b\x1b[B"], ids=["alt-down", "esc-esc-down"])
def test_alt_down_jumps_to_the_next_paragraph_start(ltt, ws, key):
    app = open_text(ltt, ws, PARAGRAPHS)
    stops = [line_col(app.press(key))[0] for _ in range(4)]
    assert stops == [5, 8, 9, 9]  # a line of spaces is blank too; then the last line


def test_alt_up_jumps_to_the_start_of_this_or_the_previous_paragraph(ltt, ws):
    app = open_text(ltt, ws, PARAGRAPHS)
    burst(app, *["down"] * 5)  # p2b
    stops = [line_col(app.press("alt+up"))[0] for _ in range(3)]
    assert stops == [5, 1, 1]


def test_paragraph_jumps_keep_the_column(ltt, ws):
    app = open_text(ltt, ws, "abcdef\n\nxyz\n\nabcdef\n")
    burst(app, *["right"] * 4)
    s = app.press("alt+down")
    assert line_col(s) == (3, 4)  # the end of the shorter line
    s = app.press("alt+down")
    assert line_col(s) == (5, 5)


# ---------------------------------------------------------------- the view follows the cursor


def test_the_view_scrolls_one_line_when_the_cursor_passes_the_bottom(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    s = burst(app, *["down"] * 25)
    assert line_col(s) == (26, 1) and s.editor_rows[0].number == 1
    s = app.press("down")
    assert line_col(s) == (27, 1) and s.editor_rows[0].number == 2
    assert s.cursor == (s.text_x, s.editor_row(27).y)


def test_the_view_scrolls_one_line_when_the_cursor_passes_the_top(ltt, ws):
    app = open_text(ltt, ws, LINES_100)
    app.press("pgdn", "pgdn")
    s = burst(app, *["up"] * 25)
    assert line_col(s) == (28, 1) and s.editor_rows[0].number == 28
    s = app.press("up")
    assert line_col(s) == (27, 1) and s.editor_rows[0].number == 27
    assert s.cursor == (s.text_x, s.editor_row(27).y)
