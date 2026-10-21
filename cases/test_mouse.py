"""The mouse in the editor and elsewhere: clicks, drags, double-clicks and the wheel in the editor,
the wheel over the tree, tabs and results, click-to-copy on the title, buttons that mean nothing.
(Clicks in the tree and on the tab bar are tested with the tree and the tabs.)"""
import base64

import pytest

from harness import keys as K
from keys_helpers import edit, select

LONG = "".join(chr(ord("a") + i % 26) for i in range(150))  # wider than the editor


def at(s, line, col=0):
    """Screen cell of text column `col` (0-based, display cells) on file line `line` (1-based)."""
    return s.text_x + col, s.editor_row(line).y


def lines_file(n):
    return "".join(f"line {i}\n" for i in range(1, n + 1))


def first_shown(s):
    return s.editor_rows[0].number


def osc52_count(app, mark):
    return app.output_since(mark).count(b"\x1b]52;")


def flush(app):
    """An event that does nothing but makes ltt draw a frame (a click on the help line)."""
    return app.click(5, app.snap().help_y)


def test_mouse_reports_are_on_while_running_and_off_after_quit(ltt, ws):
    app = edit(ltt, ws)
    out = app.output
    assert all(m in out for m in (b"\x1b[?1000h", b"\x1b[?1002h", b"\x1b[?1006h"))  # buttons, drags, SGR
    assert b"\x1b[?1003h" not in out  # never every bare mouse move
    assert app.quit() == 0
    tail = app.output[app.output.rfind(b"\x1b[?2026l"):]
    assert all(m in tail for m in (b"\x1b[?1006l", b"\x1b[?1002l", b"\x1b[?1000l"))


# ---------------------------------------------------------------- clicks in the editor

def test_click_places_the_cursor(ltt, ws):
    app = edit(ltt, ws, "one two\nthree four\n")
    s = app.click(*at(app.snap(), 2, 6))
    assert s.pos[:3] == (2, 3, 7) and s.cursor == at(s, 2, 6)


def test_x10_click_places_the_cursor_like_an_sgr_one(ltt, ws):
    # terminals without SGR reports (mode 1006) send ESC [ M and three bytes: button, x, y
    app = edit(ltt, ws, "one two\nthree four\n")
    x, y = at(app.snap(), 2, 6)
    s = app.press(b"\x1b[M" + bytes([32, 33 + x, 33 + y]) + b"\x1b[M" + bytes([32 + 3, 33 + x, 33 + y]))
    assert s.pos[:3] == (2, 3, 7) and s.editor_lines[:2] == ["one two", "three four"]


def test_click_after_wide_characters_lands_on_the_character_there(ltt, ws):  # U1
    app = edit(ltt, ws, "日本語abc\n")
    app.click(*at(app.snap(), 1, 6))  # 日本語 take six cells: the seventh is the a
    app.type("X")
    s = app.snap()
    assert s.editor_lines[0] == "日本語Xabc" and s.pos[:3] == (1, 2, 8)


def test_click_on_the_right_half_of_a_wide_character_lands_before_it(ltt, ws):
    app = edit(ltt, ws, "日本語abc\n")
    s = app.click(*at(app.snap(), 1, 3))
    assert s.pos[:3] == (1, 2, 3)
    app.type("X")
    assert app.snap().editor_lines[0] == "日X本語abc"


def test_click_inside_a_tab_lands_before_it(ltt, ws):
    app = edit(ltt, ws, "x\tyz\n")
    app.click(*at(app.snap(), 1, 2))  # the tab fills cells 1-3
    app.type("A")
    s = app.snap()
    assert s.pos[:3] == (1, 2, 3)
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "xA\tyz\n"


def test_click_after_a_tab_lands_after_it(ltt, ws):
    app = edit(ltt, ws, "x\tyz\n")
    s = app.click(*at(app.snap(), 1, 4))
    assert s.pos[:3] == (1, 2, 5)
    app.type("A")
    assert app.snap().editor_lines[0].endswith("Ayz")


