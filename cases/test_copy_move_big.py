"""Big copies run in the background: progress in the help line, Esc stops them, Ctrl+Q asks first."""
import os
import re

import pytest

from copy_move_helpers import (BIG_DIRS, BIG_FILES, big_folder, contents, count_files, other_disk, rows,  # noqa: F401
                               select, status_is)

TOTAL = BIG_DIRS * BIG_FILES
PROGRESS = re.compile(r"^Copying big/ into dst/: (\d+) files so far · Esc in the tree stops it$")
STOPPED = "Stopped: the copy in dst/ is incomplete."
QUIT_QUESTION = "Files are still being copied!  Q stops the copy and quits · Esc keeps it going"

pytestmark = pytest.mark.slow


def copied(s):
    """The count in the help line while the copy runs; -1 when no copy is shown."""
    m = PROGRESS.match(s.help)
    return int(m.group(1)) if m else -1


def start_copy(ltt, ws, open_file=False):
    """ltt with the shared folder `big` and an empty `dst` as roots; Ctrl+C on big, Ctrl+V into dst.

    open_file: dst holds notes.txt, opened (the editor had the focus) before the paste.
    Returns once the help line shows files copied."""
    if open_file:
        ws.write("dst/notes.txt", "n\n")
    else:
        ws.mkdir("dst")
    app = ltt(big_folder(), ws.path("dst"), cols=160)
    app.press("ctrl+c")  # the root big is selected at the start
    app.press("end")     # the last row: dst, or the file in it
    if open_file:
        app.press("enter")
        app.wait_mode("EDITOR")
        app.press("esc")
    app.press("ctrl+v")
    s = app.wait(lambda s: copied(s) > 0, what="the copy's progress in the help line")
    return app, s


def test_big_copy_shows_progress_and_finishes(ltt, ws):
    app, s = start_copy(ltt, ws)
    first = copied(s)
    app.wait(lambda s: copied(s) > first, what="the count going up")
    s = status_is(app, f"Copied big/ into dst/ ({TOTAL} files).", timeout=120)
    assert s.help.startswith("Ctrl+V copies big/ here · Esc forgets it")
    assert rows(s)["dst/big"].selected
    assert contents(ws.path("dst/big")) == contents(big_folder())


def test_esc_stops_a_big_copy_and_what_is_left_is_whole_files(ltt, ws):
    app, _ = start_copy(ltt, ws)
    s = app.press("esc")
    assert s.status in ("Stopping the copy…", STOPPED), s.status
    s = status_is(app, STOPPED)
    assert s.help.startswith("Ctrl+V copies big/ here · Esc forgets it")  # still picked up
    assert rows(s)["dst/big"].selected
    left = contents(ws.path("dst/big"))
    assert 0 < len(left) < TOTAL
    big = big_folder()
    for rel, data in left.items():  # stopped between files: every file there is complete
        with open(os.path.join(big, rel), "rb") as f:
            assert f.read() == data, rel


def test_ctrl_v_while_copying_says_still_copying(ltt, ws):
    app, _ = start_copy(ltt, ws)
    s = app.press("ctrl+v")
    assert s.status == "Still copying — Esc stops it."
    app.press("esc")
    status_is(app, STOPPED)
    assert os.listdir(ws.path("dst")) == ["big"]
    assert count_files(ws.path("dst/big")) < TOTAL


def test_progress_shows_in_the_editor_and_esc_there_does_not_stop_the_copy(ltt, ws):
    app, _ = start_copy(ltt, ws, open_file=True)
    s = app.press("tab")
    assert s.mode == "EDITOR" and copied(s) > 0
    s = app.press("esc")  # back to the tree only
    assert s.mode == "TREE" and copied(s) > 0
    n = copied(s)
    app.wait(lambda s: copied(s) > n, what="the copy going on")
    app.press("esc")  # in the tree: stops it
    status_is(app, STOPPED)


def test_ctrl_q_during_a_copy_asks_and_esc_keeps_copying(ltt, ws):
    app, _ = start_copy(ltt, ws)
    s = app.press("ctrl+q")
    assert s.mode == "CONFIRM" and s.question == QUIT_QUESTION
    s = app.press("esc")
    assert s.mode == "TREE" and copied(s) > 0
    n = copied(s)
    app.wait(lambda s: copied(s) > n, what="the copy going on")
    app.press("esc")
    status_is(app, STOPPED)
    assert app.alive


def test_q_at_the_question_stops_the_copy_and_quits(ltt, ws):
    app, _ = start_copy(ltt, ws)
    assert app.quit("q") == 0
    assert 0 < count_files(ws.path("dst/big")) < TOTAL  # what is left: a partial copy


def test_unsaved_tabs_are_still_asked_about_and_esc_keeps_copying(ltt, ws):
    app, _ = start_copy(ltt, ws, open_file=True)
    app.press("tab")
    app.type("X")
    app.press("esc")
    s = app.press("ctrl+q")
    assert s.question == QUIT_QUESTION
    s = app.press("q")
    assert s.mode == "CONFIRM" and s.question == "Unsaved tabs: 1!  S saves all · D quits without saving · Esc cancels"
    s = app.press("esc")
    assert s.mode == "TREE" and app.alive
    n = copied(s)
    assert n > 0
    app.wait(lambda s: copied(s) > n, what="the copy going on")
    app.press("esc")
    status_is(app, STOPPED)


def test_selection_moved_during_a_copy_stays_where_it_was_moved(ltt, ws):
    app, _ = start_copy(ltt, ws)
    s = app.press("up")  # off dst, to the last folder of big
    moved_to = s.tree_selected
    assert moved_to.name != "dst"
    app.press("esc")
    s = status_is(app, STOPPED)
    assert s.tree_selected.name == moved_to.name and s.tree_selected.depth == moved_to.depth


def test_stopped_move_across_disks_keeps_the_original(ws, other_disk, ltt):
    src = ws.path("proj/big")
    for d in range(50):  # 10,000 files of its own: a move changes the source
        os.makedirs(os.path.join(src, f"d{d:02}"))
        for f in range(200):
            with open(os.path.join(src, f"d{d:02}", f"f{f:03}.txt"), "w") as fh:
                fh.write(f"{d} {f}\n")
    app = ltt(ws.path("proj"), other_disk, cols=160)
    select(app, "proj/big")
    app.press("ctrl+x")
    select(app, "C")
    app.press("ctrl+v")
    moving = re.compile(r"^Moving big/ into C/: (\d+) files so far · Esc in the tree stops it$")
    s = app.wait(lambda s: moving.match(s.help) or s.status.startswith("Move error"), what="the move under way")
    assert moving.match(s.help), s.status
    app.press("esc")
    s = status_is(app, "Stopped: the copy in C/ is incomplete. The original stays.")
    assert count_files(src) == 10_000
    assert 0 < count_files(os.path.join(other_disk, "big")) < 10_000
    assert s.help.startswith("Ctrl+V moves big/ here")
    assert app.quit() == 0
