"""Timings, bytes and memory: ltt stays quick on big folders, big files and big pastes.

Time limits are generous — several times what a quiet machine measures (noted
next to each) — and stretch with LTT_TEST_TIME_SCALE; ./run runs these tests
alone, one at a time. A busy machine slows some runs, not all: typing speed is
the best of a few keys, the time to the first frame the best of a few starts.
"""
import os
import re
import shutil
import time

import pytest

from harness import keys as K
from perf_helpers import (MILLION, PLAIN, answer, big_repo, first_frame, million_js, million_line, million_log,
                          minified_js, quickest_start, rss_mb, runs, secs, sent, tree)

pytestmark = pytest.mark.perf


def best_of(app, key, pred_for, times=3):
    """The quickest of a few answers to the same key; pred_for(frame before) -> pred for the answer."""
    best = None
    for _ in range(times):
        before = app.snap()
        t, _ = answer(app, key, pred_for(before))
        app.settle()
        best = t if best is None else min(best, t)
    return best


def typed_one_more(before):
    return lambda s: s.pos is not None and s.pos[2] == before.pos[2] + 1


# ---------------------------------------------------------------- time to the first frame

def test_first_frame_of_a_small_folder(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/docs/b.md", "# b\n")
    t, app = quickest_start(ltt, ws.path("proj"), limit=secs(2.0))
    assert first_frame(app).tree_names == ["proj", "docs", "a.txt"]
    assert t < secs(2.0), f"first frame after {t:.3f} s"  # ~0.2 s


def test_first_frame_of_a_folder_of_20000_files(ltt):
    t, app = quickest_start(ltt, tree() + "/flat", env=PLAIN, limit=secs(3.0))
    assert first_frame(app).tree_names[:3] == ["flat", "f00000.txt", "f00001.txt"]
    assert t < secs(3.0), f"first frame after {t:.3f} s"  # ~0.26 s


def test_first_frame_does_not_wait_for_folder_sizes(ltt):
    # 20,000 files in 100 folders: the first frame comes before they are counted
    t, app = quickest_start(ltt, tree() + "/nested", env=PLAIN, limit=secs(2.0))
    sizes = [r.size for r in first_frame(app).tree[1:]]
    assert any(size == "…" or size.startswith("≥") for size in sizes), sizes
    assert t < secs(2.0), f"first frame after {t:.3f} s"  # ~0.2 s
    app.wait(lambda s: all(r.size[:1].isdigit() for r in s.tree[1:]), timeout=30, what="every folder counted")


def test_first_frame_of_a_big_git_repository(ltt, ws):
    t, app = quickest_start(ltt, big_repo(ws), limit=secs(3.0))
    assert first_frame(app).tree_names[:2] == ["perf-repo", "src"]
    assert t < secs(3.0), f"first frame after {t:.3f} s"  # ~0.2 s


def test_first_frame_with_a_million_line_file(ltt):
    t, app = quickest_start(ltt, million_js(), env=PLAIN, limit=secs(3.0))  # the file is read whole before it
    s = first_frame(app)
    assert s.pos == (1, MILLION, 1, 0) and s.editor_lines[0] == million_line(1)
    assert t < secs(3.0), f"first frame after {t:.3f} s"  # ~0.3 s


# ---------------------------------------------------------------- bytes per action (U6)

CODE = "namespace Demo;\n\npublic sealed class Big\n{\n" + "".join(
    f"    public int Method{i:03d}(int value) => value * {i} + Offset; // computes the {i}th value of the series\n"
    for i in range(400)) + "}\n"


def code_session(ltt, ws):
    """400 coloured lines of C# in a 160x50 terminal, settled."""
    ws.write("proj/Big.cs", CODE)
    app = ltt(ws.path("proj/Big.cs"), cols=160, rows=50)
    app.settle()
    return app


def test_typing_a_character_sends_only_the_changed_cells(ltt, ws):
    # U6: every frame used to redraw the whole screen
    app = code_session(ltt, ws)
    whole = app.output_mark()  # the first frame: ~12.8 KB
    out = sent(app, "x", lambda s: s.pos[2] == 2)
    assert whole > 10_000 and len(out) < 400, out  # ~140 bytes: the line, the title and tab ● marks, the column


def test_cursor_down_sends_two_line_numbers_and_the_status(ltt, ws):
    app = code_session(ltt, ws)
    s = app.snap()
    out = sent(app, "down", lambda s: s.pos[0] == 2)
    assert {y for _, y in runs(out)} == {s.editor_row(1).y, s.editor_row(2).y, s.status_y}, out
    assert len(out) < 300, out  # ~100 bytes


def test_a_key_that_changes_nothing_sends_no_cells(ltt, ws):
    app = code_session(ltt, ws)
    out = sent(app, "left")  # already at line 1, column 1
    assert re.fullmatch(rb"\x1b\[\?2026h\x1b\[\?25l(\x1b\[\d+;\d+H\x1b\[\?25h)?\x1b\[\?2026l", out), out


def test_page_down_sends_the_editor_rows_only(ltt, ws):
    app = code_session(ltt, ws)
    s = app.snap()
    out = sent(app, "pgdn", lambda s: s.pos[0] > 40)
    cells = runs(out)
    assert cells and all(x >= s.editor_x and 2 <= y <= s.status_y for x, y in cells), cells  # ~8 KB


def test_wheel_notch_sends_the_editor_rows_only(ltt, ws):
    app = code_session(ltt, ws)
    s = app.snap()
    out = sent(app, K.mouse(K.WHEEL_DOWN, 100, 20), lambda s: s.editor_rows[0].number > 1)
    cells = runs(out)
    assert cells and all(x >= s.editor_x and 2 <= y < s.status_y for x, y in cells), cells


def test_burst_of_wheel_notches_is_drawn_once(ltt, ws):
    # a trackpad sends dozens of wheel events at once: one frame answers them all
    app = code_session(ltt, ws)
    n = app.frames
    app.write(K.mouse(K.WHEEL_DOWN, 100, 20) * 50)
    app.wait(lambda s: s.editor_rows[0].number == 61, what="60 lines scrolled")  # 6/5 of a line a notch
    app.settle()
    assert app.frames == n + 1


def test_fast_typing_is_drawn_once(ltt, ws):
    app = code_session(ltt, ws)
    n = app.frames
    app.type("typed faster than a frame")  # one burst of 25 keys
    assert app.snap().editor_lines[0].startswith("typed faster than a frame")
    assert app.frames == n + 1


def test_idle_session_sends_nothing(ltt, ws):
    app = code_session(ltt, ws)
    mark = app.output_mark()
    time.sleep(1.5)  # on purpose: nothing may be drawn while nothing happens
    assert app.output_since(mark) == b""


def test_idle_big_repository_sends_nothing(ltt, ws):
    # once git has answered and the sizes are counted, nothing polls and redraws
    app = ltt(big_repo(ws))
    app.wait(lambda s: s.tree_row("src").fg == "Yellow" and all(r.size[:1].isdigit() for r in s.tree[1:]),
             timeout=30, what="git status and sizes")
    app.settle()
    mark = app.output_mark()
    time.sleep(1.5)  # on purpose: nothing may be drawn while nothing happens
    assert app.output_since(mark) == b""


def test_ctrl_l_repaints_the_whole_screen(ltt, ws):
    # the one deliberate full repaint: it mends a garbled screen
    app = code_session(ltt, ws)
    out = sent(app, "ctrl+l", lambda s: s.mode == "PROMPT")
    assert b"\x1b[2J" in out and len(out) > 10_000


# ---------------------------------------------------------------- a million lines

def million_session(ltt):
    return ltt(million_js(), env=PLAIN)


def at_last_line(s):
    return s.pos is not None and s.pos[0] == MILLION


def test_opening_a_million_line_file_from_the_tree(ltt):
    app = ltt(os.path.dirname(million_js()), env=PLAIN)
    app.press("down")  # million.js
    t, s = answer(app, "enter", lambda s: s.pos is not None)
    assert s.pos == (1, MILLION, 1, 0) and s.editor_lines[0] == million_line(1)
    assert t < secs(2.0), f"opened after {t:.3f} s"  # ~0.15 s


def test_saving_a_million_line_file(ltt, ws):
    shutil.copyfile(million_js(), ws.path("million.js"))  # a copy: the shared file stays as it is
    app = ltt(ws.path("million.js"))
    app.press("ctrl+end")
    app.type("x")
    t, s = answer(app, "ctrl+s", lambda s: s.status.startswith("Saved"))
    assert s.status == "Saved: million.js" and not s.title_dirty
    assert t < secs(3.0), f"saved after {t:.3f} s"  # ~0.2 s
    assert os.path.getsize(ws.path("million.js")) == os.path.getsize(million_js()) + 1


def test_ctrl_end_in_a_million_lines(ltt):
    app = million_session(ltt)
    t, s = answer(app, "ctrl+end", at_last_line)
    assert s.editor_lines[-1] == million_line(MILLION)
    assert t < secs(1.5), f"Ctrl+End answered after {t:.3f} s"  # ~0.2 s: the comment state of every line, once


def test_typing_at_the_end_of_a_million_lines(ltt):
    app = million_session(ltt)
    app.press("ctrl+end")
    t = best_of(app, "x", typed_one_more)
    assert t < secs(0.1), f"best key answered after {t:.3f} s"  # ~5 ms


def test_undo_at_the_end_of_a_million_lines_keeps_the_comment_state(ltt):
    # D10: undo and redo threw away the comment state of every line (~0.6 s each near the end)
    app = million_session(ltt)
    whole, _ = answer(app, "ctrl+end", at_last_line)  # works out the state of all lines once
    undo = redo = None
    for _ in range(3):
        app.press("x")
        t, _ = answer(app, "ctrl+z", lambda s: not s.title_dirty)
        undo = t if undo is None else min(undo, t)
        t, _ = answer(app, "ctrl+y", lambda s: s.title_dirty)
        redo = t if redo is None else min(redo, t)
        app.press("ctrl+z")
    limit = max(whole / 4, secs(0.05))  # ~3 ms each against ~200 ms for all the lines
    assert undo < limit and redo < limit, f"undo {undo:.3f} s, redo {redo:.3f} s, all lines {whole:.3f} s"


def test_go_to_a_line_in_a_million_lines(ltt):
    app = million_session(ltt)
    app.press("ctrl+l")
    app.type("500000")
    t, s = answer(app, "enter", lambda s: s.pos[0] == 500_000)
    assert t < secs(1.0), f"answered after {t:.3f} s"  # ~0.1 s: the comment state of the 500,000 lines above, once


def test_search_in_an_open_million_line_file(ltt):
    app = million_session(ltt)
    app.press("ctrl+f")
    app.type("z")
    t, s = answer(app, "e", lambda s: "found:" in s.status_line)  # the open buffer is searched at once
    assert "found: 4" in s.status_line
    assert t < secs(1.5), f"found after {t:.3f} s"  # ~0.1 s


def test_refining_a_search_in_a_million_lines_is_instant(ltt):
    app = million_session(ltt)
    app.press("ctrl+f")
    app.type("ze")
    app.wait_status("found: 4")
    t, s = answer(app, "b")
    assert "found: 4" in s.status_line
    assert t < secs(0.1), f"refined after {t:.3f} s"  # ~1 ms: only the 4 hits are looked at


def test_searching_an_unopened_million_line_file_keeps_answering_keys(ltt):
    # V25: searching a large file that is not open froze the UI
    app = ltt(os.path.dirname(million_js()), env=PLAIN)
    app.press("down")  # million.js, not opened
    app.press("ctrl+f")
    app.type("z")
    t, s = answer(app, "e")
    assert "searching…" in s.status_line and t < secs(0.2), (t, s.status_line)
    t, s = answer(app, "down")
    assert t < secs(0.2), f"a key during the search answered after {t:.3f} s"  # ~1 ms
    app.wait(lambda s: "found: 4" in s.status_line, timeout=30, what="the four hits")  # ~0.2 s


def test_undo_history_of_a_million_lines_stays_small(ltt):
    # D8: every undo step copied the whole line array — 200 edits held +1.5 GB
    app = million_session(ltt)
    app.settle()
    before = rss_mb(app.pid)
    app.write((K.encode("down") + b"#") * 200)  # 200 edits, each on its own line: 200 undo steps
    app.wait(lambda s: s.pos == (201, MILLION, 50, 0), timeout=60, what="200 edits done")  # a # at the end of 200
    app.settle()
    grown = rss_mb(app.pid) - before
    assert grown < 300, f"{grown:.0f} MB more after 200 edits"  # ~10 MB


def test_typing_after_edits_all_over_a_million_lines(ltt):
    # the change marks diff the text against the file after every edit
    app = million_session(ltt)
    app.write(b"".join(b"\x0c%d\r#" % n for n in range(1, MILLION, 10_000)))  # Ctrl+L n, Enter, # — 100 places
    app.wait(lambda s: s.pos[0] == 990_001 and s.title_dirty, timeout=60, what="100 edits done")
    app.settle()
    t = best_of(app, "x", typed_one_more)
    assert t < secs(0.1), f"best key answered after {t:.3f} s"  # ~5 ms


@pytest.mark.xfail(strict=True, reason="BUG: in a big file whose lines repeat, every key re-diffs all the text between "
                                       "the first and the last edited place: ~2.4 ms more per place (100 → 0.24 s a key)")
def test_typing_after_edits_all_over_a_million_repeating_lines(ltt):
    # a log of three repeating lines: the hard case for the diff behind the change marks
    app = ltt(million_log(), env=PLAIN)
    app.write(b"".join(b"\x0c%d\r#" % n for n in range(1, MILLION, 10_000)))  # Ctrl+L n, Enter, # — 100 places
    app.wait(lambda s: s.pos[0] == 990_001 and s.title_dirty, timeout=60, what="100 edits done")
    app.settle(timeout=60)
    t = best_of(app, "x", typed_one_more)
    assert t < 0.1, f"best key answered after {t:.3f} s"  # 2 ms with no edits, 0.24 s after these; not stretched


# ---------------------------------------------------------------- a 3 MB line

def test_typing_at_the_end_of_a_3mb_line(ltt):
    # V10, V15: long lines were measured and tokenized whole on every frame (80 ms per key)
    app = ltt(minified_js(), env=PLAIN)
    app.press("end")
    t = best_of(app, "x", typed_one_more, times=5)
    assert t < secs(0.05), f"best key answered after {t:.3f} s"  # ~4 ms


def test_moving_along_a_3mb_line(ltt):
    app = ltt(minified_js(), env=PLAIN)
    t, _ = answer(app, "end", lambda s: s.pos[2] > 3_000_000)
    assert t < secs(0.3), f"End answered after {t:.3f} s"  # ~20 ms
    t = best_of(app, "left", lambda before: lambda s: s.pos[2] == before.pos[2] - 1, times=5)
    assert t < secs(0.05), f"best key answered after {t:.3f} s"  # ~1 ms


# ---------------------------------------------------------------- a big paste

def test_pasting_5000_lines_lands_in_one_frame(ltt, ws):
    ws.write("notes.txt", "start\n")
    app = ltt(ws.path("notes.txt"))
    text = "".join(f"line {i} of the pasted text, some words here\n" for i in range(5000))  # 229 KB
    n = app.frames
    t0 = time.monotonic()
    app.paste(text, settle=False)
    s = app.wait(lambda s: s.status.startswith("Pasted"), what="the paste")
    assert s.pos == (5001, 5002, 1, 0)
    assert s.at - t0 < secs(0.5), f"pasted after {s.at - t0:.3f} s"  # ~20 ms
    app.settle()
    assert app.frames == n + 1


# ---------------------------------------------------------------- folder search

def small_project(ws):
    """60 files of C# (~480 KB), about the size of ltt's own sources; one line says "zq"."""
    for i in range(60):
        body = "".join(f"    public int Value{k}() => {i * 100 + k}; // computes value {k}\n" for k in range(80))
        ws.write(f"proj/src/File{i:02d}.cs", f"namespace Demo;\n\npublic sealed class File{i:02d}\n{{\n{body}}}\n"
                 + ("// zq marks this one\n" if i == 7 else ""))
    return ws.path("proj")


def test_folder_search_of_a_small_project(ltt, ws):
    app = ltt(small_project(ws))
    app.press("ctrl+f")
    app.type("z")
    t, s = answer(app, "q", lambda s: "found:" in s.status_line)
    assert "found: 1" in s.status_line
    assert t < secs(1.0), f"found after {t:.3f} s"  # ~0.21 s


@pytest.mark.xfail(strict=True, reason="BUG: a folder search done in ~20 ms is shown only at the main loop's next "
                                       "200 ms tick (README: ~40 ms across this repo)")
def test_folder_search_of_a_small_project_shows_its_results_at_once(ltt, ws):
    app = ltt(small_project(ws))
    app.press("ctrl+f")
    app.type("z")
    t, s = answer(app, "q", lambda s: "found:" in s.status_line)
    assert "found: 1" in s.status_line
    assert t < 0.12, f"found after {t:.3f} s"  # not stretched: the tick is 200 ms on any machine


def test_folder_search_across_20000_files(ltt):
    app = ltt(tree() + "/nested", env=PLAIN)
    app.press("ctrl+f")
    app.type("z")
    t, s = answer(app, "q", lambda s: "found:" in s.status_line, timeout=60)
    assert "found: 3" in s.status_line
    assert t < secs(6), f"found after {t:.3f} s"  # ~1 s


def test_refining_a_folder_search_reads_no_files(ltt):
    app = ltt(tree() + "/nested", env=PLAIN)
    app.press("ctrl+f")
    app.type("zq")
    app.wait_status("found: 3", timeout=60)
    t, s = answer(app, "x")  # the very next frame has the refined results
    assert "found: 2" in s.status_line
    assert t < secs(0.1), f"refined after {t:.3f} s"  # ~1 ms


def test_folder_search_in_a_big_git_repository(ltt, ws):
    app = ltt(big_repo(ws))
    app.press("ctrl+f")
    t, s = answer(app, b"Item042", lambda s: "found:" in s.status_line, timeout=60)
    assert "found: 50" in s.status_line
    assert t < secs(3), f"found after {t:.3f} s"  # ~0.2 s


# ---------------------------------------------------------------- the tree

def test_folder_sizes_of_a_big_tree_finish_counting(ltt):
    app = ltt(tree(), env=PLAIN)
    first = first_frame(app)
    assert any(r.size == "…" or r.size.startswith("≥") for r in first.tree), first.dump()
    s = app.wait(lambda s: all(r.size[:1].isdigit() for r in s.tree[1:]), timeout=60, what="sizes counted")
    assert s.at - first.at < secs(5), f"counted after {s.at - first.at:.3f} s"  # ~0.4 s for 40,000 files


def test_end_in_a_folder_of_20000_files(ltt):
    app = ltt(tree() + "/flat", env=PLAIN)
    t, _ = answer(app, "end", lambda s: s.tree_selected.name == "f19999.txt")
    assert t < secs(0.3), f"End answered after {t:.3f} s"  # ~20 ms


def test_expanding_a_folder_of_20000_files(ltt):
    app = ltt(tree(), env=PLAIN)
    app.wait(lambda s: all(r.size[:1].isdigit() for r in s.tree[1:]), timeout=60, what="sizes counted")
    app.press("down", "down")  # flat/, below the bigger nested/
    t, s = answer(app, "right", lambda s: s.tree_row("f00000.txt") is not None)
    assert t < secs(1.0), f"expanded after {t:.3f} s"  # ~0.1 s


def test_expanding_a_folder_of_a_big_git_repository(ltt, ws):
    # W1: git check-ignore ran on the UI thread for every loaded folder
    app = ltt(big_repo(ws))
    app.press("down")  # src/
    t, _ = answer(app, "right", lambda s: s.tree_row("module00") is not None)
    assert t < secs(0.5), f"expanded after {t:.3f} s"  # ~20 ms
