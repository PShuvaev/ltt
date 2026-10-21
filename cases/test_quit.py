"""Ctrl+Q: quitting at once, the unsaved-tabs question and its answers, the save chain behind S, Ctrl+C."""
import os
import time

import pytest

from harness import keys as K

QUESTION = "Unsaved tabs: 2!  S saves all · D quits without saving · Esc cancels"
TERMINAL_RESTORED = b"\x1b[<u\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l\x1b[0m\x1b[?25h\x1b[?1049l\x1b[?1l\x1b>"


def two_dirty(ltt, ws, **kw):
    """ltt with a.txt ("Xa") and b.txt ("Yb") open, both with unsaved edits; b.txt is active."""
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"), **kw)
    app.type("X")
    app.press("alt+2")
    app.type("Y")
    app.wait(lambda s: s.tabs[0].dirty and s.tabs[1].dirty, what="both tabs ●")
    return app


# ---- nothing unsaved: out at once ----

def test_ctrl_q_with_nothing_open_exits_at_once(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    assert app.quit() == 0


def test_ctrl_q_with_clean_tabs_exits_at_once_and_restores_the_terminal(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    assert app.quit() == 0
    assert app.output.endswith(TERMINAL_RESTORED)


def test_ctrl_q_after_saving_exits_at_once(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.type("X")
    app.press("ctrl+s")
    assert app.quit() == 0
    assert ws.read("proj/a.txt") == "Xa" and ws.recovery_files() == []


def test_ctrl_q_from_the_tree_with_nothing_unsaved(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("esc")
    assert app.quit() == 0


# ---- unsaved tabs: the question ----

def test_ctrl_q_with_unsaved_tabs_asks(ltt, ws):
    app = two_dirty(ltt, ws)
    s = app.press("ctrl+q")
    assert s.mode == "CONFIRM" and s.question == QUESTION and s.help == "" and app.alive
    assert (s.fg(1, s.status_y), s.bg(1, s.status_y)) == ("White", "DarkRed")


def test_question_counts_only_the_unsaved_tabs(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.type("X")
    s = app.press("ctrl+q")
    assert s.question == "Unsaved tabs: 1!  S saves all · D quits without saving · Esc cancels"


@pytest.mark.parametrize("answer", ["s", "S", "ы"])
def test_s_saves_every_tab_and_quits(ltt, ws, answer):
    app = two_dirty(ltt, ws)
    assert app.quit(answer) == 0
    assert ws.read("proj/a.txt") == "Xa" and ws.read("proj/b.txt") == "Yb"
    assert ws.recovery_files() == []


@pytest.mark.parametrize("answer", ["d", "D", "в"])
def test_d_quits_without_saving(ltt, ws, answer):
    app = two_dirty(ltt, ws)
    assert app.quit(answer) == 0
    assert ws.read("proj/a.txt") == "a" and ws.read("proj/b.txt") == "b"
    assert ws.recovery_files() == []  # the edits are gone on purpose: nothing comes back next time


@pytest.mark.parametrize("answer, a, b", [("ы", "Xa", "Yb"), ("в", "a", "b")], ids=["s", "d"])
def test_cyrillic_answers_in_kitty_keyboard_mode(ltt, ws, answer, a, b):
    app = two_dirty(ltt, ws, keyboard="kitty")
    base = {"ы": "s", "в": "d"}[answer]
    assert app.quit(K.kitty_key(answer, base=base)) == 0
    assert (ws.read("proj/a.txt"), ws.read("proj/b.txt")) == (a, b)


def test_esc_cancels_the_quit(ltt, ws):
    app = two_dirty(ltt, ws)
    app.press("ctrl+q")
    s = app.press("esc")
    assert app.alive and (s.mode, s.status) == ("EDITOR", "Cancelled.")
    assert s.tabs[0].dirty and s.tabs[1].dirty and ws.read("proj/a.txt") == "a"


def test_ctrl_c_cancels_the_quit(ltt, ws):
    app = two_dirty(ltt, ws)
    app.press("ctrl+q")
    s = app.press("ctrl+c")
    assert app.alive and (s.mode, s.status) == ("EDITOR", "Cancelled (Ctrl+C).")
    assert s.tabs[0].dirty and s.tabs[1].dirty


@pytest.mark.parametrize("key", ["enter", "x", "q", "y", "space", "tab", "ctrl+q", "ctrl+w"])
def test_other_keys_keep_the_question(ltt, ws, key):
    # no destructive default: only S, D and Esc answer
    app = two_dirty(ltt, ws)
    app.press("ctrl+q")
    s = app.press(key)
    assert app.alive and s.mode == "CONFIRM" and s.question == QUESTION
    assert ws.read("proj/a.txt") == "a" and s.tab_names == ["a.txt", "b.txt"]


def test_question_ignores_clicks(ltt, ws):
    app = two_dirty(ltt, ws)
    app.press("ctrl+q")
    tab = app.snap().tabs[0]
    s = app.click(tab.close_x, 1)
    assert app.alive and s.mode == "CONFIRM" and s.question == QUESTION and len(s.tabs) == 2


# ---- the save chain behind S ----

def test_s_asks_about_a_file_changed_on_disk_then_saves_all(ltt, ws):
    app = two_dirty(ltt, ws)
    ws.write("proj/a.txt", "agent", mtime=time.time() + 5)
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on a.txt")
    app.press("ctrl+q")
    s = app.press("s")
    assert s.mode == "CONFIRM" and s.question.startswith(
        "a.txt changed on disk since it was opened!  S overwrites it · R takes the disk version")
    app.press("s", settle=False)
    assert app.wait_exit() == 0
    assert ws.read("proj/a.txt") == "Xa" and ws.read("proj/b.txt") == "Yb"


def test_r_at_the_disk_question_takes_the_disk_version_and_stays(ltt, ws):
    app = two_dirty(ltt, ws)
    ws.write("proj/a.txt", "agent", mtime=time.time() + 5)
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on a.txt")
    app.press("ctrl+q", "s")
    s = app.press("r")
    assert app.alive and s.mode == "EDITOR"
    assert s.status == "Took the disk version of a.txt; Ctrl+Z brings back your edits."
    assert [t.dirty for t in s.tabs] == [False, True] and ws.read("proj/b.txt") == "b"
    assert app.press("alt+1").editor_lines == ["agent"]


def test_esc_at_the_disk_question_saves_nothing_and_stays(ltt, ws):
    app = two_dirty(ltt, ws)
    ws.write("proj/a.txt", "agent", mtime=time.time() + 5)
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on a.txt")
    app.press("ctrl+q", "s")
    s = app.press("esc")
    assert app.alive and s.status == "Cancelled." and [t.dirty for t in s.tabs] == [True, True]
    assert ws.read("proj/a.txt") == "agent" and ws.read("proj/b.txt") == "b"


def test_s_saves_a_file_deleted_on_disk_again(ltt, ws):
    app = two_dirty(ltt, ws)
    ws.remove("proj/a.txt")
    app.wait(lambda s: s.tabs[0].missing, what="✗ on a.txt")
    app.press("ctrl+q")
    s = app.press("s")
    assert s.mode == "CONFIRM" and s.question == "a.txt was deleted on disk!  S saves it again · Esc cancels"
    app.press("s", settle=False)
    assert app.wait_exit() == 0
    assert ws.read("proj/a.txt") == "Xa" and ws.read("proj/b.txt") == "Yb"


def test_save_error_stops_the_quit(ltt, ws):
    app = two_dirty(ltt, ws)
    os.chmod(ws.path("proj/a.txt"), 0o444)
    try:
        app.press("ctrl+q")
        s = app.press("s")
        assert app.alive and s.mode == "EDITOR" and s.status.startswith("Save error: ")
        assert [t.dirty for t in s.tabs] == [True, True] and ws.read("proj/b.txt") == "b"
    finally:
        os.chmod(ws.path("proj/a.txt"), 0o644)


# ---- keys ----

def test_ctrl_q_from_the_search_results_quits(ltt, ws):
    # T7: the focused results panel swallowed Ctrl+Q
    ws.write("proj/a.txt", "alpha needle")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")  # the selected root
    app.type("needle")
    app.wait(lambda s: s.results, what="the hit")
    assert app.press("esc").mode == "SEARCH"
    assert app.quit() == 0


def test_ctrl_q_inside_a_prompt_does_nothing(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.press("ctrl+q")
    assert app.alive and s.mode == "PROMPT"


def test_ctrl_q_by_the_physical_key_on_a_cyrillic_layout(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press(K.kitty_key("ctrl+й", base="q"), settle=False)  # й sits on the Q key
    assert app.wait_exit() == 0


def test_cmd_q_quits_in_kitty_terminals(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press("cmd+q", settle=False)
    assert app.wait_exit() == 0


def test_ctrl_c_never_quits(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    s = app.press("ctrl+c")
    assert app.alive and s.status == "Ctrl+C copies a selection. Ctrl+Q quits."