def test_click_past_the_end_of_a_line_goes_to_its_end(ltt, ws):
    app = edit(ltt, ws, "a long line\nshort\n")
    s = app.click(*at(app.snap(), 2, 40))
    assert s.pos[:3] == (2, 3, 6)


def test_click_below_the_last_line_goes_to_the_last_line(ltt, ws):
    app = edit(ltt, ws, "one\nlast")
    s = app.snap()
    s = app.click(s.text_x + 2, s.editor_row(2).y + 5)
    assert s.pos[:3] == (2, 2, 3)


def test_click_on_a_line_number_goes_to_the_line_start(ltt, ws):
    app = edit(ltt, ws, "one\n  two\n")
    s = app.snap()
    s = app.click(s.editor_x + 1, s.editor_row(2).y)
    assert s.pos[:3] == (2, 3, 1)


def test_click_in_the_editor_takes_the_focus_from_the_tree(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("esc")
    s = app.click(*at(app.snap(), 1, 4))
    assert s.mode == "EDITOR" and s.pos[:3] == (1, 2, 5)


def test_click_in_the_editor_without_a_file_does_nothing(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj"))
    before = app.snap()
    s = app.click(before.editor_x + 10, 5)
    assert s.mode == "TREE" and s.tabs == [] and s.status == before.status


def test_click_on_the_divider_does_nothing(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("end")
    s = app.click(app.snap().left_w, 2)
    assert s.mode == "EDITOR" and s.pos[:3] == (1, 2, 8)


def test_click_clears_a_selection(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("ctrl+a")
    s = app.click(*at(app.snap(), 1, 2))
    assert s.pos == (1, 2, 3, 0)


def test_click_after_wheel_scrolling_hits_the_line_shown(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    s = app.wheel(*at(app.snap(), 3), notches=5)
    top = first_shown(s)
    assert top > 1
    s = app.click(s.text_x + 2, s.editor_rows[0].y)
    assert s.pos[:3] == (top, 101, 3) and first_shown(s) == top


def test_click_after_scrolling_sideways_hits_the_character_shown(ltt, ws):
    app = edit(ltt, ws, LONG + "\n")
    s = app.wheel(*at(app.snap(), 1, 5), notches=2, mods=K.MOUSE_SHIFT)
    x, y = s.text_x + 5, s.editor_row(1).y
    shown = s.cell(x, y).ch
    s = app.click(x, y)
    col = s.pos[2]
    assert LONG[col - 1] == shown and col > 6
    assert s.editor_row(1).text.startswith("‹")  # the view stayed where it was


def test_click_on_the_left_edge_marker_keeps_the_cursor_in_view(ltt, ws):  # V17
    app = edit(ltt, ws, LONG + "\n")
    s = app.wheel(*at(app.snap(), 1, 5), notches=2, mods=K.MOUSE_SHIFT)
    view = s.editor_row(1).text
    assert view.startswith("‹")
    s = app.click(s.text_x, s.editor_row(1).y)
    assert s.editor_row(1).text == view  # no jump
    assert s.cursor == (s.text_x + 1, s.editor_row(1).y) and LONG[s.pos[2] - 1] == view[1]


@pytest.mark.xfail(strict=True, reason="BUG: a click on the › cell of a line cut at the right edge puts the cursor "
                                        "on the marker, over a hidden character — the V17 fix covers only the ‹ edge")
def test_click_on_the_right_edge_marker_keeps_the_cursor_off_it(ltt, ws):
    app = edit(ltt, ws, LONG + "\n")
    s = app.snap()
    row = s.editor_row(1)
    x = s.text_x + len(row.text) - 1
    assert s.cell(x, row.y).ch == "›"
    s = app.click(x, row.y)
    assert s.cell(*s.cursor).ch != "›"


# ---------------------------------------------------------------- drag selection

def test_drag_selects_forward(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.snap()
    s = app.drag(*at(s, 1, 4), *at(s, 1, 7))
    assert s.status == "Selected: 3 chars. Ctrl+C copies, Ctrl+X cuts." and s.pos == (1, 2, 8, 3)
    app.press("ctrl+c")
    app.wait(lambda s: ws.clipboard == "two", timeout=5, what="the selection on the clipboard")


def test_drag_selects_backward(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.snap()
    s = app.drag(*at(s, 1, 7), *at(s, 1, 4))
    assert s.pos == (1, 2, 5, 3)
    app.press("ctrl+c")
    app.wait(lambda s: ws.clipboard == "two", timeout=5, what="the selection on the clipboard")


def test_drag_across_lines(ltt, ws):
    app = edit(ltt, ws, "one two\nthree four\n")
    s = app.snap()
    s = app.drag(*at(s, 1, 4), *at(s, 2, 5))
    assert s.pos == (2, 3, 6, 9)
    app.press("ctrl+x")
    s = app.wait_status("Cut to the clipboard.")
    assert s.editor_lines[:2] == ["one  four", ""]
    app.wait(lambda s: ws.clipboard == "two\nthree", timeout=5, what="the selection on the clipboard")


def test_drag_upward_across_lines(ltt, ws):
    app = edit(ltt, ws, "one two\nthree four\n")
    s = app.snap()
    s = app.drag(*at(s, 2, 5), *at(s, 1, 4))
    assert s.pos == (1, 3, 5, 9)


def test_typing_replaces_a_dragged_selection(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.snap()
    app.drag(*at(s, 1, 4), *at(s, 1, 7))
    app.type("2")
    assert app.snap().editor_lines[0] == "one 2 three"


def test_press_and_release_in_place_selects_nothing(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.click(*at(app.snap(), 1, 4))
    assert s.pos == (1, 2, 5, 0) and not s.status.startswith("Selected")


def test_drag_past_the_bottom_edge_stops_at_the_last_line_shown(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    s = app.snap()
    last = s.editor_rows[-1].number
    s = app.drag(*at(s, 1), s.text_x + 2, s.status_y)
    assert s.pos[:3] == (last, 101, 3) and first_shown(s) == 1  # no scrolling while dragging


def test_drag_over_the_tab_bar_and_title_stops_at_the_first_line_shown(ltt, ws):  # C3: no OSC 52 either
    app = edit(ltt, ws, lines_file(100))
    s = app.wheel(*at(app.snap(), 3), notches=10)
    top = first_shown(s)
    mark = app.output_mark()
    s = app.drag(*at(s, top + 5, 3), s.text_x + 1, 0)
    assert s.pos[:3] == (top, 101, 2) and first_shown(s) == top
    assert s.pos[3] > 0 and osc52_count(app, mark) == 0


# ---------------------------------------------------------------- double-click

def test_double_click_selects_a_word(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.double_click(*at(app.snap(), 1, 5))
    assert s.pos == (1, 2, 8, 3)
    app.press("ctrl+c")
    app.wait(lambda s: ws.clipboard == "two", timeout=5, what="the word on the clipboard")


def test_double_click_on_punctuation_selects_the_run(ltt, ws):
    app = edit(ltt, ws, "a +=- b\n")
    s = app.double_click(*at(app.snap(), 1, 3))
    assert s.pos == (1, 2, 6, 3)


def test_double_click_on_spaces_selects_the_spaces(ltt, ws):
    app = edit(ltt, ws, "one    two\n")
    s = app.double_click(*at(app.snap(), 1, 5))
    assert s.pos == (1, 2, 8, 4)


def test_double_click_on_an_empty_line_selects_nothing(ltt, ws):
    app = edit(ltt, ws, "one\n\ntwo\n")
    s = app.double_click(*at(app.snap(), 2, 3))
    assert s.pos == (2, 4, 1, 0)


def test_double_click_on_a_non_latin_word(ltt, ws):
    app = edit(ltt, ws, "привет, мир\n")
    s = app.double_click(*at(app.snap(), 1, 9))
    assert s.pos == (1, 2, 12, 3)


def test_two_clicks_on_different_cells_are_not_a_double_click(ltt, ws):
    app = edit(ltt, ws, "one two three\n")
    s = app.snap()
    (x, y) = at(s, 1, 5)
    app.write(K.mouse(K.LEFT, x, y, True) + K.mouse(K.LEFT, x, y, False)
              + K.mouse(K.LEFT, x + 1, y, True) + K.mouse(K.LEFT, x + 1, y, False))
    s = app.settle()
    assert s.pos == (1, 2, 7, 0)


# ---------------------------------------------------------------- the wheel

def test_wheel_scrolls_without_moving_the_cursor(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    s = app.wheel(*at(app.snap(), 3), notches=5)
    assert first_shown(s) == 7  # five notches are six lines
    assert s.pos[:3] == (1, 101, 1)


def test_wheel_up_scrolls_back(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    s = app.wheel(*at(app.snap(), 3), notches=10)
    assert first_shown(s) == 13
    s = app.wheel(*at(s, 15), down=False, notches=5)
    assert first_shown(s) == 7
    s = app.wheel(*at(s, 8), down=False, notches=20)
    assert first_shown(s) == 1


def test_wheel_stops_with_the_last_line_at_the_bottom(ltt, ws):
    app = edit(ltt, ws, lines_file(40))
    s = app.wheel(*at(app.snap(), 3), notches=50)
    assert s.editor_rows[-1].number == 41 and s.editor_rows[-1].y == s.status_y - 1
    s = app.wheel(*at(s, 30), down=False, notches=1)
    assert s.editor_rows[-1].number == 40  # turning back answers at once


def test_typing_after_wheel_scrolling_brings_the_cursor_back(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    app.wheel(*at(app.snap(), 3), notches=20)
    app.type("X")
    s = app.snap()
    assert first_shown(s) == 1 and s.editor_lines[0] == "Xline 1"


def test_wheel_over_the_tree_moves_the_tree_selection(ltt, ws):
    for i in range(30):
        ws.write(f"proj/f{i:02d}.txt", "x" * (100 - i))  # sorted by size: f00 first
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("esc", "home")
    s = app.wheel(5, 10, notches=5)
    assert s.tree_selected.name == "f02.txt"  # 3/5 of a row per notch
    assert s.mode == "TREE" and s.editor_lines[0] == "a"


def test_wheel_over_the_tab_bar_switches_tabs(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/b.txt", "b\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    s = app.snap()
    s = app.wheel(s.editor_x + 20, 1)
    assert s.active_tab.name == "b.txt" and s.editor_lines[0] == "b"
    s = app.wheel(s.editor_x + 20, 1, down=False)
    assert s.active_tab.name == "a.txt"


def test_shift_wheel_scrolls_sideways_without_moving_the_cursor(ltt, ws):
    app = edit(ltt, ws, LONG + "\nshort\n")
    s = app.wheel(*at(app.snap(), 2), notches=2, mods=K.MOUSE_SHIFT)
    text = s.editor_row(1).text  # three columns a notch; the ‹ covers the cut-off column 6
    assert text.startswith("‹" + LONG[7:20]) and text.endswith("›")
    assert s.pos[:3] == (1, 3, 1)


def test_horizontal_wheel_scrolls_sideways(ltt, ws):
    app = edit(ltt, ws, LONG + "\n")
    s = app.snap()
    app.write(K.mouse(K.WHEEL_RIGHT, *at(s, 1, 5)))
    s = app.settle()
    assert s.editor_row(1).text.startswith("‹" + LONG[4:10])
    app.write(K.mouse(K.WHEEL_LEFT, *at(s, 1, 5)))
    s = app.settle()
    assert s.editor_row(1).text.startswith(LONG[:10]) and s.pos[:3] == (1, 2, 1)


def test_sideways_scroll_stops_when_the_longest_line_ends_at_the_edge(ltt, ws):
    app = edit(ltt, ws, LONG + "\n")
    s = app.snap()
    app.write(K.mouse(K.WHEEL_RIGHT, *at(s, 1, 5)) * 80)
    s = app.settle()
    text = s.editor_row(1).text
    assert text.endswith(LONG[-10:]) and not text.endswith("›")
    assert len(text) == s.cols - s.text_x  # the end of the line sits at the right edge


def test_horizontal_wheel_over_the_tree_does_nothing(ltt, ws):
    app = edit(ltt, ws, LONG + "\n")
    before = app.snap()
    app.write(K.mouse(K.WHEEL_RIGHT, 5, 3))
    s = app.settle()
    assert s.editor_row(1).text == before.editor_row(1).text and s.tree_selected == before.tree_selected


def test_wheel_is_ignored_while_a_prompt_is_open(ltt, ws):
    app = edit(ltt, ws, lines_file(100))
    app.press("ctrl+l")
    s = app.wheel(*at(app.snap(), 3), notches=5)
    assert s.mode == "PROMPT" and first_shown(s) == 1


def test_wheel_over_the_results_while_typing_moves_the_hit(ltt, ws):
    app = edit(ltt, ws, lines_file(30))
    app.press("ctrl+f")
    app.type("line")
    s = app.wait(lambda s: len(s.results) > 3, what="the hits")
    assert ": 1/" in s.results_header
    s = app.wheel(10, s.results[1].y, notches=5)
    assert ": 4/" in s.results_header and s.mode == "PROMPT"  # still typing


def search_al(ltt, ws):
    """A file search for "al" with three hits, the results panel showing them, still typing."""
    app = edit(ltt, ws, "alpha\nbeta\nalpine\ngamma\nalps\n")
    app.press("ctrl+f")
    app.type("al")
    app.wait(lambda s: [h.line for h in s.results] == [1, 3, 5], what="three hits")
    return app


def test_clicks_are_ignored_while_typing_a_search(ltt, ws):
    app = search_al(ltt, ws)
    s = app.double_click(10, app.snap().results[2].y)
    assert s.mode == "PROMPT" and s.results[0].selected and s.pos is None
    s = app.click(*at(app.snap(), 2, 1))
    assert s.mode == "PROMPT"


def test_click_on_the_results_header_focuses_the_results(ltt, ws):
    app = search_al(ltt, ws)
    app.press("enter")  # opens the first hit; the panel stays
    app.wait_mode("EDITOR")
    s = app.click(10, app.snap().results_top)
    assert s.mode == "SEARCH" and s.results[0].selected


def test_click_on_a_hit_selects_it(ltt, ws):
    app = search_al(ltt, ws)
    app.press("esc")  # from the input to the results
    app.wait_mode("SEARCH")
    s = app.click(10, app.snap().results[1].y)
    assert s.mode == "SEARCH" and s.results[1].selected and ": 2/3" in s.results_header
    assert s.title_path.endswith("/proj/a.txt")  # the title shows the hit's file


def test_double_click_on_a_hit_opens_it(ltt, ws):
    app = search_al(ltt, ws)
    app.press("esc")
    app.wait_mode("SEARCH")
    s = app.double_click(10, app.snap().results[2].y)
    assert s.mode == "EDITOR" and s.pos[:3] == (5, 6, 1) and s.results_top is not None


def test_wheel_over_the_results_moves_the_hit(ltt, ws):
    app = search_al(ltt, ws)
    app.press("esc")
    s = app.wait_mode("SEARCH")
    s = app.wheel(10, s.results[0].y, notches=4)
    assert ": 3/3" in s.results_header and s.mode == "SEARCH"
    s = app.wheel(10, s.results[0].y, down=False, notches=10)
    assert ": 1/3" in s.results_header


# ---------------------------------------------------------------- buttons and places that mean nothing

def test_back_and_forward_buttons_do_nothing(ltt, ws):  # T9: "forward" closed a tab, another button removed a root
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/b.txt", "b\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    before = app.snap()
    tab = before.tabs[0]
    for button, (x, y) in [(128, (tab.close_x, 1)), (129, (tab.x0 + 1, 1)), (130, (2, 1)), (131, at(before, 1, 1))]:
        app.write(K.mouse(button, x, y, True) + K.mouse(button, x, y, False))
    s = flush(app)
    assert s.tab_names == before.tab_names and s.active_tab.name == "a.txt"
    assert s.tree_names == before.tree_names and s.pos == before.pos and s.status == before.status


def test_mouse_far_outside_the_screen_does_nothing(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("end")
    before = app.snap()
    s = app.click(500, 300)
    s = app.wheel(500, 300, notches=3)
    s = app.drag(500, 300, 600, 400)
    assert (s.mode, s.pos, s.status, s.editor_lines) == (before.mode, before.pos, before.status, before.editor_lines)


def test_middle_click_in_the_editor_does_nothing(ltt, ws):
    ws.set_clipboard("PASTE")
    app = edit(ltt, ws, "one two\n")
    s = app.click(*at(app.snap(), 1, 4), button=K.MIDDLE)
    assert s.pos == (1, 2, 1, 0) and s.editor_lines[0] == "one two"


def test_right_click_in_the_editor_does_nothing(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    s = app.click(*at(app.snap(), 1, 4), button=K.RIGHT)
    assert s.pos == (1, 2, 1, 0) and s.mode == "EDITOR"


def test_clicks_on_the_status_and_help_lines_do_nothing(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("end")
    before = app.snap()
    for x in (2, before.left_w - 1, before.editor_x + 5, before.cols - 3):
        for y in (before.status_y, before.help_y):
            s = app.click(x, y)
            assert (s.mode, s.pos, s.status, s.tree_selected) == (before.mode, before.pos, before.status,
                                                                   before.tree_selected)


def test_clicks_are_ignored_while_a_prompt_is_open(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.press("ctrl+l")
    s = app.click(*at(app.snap(), 1, 4))
    assert s.mode == "PROMPT"
    app.press("esc")
    assert app.snap().pos[:3] == (1, 2, 1)


def test_clicks_are_ignored_while_a_question_is_open(ltt, ws):
    app = edit(ltt, ws, "one two\n")
    app.type("X")
    app.press("ctrl+w")
    app.wait_mode("CONFIRM")
    s = app.click(*at(app.snap(), 1, 5))
    assert s.mode == "CONFIRM"
    app.press("esc")
    assert app.wait_status("Cancelled.").pos[:3] == (1, 2, 2)


def test_click_in_the_editor_leaves_the_search_results(ltt, ws):
    app = edit(ltt, ws, "alpha\nbeta\nalpine\n")
    app.press("ctrl+f")
    app.type("al")
    app.wait(lambda s: len(s.results) == 2, what="two hits")
    app.press("esc")
    app.wait_mode("SEARCH")
    s = app.click(*at(app.snap(), 2, 2))
    assert s.mode == "EDITOR" and s.pos[:3] == (2, 4, 3) and s.results_top is not None


# ---------------------------------------------------------------- the title: click-to-copy (R3), never a flood (C3)

def test_click_on_the_title_copies_the_open_files_path(ltt, ws):  # R3 kept click-to-copy
    app = edit(ltt, ws)
    path = ws.path("proj/a.txt")
    mark = app.output_mark()
    s = app.click(10, 0)
    assert s.status.startswith("Copied path: ")
    app.wait(lambda s: ws.clipboard == path, timeout=5, what="the path on the clipboard")
    assert b"\x1b]52;c;" + base64.b64encode(path.encode()) in app.output_since(mark)


def test_click_on_a_title_shown_from_home_copies_the_full_path(ltt, ws):
    path = ws.write(ws.home + "/notes/a.txt", "a\n")
    app = ltt(path)
    s = app.wait_mode("EDITOR")
    assert s.title_path == "~/notes/a.txt"
    app.click(8, 0)
    app.wait(lambda s: ws.clipboard == path, timeout=5, what="the full path on the clipboard")


def test_click_on_the_title_in_the_tree_copies_the_selected_path(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/sub/b.txt", "b\n")
    app = ltt(ws.path("proj"))
    select(app, "sub")
    app.click(30, 0)
    app.wait(lambda s: ws.clipboard == ws.path("proj/sub"), timeout=5, what="the folder's path on the clipboard")


def test_dragging_along_the_title_copies_once(ltt, ws):  # C3: every mouse move wrote OSC 52
    app = edit(ltt, ws)
    mark = app.output_mark()
    app.drag(5, 0, 60, 0)
    app.wait(lambda s: ws.clipboard == ws.path("proj/a.txt"), timeout=5, what="the path on the clipboard")
    flush(app)
    assert osc52_count(app, mark) == 1 and len(ws.clipboard_log) == 1
