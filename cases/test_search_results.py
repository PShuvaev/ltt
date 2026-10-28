"""The search results panel: its rows, keys and mouse, opening hits, Ctrl+G, selection seeds,
and the shortcuts that keep working over it."""
import re

import pytest

from search_helpers import locations, search, search_input, wait_found


def needles(n):
    """n lines "needle 01", "needle 02", …"""
    return "".join(f"needle {i:02d}\n" for i in range(1, n + 1))


def picked(s):
    """The number of the picked hit, from the header ("…: 3/30 · …")."""
    return int(re.search(r'": (\d+)/\d+', s.results_header).group(1))


def search_file(ltt, ws, content, n, query="needle", **kw):
    """ltt on proj/a.txt with this content, its search run: returns (app, frame with the results)."""
    ws.write("proj/a.txt", content)
    app = ltt(ws.path("proj/a.txt"), **kw)
    app.press("ctrl+f")
    return app, search(app, query, n)


# ---------------------------------------------------------------- the panel


def test_the_header_shows_scope_query_count_and_keys(ltt, ws):
    app, s = search_file(ltt, ws, "needle one\nneedle two\n", 2)
    assert s.results_header == 'Search results (file a.txt) for "needle": 1/2 · Enter opens · Ctrl+G next · Esc hides'
    assert (s.fg(1, s.results_top), s.bg(1, s.results_top)) == ("White", "DarkMagenta")


def test_rows_show_path_line_and_the_trimmed_line(ltt, ws):
    ws.write("proj/a.txt", "needle at the start\n")
    ws.write("proj/sub/b.txt", "x\n    indented needle here   \n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert [(h.location, h.snippet) for h in s.results] == [
        ("a.txt:1", "needle at the start"), ("sub/b.txt:2", "indented needle here")]


