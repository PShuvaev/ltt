"""Tabs: switching (Alt+1..9, Ctrl+PgUp/PgDn, clicks, wheel), closing (Ctrl+W, ×), the bar, the title."""
import os
import time

import pytest

from harness import keys as K
from prompts_helpers import goto, select_in_tree

CLOSE_QUESTION = 'Tab "a.txt" has changes!  S saves · D closes without saving · Esc cancels'


def three_tabs(ltt, ws, **kw):
    """ltt with proj/a.txt, b.txt, c.txt open; a.txt is active."""
    for n in "abc":
        ws.write(f"proj/{n}.txt", n)
    return ltt(ws.path("proj"), *(ws.path(f"proj/{n}.txt") for n in "abc"), **kw)


def dirty_a(ltt, ws):
    """ltt with a.txt (active, unsaved edits: "Xa") and b.txt open."""
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.type("X")
    app.wait(lambda s: s.tabs[0].dirty, what="a.txt ●")
    return app


def burst_click(x, y=1):
    return K.mouse(K.LEFT, x, y, True) + K.mouse(K.LEFT, x, y, False)


# ---- switching ----

def test_alt_digit_switches_to_that_tab(ltt, ws):
    app = three_tabs(ltt, ws)
    s = app.press("alt+2")
    assert s.active_tab.name == "b.txt" and s.editor_lines == ["b"] and s.status == "Tab 2/3: b.txt"
    assert s.title_path.endswith("/proj/b.txt") and s.title.endswith("[tab 2/3]")


def test_alt_digit_past_the_last_tab_does_nothing(ltt, ws):
    app = three_tabs(ltt, ws)
    before = app.snap()
    s = app.press("alt+9")
    assert s.active_tab.name == "a.txt" and s.status == before.status and s.mode == "EDITOR"


def test_alt_digit_from_the_tree_moves_to_the_editor(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    s = app.press("alt+3")
    assert s.mode == "EDITOR" and s.active_tab.name == "c.txt"


def test_alt_digit_follows_the_physical_key_on_azerty(ltt, ws):
    # V16: on AZERTY the 2 key types é; kitty reports it with its base-layout key
    app = three_tabs(ltt, ws)
    s = app.press(K.kitty(ord("é"), K.ALT, base=ord("2")))
    assert s.active_tab.name == "b.txt"


def test_alt_digit_in_kitty_keyboard_mode(ltt, ws):
    app = three_tabs(ltt, ws, keyboard="kitty")
    assert app.press("alt+3").active_tab.name == "c.txt"


def test_each_tab_keeps_its_cursor_and_scroll(ltt, ws):
    ws.write("proj/a.txt", "\n".join(f"a{i}" for i in range(1, 101)))
    ws.write("proj/b.txt", "\n".join(f"b{i}" for i in range(1, 101)))
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    goto(app, "60")
    app.press("end")
    top = app.snap().editor_rows[0].number
    app.press("alt+2")
    goto(app, "5")
    s = app.press("alt+1")
    assert s.pos[:3] == (60, 100, 4) and s.editor_rows[0].number == top
    assert app.press("alt+2").pos[0] == 5


def test_each_tab_keeps_its_own_undo(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.type("X")
    app.press("alt+2")
    app.type("Y")
    app.press("alt+1")
    s = app.press("ctrl+z")
    assert s.editor_lines == ["a"] and not s.tabs[0].dirty and s.tabs[1].dirty
    s = app.press("alt+2")
    assert s.editor_lines == ["Yb"]


def test_status_marks_a_dirty_tab(ltt, ws):
    app = dirty_a(ltt, ws)
    app.press("alt+2")
    assert app.press("alt+1").status == "Tab 1/2: a.txt ●"


def test_ctrl_pgdn_and_ctrl_pgup_cycle_and_wrap(ltt, ws):
    app = three_tabs(ltt, ws)
    assert app.press("ctrl+pgdn").active_tab.name == "b.txt"
    assert app.press("ctrl+pgdn").active_tab.name == "c.txt"
    s = app.press("ctrl+pgdn")
    assert s.active_tab.name == "a.txt" and s.status == "Tab 1/3: a.txt"
    s = app.press("ctrl+pgup")
    assert s.active_tab.name == "c.txt" and s.status == "Tab 3/3: c.txt"


def test_ctrl_pgdn_from_the_tree_moves_to_the_editor(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    s = app.press("ctrl+pgdn")
    assert s.mode == "EDITOR" and s.active_tab.name == "b.txt"


@pytest.mark.parametrize("key", ["ctrl+w", "ctrl+pgdn", "ctrl+pgup"])
def test_tab_keys_without_tabs(ltt, ws, key):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    s = app.press(key)
    assert s.status == "No open tabs." and s.mode == "TREE"


def test_reopening_an_open_file_activates_its_tab(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    select_in_tree(app, "c.txt")
    s = app.press("enter")
    assert s.tab_names == ["a.txt", "b.txt", "c.txt"] and s.active_tab.name == "c.txt"
    assert s.status == "Tab: c.txt" and s.mode == "EDITOR"


def test_new_tabs_open_at_the_end(ltt, ws):
    app = three_tabs(ltt, ws)
    ws.write("proj/d.txt", "d")
    s = goto(app, "d.txt")
    assert s.tab_names == ["a.txt", "b.txt", "c.txt", "d.txt"] and s.active_tab.name == "d.txt"
    assert s.title.endswith("[tab 4/4]")


# ---- closing ----

def test_ctrl_w_closes_a_clean_tab_and_its_right_neighbour_takes_over(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("alt+2")
    s = app.press("ctrl+w")
    assert s.status == 'Closed tab "b.txt". Tabs left: 2.'
    assert s.tab_names == ["a.txt", "c.txt"] and s.active_tab.name == "c.txt" and s.title.endswith("[tab 2/2]")


def test_closing_the_rightmost_tab_activates_its_left_neighbour(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("alt+3")
    s = app.press("ctrl+w")
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "b.txt"


def test_ctrl_w_from_the_search_results_closes_the_tab(ltt, ws):
    # T7: the focused results panel swallowed Ctrl+W
    ws.write("proj/a.txt", "alpha needle")
    ws.write("proj/b.txt", "beta")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.press("ctrl+f", "ctrl+f")  # all folders
    app.type("needle")
    app.wait(lambda s: s.results, what="the hit")
    assert app.press("esc").mode == "SEARCH"
    s = app.press("ctrl+w")
    assert s.tab_names == ["b.txt"] and s.status == 'Closed tab "a.txt". Tabs left: 1.'


def test_ctrl_w_in_the_tree_closes_the_active_tab(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    s = app.press("ctrl+w")
    assert s.tab_names == ["b.txt", "c.txt"] and s.mode == "TREE"


def test_closing_the_last_tab_leaves_no_file(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    s = app.press("ctrl+w")
    assert s.status == 'Closed tab "a.txt".' and s.tabs == [] and s.mode == "TREE"
    assert s.text(1, s.editor_x).strip() == "(no file)"
    assert s.editor_hint.startswith("Pick a file on the left and press Enter.")
    assert s.pos is None and "[tab" not in s.title


def test_ctrl_w_on_a_dirty_tab_asks(ltt, ws):
    app = dirty_a(ltt, ws)
    s = app.press("ctrl+w")
    assert s.mode == "CONFIRM" and s.question == CLOSE_QUESTION and s.help == ""
    assert s.tab_names == ["a.txt", "b.txt"]


@pytest.mark.parametrize("answer", ["s", "S", "ы"])
def test_dirty_close_s_saves_and_closes(ltt, ws, answer):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    s = app.press(answer)
    assert ws.read("proj/a.txt") == "Xa"
    assert s.tab_names == ["b.txt"] and s.status == 'Closed tab "a.txt". Tabs left: 1.'


@pytest.mark.parametrize("answer", ["d", "D", "в"])
def test_dirty_close_d_closes_without_saving(ltt, ws, answer):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    s = app.press(answer)
    assert ws.read("proj/a.txt") == "a" and s.tab_names == ["b.txt"]


def test_dirty_close_esc_keeps_the_tab(ltt, ws):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    s = app.press("esc")
    assert (s.mode, s.status) == ("EDITOR", "Cancelled.")
    assert s.tab_names == ["a.txt", "b.txt"] and s.tabs[0].dirty and ws.read("proj/a.txt") == "a"


def test_dirty_close_ctrl_c_keeps_the_tab(ltt, ws):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    s = app.press("ctrl+c")
    assert s.status == "Cancelled (Ctrl+C)." and s.tab_names == ["a.txt", "b.txt"] and s.tabs[0].dirty


@pytest.mark.parametrize("key", ["enter", "x", "y", "space", "ctrl+w"])
def test_dirty_close_other_keys_keep_the_question(ltt, ws, key):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    s = app.press(key)
    assert s.mode == "CONFIRM" and s.question == CLOSE_QUESTION
    assert s.tab_names == ["a.txt", "b.txt"] and ws.read("proj/a.txt") == "a"


def test_dirty_close_asks_again_when_the_file_changed_on_disk(ltt, ws):
    app = dirty_a(ltt, ws)
    ws.write("proj/a.txt", "agent", mtime=time.time() + 5)
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on a.txt")
    app.press("ctrl+w")
    s = app.press("s")
    assert s.mode == "CONFIRM" and s.question.startswith("a.txt changed on disk since it was opened!  S overwrites it")
    s = app.press("s")
    assert ws.read("proj/a.txt") == "Xa" and s.tab_names == ["b.txt"]


# ---- the bar ----

def test_tab_bar_overflow_keeps_the_active_tab_visible(ltt, ws):
    names = [f"file-number-{i}.txt" for i in range(1, 10)]
    for n in names:
        ws.write("proj/" + n, n)
    app = ltt(ws.path("proj"), *(ws.path("proj/" + n) for n in names))
    s = app.snap()
    assert s.tab_names == names[:3] and s.active_tab.name == names[0]
    assert "‹" not in s.lines[1] and s.text(1, s.cols - 1) == "›" and s.fg(s.cols - 1, 1) == "Yellow"
    s = app.press("alt+9")
    assert s.tab_names[-1] == names[8] and s.active_tab.name == names[8]
    assert s.text(1, s.editor_x, s.editor_x + 1) == "‹" and s.fg(s.editor_x, 1) == "Yellow" and "›" not in s.lines[1]
    s = app.press("alt+5")
    assert s.active_tab.name == names[4] and "‹" in s.lines[1] and "›" in s.lines[1]


def test_cycling_through_an_overflowing_bar_always_shows_the_active_tab(ltt, ws):
    names = [f"file-number-{i}.txt" for i in range(1, 10)]
    for n in names:
        ws.write("proj/" + n, n)
    app = ltt(ws.path("proj"), *(ws.path("proj/" + n) for n in names))
    for i in range(1, 10):  # tabs 2..9, then round to 1
        s = app.press("ctrl+pgdn")
        assert s.active_tab is not None and s.active_tab.name == names[i % 9]
    for i in (8, 7, 6):  # back round to 9, 8, 7
        assert app.press("ctrl+pgup").active_tab.name == names[i]


def test_same_names_show_their_folder(ltt, ws):
    # F2: several README.md tabs looked the same
    ws.write("proj/a/README.md", "A")
    ws.write("proj/b/README.md", "B")
    ws.write("proj/c.txt", "c")
    app = ltt(ws.path("proj"), ws.path("proj/a/README.md"), ws.path("proj/b/README.md"), ws.path("proj/c.txt"))
    assert app.snap().tab_names == ["README.md · a", "README.md · b", "c.txt"]


def test_same_name_label_is_plain_again_when_alone(ltt, ws):
    ws.write("proj/a/README.md", "A")
    ws.write("proj/b/README.md", "B")
    app = ltt(ws.path("proj"), ws.path("proj/a/README.md"), ws.path("proj/b/README.md"))
    s = app.press("ctrl+w")
    assert s.tab_names == ["README.md"] and s.editor_lines == ["B"]


def test_dirty_tab_label_has_a_dot(ltt, ws):
    app = dirty_a(ltt, ws)
    s = app.snap()
    assert s.text(1, s.tabs[0].x0, s.tabs[0].x1 + 1) == " a.txt ● × " and not s.tabs[1].dirty


def test_active_tab_dims_while_the_tree_has_focus(ltt, ws):
    app = three_tabs(ltt, ws)
    s = app.snap()
    x = s.active_tab.x0 + 1
    assert (s.fg(x, 1), s.bg(x, 1)) == ("White", "DarkCyan")
    s = app.press("esc")
    assert s.active_tab.name == "a.txt" and (s.fg(x, 1), s.bg(x, 1)) == ("White", "DarkGray")
    x = s.tabs[1].x0 + 1
    assert (s.fg(x, 1), s.bg(x, 1)) == ("Gray", "Black")


def test_title_shows_the_tab_number(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    assert app.snap().title.endswith("[tab 1/2]")
    s = app.press("ctrl+w")
    assert s.title_path.endswith("/proj/b.txt") and "[tab" not in s.title


def test_title_shows_the_tree_selection_while_the_tree_has_focus(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    s = select_in_tree(app, "c.txt")
    assert s.title_path.endswith("/proj/c.txt") and "[tab" not in s.title and s.active_tab.name == "a.txt"


# ---- mouse ----

def test_click_on_a_tab_activates_it(ltt, ws):
    app = three_tabs(ltt, ws)
    app.press("esc")
    tab = app.snap().tabs[2]
    s = app.click(tab.x0 + 2, 1)
    assert s.active_tab.name == "c.txt" and s.mode == "EDITOR" and s.status == "Tab 3/3: c.txt"


def test_click_on_x_closes_that_tab(ltt, ws):
    app = three_tabs(ltt, ws)
    tab = app.snap().tabs[1]
    s = app.click(tab.close_x, 1)
    assert s.tab_names == ["a.txt", "c.txt"] and s.active_tab.name == "a.txt"
    assert s.status == 'Closed tab "b.txt". Tabs left: 2.'


def test_click_on_x_of_a_dirty_tab_asks(ltt, ws):
    app = dirty_a(ltt, ws)
    app.press("alt+2")
    tab = app.snap().tabs[0]
    s = app.click(tab.close_x, 1)
    assert s.mode == "CONFIRM" and s.question == CLOSE_QUESTION


def test_middle_click_closes_a_tab(ltt, ws):
    app = three_tabs(ltt, ws)
    tab = app.snap().tabs[2]
    s = app.click(tab.x0 + 2, 1, button=K.MIDDLE)
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "a.txt"


def test_wheel_over_the_tab_bar_switches_tabs(ltt, ws):
    app = three_tabs(ltt, ws)
    x = app.snap().editor_x + 20
    assert app.wheel(x, 1, down=True).active_tab.name == "b.txt"
    assert app.wheel(x, 1, down=False).active_tab.name == "a.txt"
    assert app.wheel(x, 1, down=False).active_tab.name == "c.txt"  # wraps


def test_click_on_the_empty_part_of_the_bar_does_nothing(ltt, ws):
    app = three_tabs(ltt, ws)
    before = app.snap()
    s = app.click(before.cols - 3, 1)
    assert s.tab_names == before.tab_names and s.active_tab.name == "a.txt" and s.status == before.status


def test_tab_clicks_wait_while_a_question_is_open(ltt, ws):
    app = dirty_a(ltt, ws)
    app.press("ctrl+w")
    tab = app.snap().tabs[1]
    s = app.click(tab.x0 + 2, 1)
    assert s.mode == "CONFIRM" and s.active_tab.name == "a.txt"


def test_ctrl_w_and_a_click_in_one_burst_activate_the_clicked_tab(ltt, ws):
    # U3: a click read in the same burst as Ctrl+W used the closed tab's index and crashed
    app = three_tabs(ltt, ws)
    b = app.snap().tabs[1]
    app.write(b"\x17" + burst_click(b.x0 + 2))  # before the frame is redrawn, b.txt is still there
    s = app.settle()
    assert app.alive and s.tab_names == ["b.txt", "c.txt"] and s.active_tab.name == "b.txt"
    assert s.status == "Tab 1/2: b.txt"


def test_ctrl_w_and_a_click_on_the_closed_tab_in_one_burst(ltt, ws):
    # U3: the click lands where the closed tab was drawn — it is simply no longer there
    app = three_tabs(ltt, ws)
    a = app.snap().tabs[0]
    app.write(b"\x17" + burst_click(a.x0 + 2))
    s = app.settle()
    assert app.alive and s.tab_names == ["b.txt", "c.txt"] and s.active_tab.name == "b.txt"
    assert s.status == 'Closed tab "a.txt". Tabs left: 2.'


# ---- a tab hidden while the layout changed ----

def test_hidden_tab_catches_up_with_a_resize(ltt, ws):
    # U2: a tab keeps the view height it was last drawn with
    ws.write("proj/a.txt", "\n".join(f"a{i}" for i in range(1, 101)))
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"), rows=30)
    goto(app, "25")  # the cursor near the bottom of a 26-row view
    app.press("alt+2")
    app.resize(100, 15)
    s = app.press("alt+1")
    row = s.editor_row(25)
    assert s.pos[0] == 25 and row is not None and row.current
    assert s.cursor == (s.text_x, row.y)


def test_hidden_tab_catches_up_with_the_search_results_panel(ltt, ws):
    # U2: a jump to a hit in an inactive tab hid the line under the results panel
    lines = [f"filler {i}" for i in range(1, 301)]
    lines[299] = "needle_xyz is here"
    ws.write("proj/a.txt", "\n".join(lines))
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"), cols=120, rows=50)
    app.press("alt+2")
    app.press("ctrl+f", "ctrl+f")  # all folders
    app.type("needle_xyz")
    app.wait(lambda s: s.results, what="the hit")
    app.press("enter")
    s = app.wait(lambda s: s.title_path.endswith("/proj/a.txt"), what="a.txt active")
    row = s.editor_row(300)
    assert s.results_top is not None and row is not None and row.y < s.results_top


def test_a_path_through_a_link_finds_the_tab_already_open(ltt, ws):  # Vim's Test_swap_symlink
    p = ws.write("proj/a.c", "1\n2\n3\n4\n")
    os.symlink(ws.path("proj"), ws.path("alias"))
    app = ltt(ws.path("proj"), p)
    app.press("ctrl+l")
    app.type(ws.path("alias/a.c") + ":3")
    s = app.press("enter")
    assert s.tab_names == ["a.c"] and s.pos[:2] == (3, 5)