def test_the_picked_row_is_highlighted(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    first, second = s.results
    assert first.selected and not second.selected
    assert (s.fg(1, first.y), s.fg(s.lines[first.y].index("needle"), first.y)) == ("White", "White")
    assert s.bg(0, second.y) == "Black"
    assert (s.fg(1, second.y), s.fg(s.lines[second.y].index("needle"), second.y)) == ("Cyan", "Gray")


def test_a_long_line_is_shown_from_just_before_the_match(ltt, ws):
    line = "        start " + "filler " * 30 + "the needle is far to the right " + "tail " * 10
    app, s = search_file(ltt, ws, line + "\n", 1)
    assert s.results[0].snippet.startswith("… filler the needle is far to the right tail")


def test_a_long_line_with_an_early_match_is_shown_from_its_start(ltt, ws):
    app, s = search_file(ltt, ws, "the needle " + "filler " * 40 + "\n", 1)
    assert s.results[0].snippet.startswith("the needle filler filler")


def test_tabs_inside_a_hit_line_are_shown_as_spaces(ltt, ws):
    app, s = search_file(ltt, ws, "int\tbeta;\t// the beta value\n", 1, query="beta")
    assert re.fullmatch(r"int +beta; +// the beta value", s.results[0].snippet)
    assert b"\t" not in app.output


def test_wide_characters_in_hit_lines(ltt, ws):
    app, s = search_file(ltt, ws, "日本語のテキスト beta 漢字\n" + "漢字" * 60 + " beta\n", 2, query="beta")
    assert [h.snippet for h in s.results] == ["日本語のテキスト beta 漢字", "…字" + "漢字" * 5 + " beta"]


@pytest.mark.parametrize("rows, shown", [(10, 2), (30, 8), (60, 9)])
def test_the_panel_takes_about_a_third_of_the_height(ltt, ws, rows, shown):
    app, s = search_file(ltt, ws, needles(20), 20, rows=rows)
    assert len(s.results) == shown and s.results_top == rows - 2 - shown - 1


def test_the_list_scrolls_to_keep_the_picked_hit_in_view(ltt, ws):
    app, s = search_file(ltt, ws, needles(20), 20)
    assert len(s.results) == 8  # a 30-row terminal: header and 8 rows
    app.press("esc")
    s = app.press("end")
    assert locations(s)[0] == "a.txt:13" and s.results[-1].location == "a.txt:20" and s.results[-1].selected
    s = app.press("home")
    assert locations(s)[0] == "a.txt:1" and s.results[0].selected


# ---------------------------------------------------------------- keys while typing


def test_arrows_in_the_input_pick_a_hit(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    s = app.press("down")
    assert [h.selected for h in s.results] == [False, True, False] and picked(s) == 2
    assert search_input(s) == ("file", "needle", "found: 3")  # still typing
    s = app.press("up")
    assert [h.selected for h in s.results] == [True, False, False]


def test_page_keys_in_the_input_move_by_a_page(ltt, ws):
    app, s = search_file(ltt, ws, needles(30), 30)
    assert picked(app.press("pgdn")) == 9  # 8 rows a page
    assert picked(app.press("pgdn")) == 17
    assert picked(app.press("pgup")) == 9


def test_home_and_end_in_the_input_leave_the_pick(ltt, ws):
    app, s = search_file(ltt, ws, needles(30), 30)
    app.press("down", "down")
    for key in ("home", "end"):
        s = app.press(key)
        assert picked(s) == 3 and search_input(s) == ("file", "needle", "found: 30")


def test_esc_moves_from_the_input_to_the_results(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    s = app.press("esc")
    assert s.mode == "SEARCH"
    assert s.status == "Found: 2. Enter opens above, Esc hides the panel."
    assert s.help == "↑↓ PgUp/PgDn or wheel to pick · Enter/double-click opens above · Esc hides the panel"
    assert locations(s) == ["a.txt:1", "a.txt:2"]


def test_esc_without_hits_cancels_the_search(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 0, query="zzqq")
    s = app.press("esc")
    assert (s.mode, s.status, s.results_top) == ("EDITOR", "Search cancelled.", None)


def test_ctrl_c_in_the_results_hides_them(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    assert app.press("esc").mode == "SEARCH"
    s = app.press("ctrl+c")
    assert (s.mode, s.status, s.results_top) == ("EDITOR", "Cancelled (Ctrl+C).", None)


@pytest.mark.parametrize("start", ["tree", "editor"])
def test_a_second_esc_hides_the_panel(ltt, ws, start):
    ws.write("proj/a.txt", needles(2))
    app = ltt(ws.path("proj") if start == "tree" else ws.path("proj/a.txt"))
    app.press("ctrl+f")
    search(app, "needle", 2)
    app.press("esc")
    s = app.press("esc")
    assert (s.mode, s.status, s.results_top) == (start.upper(), "Search panel hidden (Ctrl+G brings it back).", None)


def test_enter_without_hits_stays_in_the_input(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 0, query="zzqq")
    s = app.press("enter")
    assert search_input(s) == ("file", "zzqq", "found: 0") and s.pos is None


# ---------------------------------------------------------------- keys in the results


def test_keys_in_the_results_move_the_pick(ltt, ws):
    app, s = search_file(ltt, ws, needles(30), 30)
    app.press("esc")
    moves = [("down", 2), ("up", 1), ("pgdn", 9), ("end", 30), ("pgup", 22), ("home", 1)]
    assert [(key, picked(app.press(key))) for key, _ in moves] == moves


def test_the_pick_stops_at_the_first_and_the_last_hit(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    app.press("esc")
    assert picked(app.press("up")) == 1
    app.press("end")
    assert picked(app.press("down")) == 3


def test_letters_typed_in_the_results_change_nothing(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 1)
    app.press("esc")
    s = app.type("zz")
    assert s.mode == "SEARCH" and s.editor_lines[0] == "needle" and not s.title_dirty
    assert s.results_header.startswith('Search results (file a.txt) for "needle": 1/1 ·')


def test_ctrl_f_in_the_results_goes_back_to_typing_in_the_other_scope(ltt, ws):
    ws.write("proj/b.txt", "needle in b\n")
    app, s = search_file(ltt, ws, "needle\n", 1)
    app.press("esc")
    app.press("ctrl+f")
    s = wait_found(app, "needle", 2, scope="all folders")
    assert sorted(locations(s)) == ["a.txt:1", "b.txt:1"]


def test_the_title_shows_the_file_of_the_picked_hit(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    ws.write("proj/sub/b.txt", "x needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 2)
    s = app.press("esc")
    assert s.title_path.endswith("/proj/a.txt")
    s = app.press("down")
    assert s.title_path.endswith("/proj/sub/b.txt")


# ---------------------------------------------------------------- opening hits


def test_enter_opens_the_hit_with_the_cursor_on_the_match(ltt, ws):
    ws.write("proj/a.txt", "x\n")
    ws.write("proj/sub/b.txt", "one\ntwo\nthe needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 1)
    s = app.press("enter")
    assert s.mode == "EDITOR" and s.tab_names == ["b.txt"]
    assert s.status == "Opened b.txt (4 lines), tab 1/1. Ctrl+W closes."
    assert s.pos == (3, 4, 5, 0)
    assert s.cursor == (s.text_x + 4, s.editor_row(3).y)


def test_opening_a_hit_keeps_the_panel_with_a_dimmed_header(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    s = app.press("enter")
    assert s.mode == "EDITOR" and locations(s) == ["a.txt:1", "a.txt:2"]
    assert (s.fg(1, s.results_top), s.bg(1, s.results_top)) == ("Magenta", "Black")


def test_opening_a_hit_shows_its_file_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "x\n")
    ws.write("proj/sub/b.txt", "the needle\n")
    app = ltt(ws.path("proj"))
    assert app.snap().tree_row("b.txt") is None  # inside the collapsed sub/
    app.press("ctrl+f")
    search(app, "needle", 1)
    s = app.press("enter")
    assert s.tree_row("sub").expanded and s.tree_selected.name == "b.txt"


def test_enter_on_a_hit_in_an_open_tab_switches_to_that_tab(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/b.txt", "x\nthe needle\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    assert app.snap().active_tab.name == "a.txt"
    app.press("ctrl+f", "ctrl+f")
    search(app, "needle", 1)
    s = app.press("enter")
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "b.txt"
    assert s.status == "Line 2." and s.pos[:3] == (2, 3, 5)


def test_enter_in_the_results_opens_the_picked_hit(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    app.press("esc", "down")
    s = app.press("enter")
    assert s.mode == "EDITOR" and s.pos[:3] == (2, 4, 1)


def test_a_hit_whose_file_is_gone_reports_an_error(ltt, ws):
    ws.write("proj/a.txt", "needle one\n")
    ws.write("proj/b.txt", "x\nneedle two\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 2)
    ws.remove("proj/b.txt")
    app.wait_tree("b.txt", present=False)
    app.press("down")
    s = app.press("enter")
    assert s.status.startswith("Error: File not found: ")
    assert s.mode == "TREE" and s.tabs == []


def test_a_jump_into_an_inactive_tab_is_not_hidden_under_the_panel(ltt, ws):
    # U2: b.txt was last drawn without the panel, taller than the editor is now
    lines = [f"line {i}" for i in range(1, 61)]
    lines[24] = "the uniqword here"
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/b.txt", "\n".join(lines) + "\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.press("alt+2", "alt+1")
    app.press("ctrl+f", "ctrl+f")
    search(app, "uniqword", 1)
    s = app.press("enter")
    row = s.editor_row(25)
    assert row is not None and row.text == "the uniqword here" and row.y < s.results_top
    assert s.cursor == (s.text_x + 4, row.y)


def test_a_jump_to_a_match_far_right_scrolls_the_line_to_it(ltt, ws):
    ws.write("proj/far.txt", "x" * 300 + " farneedle end\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "farneedle", 1)
    s = app.press("enter")
    assert s.pos[:3] == (1, 2, 302)
    x, y = s.cursor
    assert y == s.editor_row(1).y and s.text(y, x, x + 13) == "farneedle end"


def test_a_jump_into_a_new_tab_is_not_hidden_under_the_panel(ltt, ws):
    lines = [f"line {i}" for i in range(1, 61)]
    lines[39] = "the uniqword here"
    ws.write("proj/b.txt", "\n".join(lines) + "\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "uniqword", 1)
    s = app.press("enter")
    row = s.editor_row(40)
    assert row is not None and row.y < s.results_top and s.cursor == (s.text_x + 4, row.y)


# ---------------------------------------------------------------- Ctrl+G


@pytest.mark.parametrize("key", ["ctrl+g", "ctrl+shift+g"])
def test_the_first_ctrl_g_after_a_search_opens_hit_1(ltt, ws, key):
    # S10: it used to skip straight to hit 2
    app, s = search_file(ltt, ws, "x\nneedle one\nneedle two\nneedle three\n", 3)
    app.press("esc")
    s = app.press(key)
    assert s.mode == "EDITOR" and s.pos[0] == 2
    assert s.status == "Hit 1/3: a.txt:2 · Ctrl+G/Ctrl+Shift+G — next/prev"


def test_ctrl_g_and_ctrl_shift_g_step_through_the_hits(ltt, ws):
    app, s = search_file(ltt, ws, "x\nneedle one\nneedle two\nneedle three\n", 3)
    app.press("esc", "ctrl+g", "ctrl+g")
    s = app.press("ctrl+g")
    assert s.status.startswith("Hit 3/3: a.txt:4 ·") and s.pos[0] == 4 and picked(s) == 3
    s = app.press("ctrl+shift+g")
    assert s.status.startswith("Hit 2/3: a.txt:3 ·") and s.pos[0] == 3 and picked(s) == 2


def test_ctrl_g_stops_at_both_ends(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    app.press("esc", "ctrl+g", "ctrl+g")
    s = app.press("ctrl+g")
    assert s.status.startswith("Hit 2/2: a.txt:2 ·") and s.pos[0] == 2
    app.press("ctrl+shift+g")
    s = app.press("ctrl+shift+g")
    assert s.status.startswith("Hit 1/2: a.txt:1 ·") and s.pos[0] == 1


def test_ctrl_g_after_enter_moves_on_to_the_next_hit(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    app.press("enter")
    s = app.press("ctrl+g")
    assert s.status.startswith("Hit 2/3: a.txt:2 ·") and s.pos[0] == 2


def test_ctrl_g_opens_the_picked_hit_first(ltt, ws):
    app, s = search_file(ltt, ws, needles(4), 4)
    app.press("esc", "down", "down")
    s = app.press("ctrl+g")
    assert s.status.startswith("Hit 3/4: a.txt:3 ·") and s.pos[0] == 3


def test_ctrl_g_brings_the_hidden_panel_back(ltt, ws):
    ws.write("proj/a.txt", needles(2))
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 2)
    s = app.press("esc", "esc")
    assert s.mode == "TREE" and s.results_top is None
    s = app.press("ctrl+g")
    assert s.mode == "EDITOR" and s.tab_names == ["a.txt"] and s.pos[0] == 1
    assert locations(s) == ["a.txt:1", "a.txt:2"]


def test_ctrl_g_without_a_search_asks_for_one(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    s = app.press("ctrl+g")
    assert s.status == "Run a search first (Ctrl+F)." and s.tabs == []


def test_a_new_ctrl_f_starts_from_an_empty_query(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    app.press("enter")
    s = app.press("ctrl+f")
    assert search_input(s) == ("file", "", "search starts at 2 characters") and s.results_top is None


def test_the_help_line_offers_ctrl_g_while_the_panel_is_shown(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2, cols=280)
    s = app.press("enter")
    assert s.help.endswith("Ctrl+P pin tab · Ctrl+G — next hit")
    s = app.press("esc")  # to the tree, the panel stays
    assert s.help.endswith(" · Ctrl+G — next hit")
    s = app.press("esc")  # hides it
    assert "Ctrl+G — next hit" not in s.help


# ---------------------------------------------------------------- shortcuts over the results (T7)


def test_ctrl_s_saves_while_the_results_have_focus(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj/a.txt"))
    app.type("X")
    app.press("ctrl+f")
    search(app, "needle", 1)
    app.press("esc")
    s = app.press("ctrl+s")
    assert s.mode == "SEARCH" and s.status == "Saved: a.txt" and not s.title_dirty
    assert ws.read("proj/a.txt") == "Xneedle\n"


def test_ctrl_w_closes_the_tab_while_the_results_have_focus(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 1)
    app.press("esc")
    s = app.press("ctrl+w")
    assert s.tabs == [] and s.status == 'Closed tab "a.txt".'


def test_ctrl_q_quits_while_the_results_have_focus(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 1)
    app.press("esc")
    assert app.quit() == 0


def test_a_paste_while_the_results_have_focus_does_not_reach_the_file(ltt, ws):
    app, s = search_file(ltt, ws, "needle\n", 1)
    app.press("esc")
    s = app.paste("pasted text")
    assert s.mode == "SEARCH" and s.editor_lines[0] == "needle" and not s.title_dirty
    assert s.status.startswith("Pasting goes into the editor")


# ---------------------------------------------------------------- a selection seeds the query


def test_the_selected_text_becomes_the_query(ltt, ws):
    ws.write("proj/a.txt", "the manager\nmanager two\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("down", *["shift+right"] * 7)
    app.press("ctrl+f")
    s = wait_found(app, "manager", 2, scope="file")
    assert locations(s) == ["a.txt:1", "a.txt:2"]


def test_a_multi_line_selection_becomes_a_one_line_query(ltt, ws):
    ws.write("proj/a.txt", "hello brave\n  world of code\nhello brave world of code\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("shift+down", "shift+down")
    app.press("ctrl+f")
    s = wait_found(app, "hello brave world of code", 1)
    assert locations(s) == ["a.txt:3"]


def test_a_selection_seed_is_cut_at_120_characters(ltt, ws):
    ws.write("proj/a.txt", "w" * 150 + "\n")
    app = ltt(ws.path("proj/a.txt"), cols=200)
    app.press("shift+end")
    app.press("ctrl+f")
    wait_found(app, "w" * 120, 1)


def test_a_selection_seeds_only_a_search_started_from_the_editor(ltt, ws):
    ws.write("proj/a.txt", "the manager\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("shift+end", "esc")  # the selection stays, the focus goes to the tree
    s = app.press("ctrl+f")
    assert search_input(s) == ("file", "", "search starts at 2 characters")


# ---------------------------------------------------------------- mouse


def test_a_click_on_a_hit_picks_it_without_opening_it(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    s = app.press("enter")  # hit 1 open, the panel stays
    s = app.click(5, s.results[2].y)
    assert s.mode == "SEARCH" and picked(s) == 3 and s.results[2].selected
    assert s.pos[0] == 1


def test_a_double_click_on_a_hit_opens_it(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    app.press("esc")
    s = app.double_click(5, s.results[1].y)
    s = app.wait(lambda s: s.mode == "EDITOR", what="the hit opened")
    assert s.pos[:3] == (2, 4, 1)


def test_a_click_on_the_header_gives_the_results_the_focus(ltt, ws):
    app, s = search_file(ltt, ws, needles(2), 2)
    s = app.press("enter")
    s = app.click(10, s.results_top)
    assert s.mode == "SEARCH" and (s.fg(1, s.results_top), s.bg(1, s.results_top)) == ("White", "DarkMagenta")


def test_the_wheel_over_the_results_moves_the_pick(ltt, ws):
    app, s = search_file(ltt, ws, needles(30), 30)
    app.press("esc")
    s = app.wheel(10, s.results[2].y, down=True, notches=5)
    assert picked(s) == 4  # 3/5 of a row per notch
    s = app.wheel(10, s.results[2].y, down=False, notches=5)
    assert picked(s) == 1


def test_the_wheel_over_the_results_while_typing_keeps_the_input(ltt, ws):
    app, s = search_file(ltt, ws, needles(30), 30)
    s = app.wheel(10, s.results[2].y, down=True, notches=5)
    assert picked(s) == 4 and search_input(s) == ("file", "needle", "found: 30")


def test_a_click_on_the_results_while_typing_is_ignored(ltt, ws):
    app, s = search_file(ltt, ws, needles(3), 3)
    s = app.click(5, s.results[1].y)
    assert picked(s) == 1 and search_input(s) == ("file", "needle", "found: 3")


# ---------------------------------------------------------------- kitty keyboard


def test_search_keys_work_with_the_kitty_keyboard(ltt, ws):
    ws.write("proj/a.txt", needles(3))
    app = ltt(ws.path("proj/a.txt"), keyboard="kitty")
    app.press("ctrl+f", *"needle")
    wait_found(app, "needle", 3)
    app.press("down")
    s = app.press("enter")
    assert s.mode == "EDITOR" and s.pos[0] == 2
    s = app.press("ctrl+g")
    assert s.status.startswith("Hit 3/3: a.txt:3 ·")
    s = app.press("ctrl+shift+g")
    assert s.status.startswith("Hit 2/3: a.txt:2 ·")
    app.press("ctrl+f", *"zz", "esc")
    assert app.snap().status == "Search cancelled."


# ---------------------------------------------------------------- hits after edits


@pytest.mark.parametrize("edit, line", [("added", 4), ("deleted", 2)])
def test_ctrl_g_after_an_edit_goes_where_the_hit_is_now(ltt, ws, edit, line):  # Ox's kaolinite document_searching
    app, s = search_file(ltt, ws, "x\nneedle one\nneedle two\nneedle three\n", 3)
    app.press("enter")  # hit 1, on line 2
    if edit == "added":
        app.press("ctrl+home", "enter")  # a line above every hit
    else:
        app.press("home", "shift+down", "backspace")  # hit 1's own line goes
    s = app.press("ctrl+g")
    assert s.editor_row(s.pos[0]).text == "needle two" and (s.pos[0], s.pos[2]) == (line, 1)
    assert s.status.startswith(f"Hit 2/3: a.txt:{line} ·")


def test_ctrl_g_to_a_hit_whose_file_is_gone_reports_an_error(ltt, ws):  # Textadept's find_test.lua goto_file_found
    ws.write("proj/a.txt", "needle one\n")
    ws.write("proj/b.txt", "x\nneedle two\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 2)
    app.press("esc", "esc")
    ws.remove("proj/b.txt")
    app.wait_tree("b.txt", present=False)
    app.press("ctrl+g")
    s = app.press("ctrl+g")
    assert s.status.startswith("Error: File not found: ") and s.tab_names == ["a.txt"]


def test_a_selection_seed_is_cut_at_120_characters_of_any_kind(ltt, ws):  # Ox's kaolinite char_mapping
    ws.write("proj/a.txt", "a" + "\U0001F440" * 130 + "\n")
    app = ltt(ws.path("proj/a.txt"), cols=300)
    app.press("shift+end")
    app.press("ctrl+f")
    wait_found(app, "a" + "\U0001F440" * 119, 1, timeout=5)
