"""The terminal around ltt: its modes on and off, signals and exit codes, the size of the window,
and what file content may never do to it (D12)."""
import base64
import os
import re
import signal
import threading
import time

import pytest

from harness import shared
from startup_helpers import (ENTER, FRAME_END, FRAME_START, LEAVE, SIGNALS, foreign_controls, fresh_termios,
                             is_raw, session_output, shell_status)


def leave(app, way):
    """End the session one way; returns the exit code."""
    if way == "ctrl+q":
        return app.quit()
    if way == "ctrl+q, d":
        app.type("X")
        return app.quit("d")
    app.signal(SIGNALS[way])
    return app.wait_exit()


def after_last_frame(out):
    return out[out.rfind(FRAME_END) + len(FRAME_END):]


# ---------------------------------------------------------------- modes on and off

def test_start_takes_over_the_terminal(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    out = app.output
    assert out.count(ENTER) == 1 and out.find(ENTER) < out.find(FRAME_START)
    assert is_raw(app.tty_settings())  # no echo, no line editing, no signal keys, no XON/XOFF


@pytest.mark.parametrize("way", ["ctrl+q", "ctrl+q, d", "SIGTERM", "SIGINT", "SIGHUP"])
def test_leaving_restores_the_terminal(ltt, ws, way):  # D7, V14
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    leave(app, way)
    assert after_last_frame(app.output).startswith(LEAVE)
    assert app.tty_settings() == fresh_termios()


@pytest.mark.parametrize("way, code", [("ctrl+q", 0), ("ctrl+q, d", 0), ("SIGTERM", 143), ("SIGINT", 130),
                                       ("SIGHUP", 129)])
def test_exit_code(ltt, ws, way, code):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    assert leave(app, way) == code


def test_closed_terminal_exits_with_129(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.hang_up()
    assert app.wait_exit() == 129
    assert ws.recovery_files() == []  # nothing unsaved, nothing to keep


@pytest.mark.parametrize("way", ["ctrl+q", "ctrl+q, d", "SIGTERM"])
def test_nothing_is_printed_after_a_session_without_unsaved_edits(ltt, ws, way):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    leave(app, way)
    assert app.output.endswith(LEAVE)
    assert ws.recovery_files() == []


@pytest.mark.parametrize("name", ["SIGTERM", "SIGINT", "SIGHUP"])
def test_signal_with_unsaved_edits_leaves_a_note_on_the_shell_screen(ltt, ws, name):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.type("X")
    app.wait(lambda s: s.title_dirty, what="dirty")
    app.signal(SIGNALS[name])
    assert app.wait_exit() == 128 + SIGNALS[name]
    out = app.output
    note = out[out.rfind(LEAVE) + len(LEAVE):]  # after the alternate screen is gone
    assert note.decode() == (f"ltt stopped (signal {name}): 1 unsaved tab(s) saved to {ws.recovery}; "
                             "they reopen on the next start.\r\n")
    assert len(ws.recovery_files()) == 1


def test_note_counts_the_unsaved_tabs(ltt, ws):
    ws.write("a.txt", "alpha\n")
    ws.write("b.txt", "beta\n")
    ws.write("c.txt", "gamma\n")
    app = ltt(ws.path("a.txt"), ws.path("b.txt"), ws.path("c.txt"))
    app.type("X")
    app.press("ctrl+pgdn")
    app.type("Y")
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    assert b"ltt stopped (signal SIGTERM): 2 unsaved tab(s) saved to " in after_last_frame(app.output)
    assert len(ws.recovery_files()) == 2


@pytest.mark.xfail(strict=True, reason="BUG: SIGQUIT kills ltt with the terminal left raw, on the alternate screen, "
                                       "the mouse and kitty keys on")
def test_sigquit_restores_the_terminal(ltt, ws):  # micro's handled signals, Neovim's signal_spec
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.signal(signal.SIGQUIT)
    assert app.wait_exit() == 131
    assert after_last_frame(app.output).startswith(LEAVE)
    assert app.tty_settings() == fresh_termios()


@pytest.mark.xfail(strict=True, reason="BUG: after a stop and a continue, quitting leaves the terminal raw "
                                       "(the runtime puts back its own snapshot of the settings, taken in raw mode)")
def test_quit_after_a_stop_and_a_continue_restores_the_terminal(ltt, ws):  # Neovim's tui_spec 'suspending'
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.signal(signal.SIGSTOP)
    app.signal(signal.SIGCONT)
    time.sleep(0.5)  # on purpose: the runtime takes SIGCONT on a thread of its own
    assert app.quit() == 0
    assert app.tty_settings() == fresh_termios()


@pytest.mark.xfail(strict=True, reason="BUG: after a stop and a continue, ltt neither sets its terminal modes again "
                                       "nor repaints, so whatever ran in the meantime stays on the screen")
def test_continue_after_a_stop_sets_the_modes_again_and_repaints(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    mark = app.output_mark()
    app.signal(signal.SIGSTOP)
    app.signal(signal.SIGCONT)
    app.wait_output(b"\x1b[2J", after=mark)
    assert ENTER in app.output_since(mark)
    assert app.snap().editor_lines[0] == "alpha"


def test_a_resize_while_stopped_is_drawn_once_continued(ltt, ws):  # Neovim's 'handles suspended PTY process'
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    before = app.frames
    app.signal(signal.SIGSTOP)
    app.resize(80, 24, settle=False)
    app.signal(signal.SIGCONT)
    s = app.wait(lambda s: s.frame_no > before and (s.cols, s.rows) == (80, 24) and s.lines[1][s.left_w] == "│",
                 what="an 80x24 frame")
    assert s.editor_lines[0] == "alpha"


def test_rapid_resizes_down_to_nothing_and_back(ltt, ws):  # Neovim's tui_spec 'rapid resize'
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    for cols, rows in [(4, 44), (44, 1), (1, 1), (1, 4), (0, 0)]:
        app.resize(cols, rows, settle=False)
    s = app.resize(57, 17)
    assert app.alive and s.editor_lines[0] == "alpha"


def test_ctrl_c_never_ends_the_session(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    s = app.press("ctrl+c")  # raw mode: a byte, not SIGINT
    assert app.alive and s.status == "Ctrl+C copies a selection. Ctrl+Q quits."


@pytest.fixture(scope="module")
def huge_file():
    """256 MiB of short lines: opening it keeps ltt busy before its first frame for a good while."""
    with shared("startup-huge-256mib") as (path, ready):
        f = os.path.join(path, "huge.txt")
        if not ready:
            block = (b"x" * 19 + b"\n") * 65536
            with open(f, "wb") as out:
                for _ in range(256 * 1024 * 1024 // len(block)):
                    out.write(block)
    return f


@pytest.mark.slow
@pytest.mark.parametrize("name", ["SIGTERM", "SIGINT", "SIGHUP"])
def test_signal_during_startup_restores_the_terminal(ltt, huge_file, name):  # V13
    app = ltt(huge_file, ready=False)
    app.wait_output(ENTER, timeout=20)  # raw mode and the modes are on, the file is still being read
    app.signal(SIGNALS[name])
    code = app.wait_exit()
    out = app.output
    assert FRAME_END not in out, "the signal came after the first frame: this checks nothing"
    assert shell_status(code) == 128 + SIGNALS[name]  # the signal itself ends the process
    assert out.endswith(ENTER + LEAVE)
    assert app.tty_settings() == fresh_termios()


def test_signal_while_the_ui_is_stuck_still_restores_the_terminal(ltt, ws):  # V13
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.type("X")
    app.wait(lambda s: s.title_dirty, what="dirty")
    # A paste that never ends keeps the UI thread reading: it cannot answer the signal in time.
    app.write(b"\x1b[200~endless paste ")
    stop = threading.Event()

    def trickle():
        while not stop.is_set():
            try:
                app.write(b".")
            except OSError:
                return
            stop.wait(0.3)

    feeder = threading.Thread(target=trickle, daemon=True)
    feeder.start()
    try:
        app.signal(signal.SIGTERM)
        code = app.wait_exit(timeout=15)
    finally:
        stop.set()
        feeder.join()
    assert shell_status(code) == 143
    assert app.output.endswith(LEAVE)
    assert app.tty_settings() == fresh_termios()
    assert len(ws.recovery_files()) == 1  # saved from the signal's own thread


def test_scrollback_is_never_wiped(ltt, ws):  # T2
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.resize(80, 24)
    app.press("ctrl+l", "esc")  # Ctrl+L repaints the whole screen
    app.resize(100, 30)
    assert app.quit() == 0
    out = app.output
    assert b"\x1b[3J" not in out
    alt_on, alt_off = out.find(b"\x1b[?1049h"), out.rfind(b"\x1b[?1049l")
    clears = [m.start() for m in re.finditer(re.escape(b"\x1b[2J"), out)]
    assert len(clears) >= 3 and all(alt_on < c < alt_off for c in clears)  # only the alternate screen is cleared


def test_every_frame_is_one_synchronized_update(ltt, ws):
    ws.write("proj/a.txt", "alpha\nbeta\n")
    ws.write("proj/b.txt", "gamma\n")
    app = ltt(ws.path("proj"))
    app.press("down", "enter")
    app.type("typed ")
    app.press("shift+down", "esc", "down", "enter", "ctrl+f")
    app.type("ga")
    app.wait(lambda s: s.results, what="hits")
    app.press("esc", "esc")
    app.resize(70, 20)
    app.resize(100, 30)
    app.quit("d")
    out = app.output
    body = out[out.find(ENTER) + len(ENTER):out.rfind(LEAVE)]
    pieces = body.split(FRAME_END)
    assert pieces[-1] == b""  # nothing after the last frame but the restore
    for piece in pieces[:-1]:  # nothing between frames, no frame inside another
        assert piece.startswith(FRAME_START) and FRAME_START not in piece[len(FRAME_START):]


def test_cursor_hidden_in_the_tree_and_shown_at_the_edit_point(ltt, ws):
    ws.write("a.txt", "alpha\nbeta\n")
    app = ltt(ws.path("a.txt"))
    s = app.press("down", "end")
    assert not s.cursor_hidden and s.cursor == (s.text_x + 4, 3)
    s = app.press("esc")
    assert s.cursor_hidden


def test_incremental_frames_match_a_full_repaint(ltt, ws):
    # Frames send only the cells that changed: after each step the screen must equal
    # what a full repaint (Ctrl+L) draws — no stale cells, wide characters included.
    ws.write("proj/wide.txt", "日本語のテキスト text\n漢字 ⚡ émoji 👀 end\n\tx = find(me);\n"
             + "".join(f"line {i} 中文\n" for i in range(60)))
    ws.write("proj/sub/日本.txt", "名前\n")
    app = ltt(ws.path("proj"), ws.path("proj/wide.txt"))

    def same_as_full_repaint(step):
        s = app.snap()
        full = app.press("ctrl+l")  # its prompt changes only the status and help lines
        for y in range(s.status_y):
            assert s.cells[y] == full.cells[y], f"after {step}: row {y} differs from a full repaint"
        app.press("esc")

    same_as_full_repaint("the first frame")
    app.press("home")
    app.type("日本")
    same_as_full_repaint("typing wide characters")
    app.press("backspace", "backspace", "backspace")
    same_as_full_repaint("deleting them")
    app.press("down", "end")
    app.type("👀👀")
    same_as_full_repaint("typing emoji")
    app.press("pgdn")
    same_as_full_repaint("a page down")
    app.press("esc", "home", "down", "enter", "down", "enter")
    same_as_full_repaint("opening a second tab from the tree")
    app.press("ctrl+pgdn")
    same_as_full_repaint("switching tabs")
    app.press("ctrl+w")
    same_as_full_repaint("closing a tab")
    app.press("ctrl+f")
    app.type("中文")
    app.wait(lambda s: s.results, what="hits")
    app.press("down", "enter")
    same_as_full_repaint("opening a hit with the results panel open")


def test_ctrl_l_repaints_the_whole_screen(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    mark = app.output_mark()
    s = app.press("ctrl+l")
    assert b"\x1b[0m\x1b[2J" in app.output_since(mark)
    assert s.prompt.startswith("Go to:") and s.editor_lines[0] == "alpha"


# ---------------------------------------------------------------- window size

@pytest.mark.parametrize("cols, rows", [(41, 10), (42, 9), (20, 5)])
def test_too_small_terminal_shows_only_a_message(ltt, ws, cols, rows):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=cols, rows=rows)
    s = app.snap()
    assert s.lines[0] == " Terminal too small (need at least 42x10)."[:cols].ljust(cols)
    assert (s.fg(1, 0), s.bg(1, 0)) == ("White", "DarkRed")
    assert all(not line.strip() for line in s.lines[1:])


def test_smallest_size_shows_the_editor(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=42, rows=10)
    s = app.snap()
    assert not s.too_small
    assert [x for x, ch in enumerate(s.lines[2]) if ch == "│"] == [22]
    assert s.editor_lines[0] == "alpha" and s.status == "Tab: a.txt"


def test_enlarging_a_too_small_terminal_brings_everything_back(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    app.type("X")
    assert app.resize(30, 8).too_small
    s = app.resize(100, 30)
    assert s.mode == "EDITOR" and s.tab_names == ["a.txt"] and s.editor_lines[0] == "Xalpha" and s.title_dirty


def test_starting_too_small_then_enlarging(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=40, rows=8)
    assert app.snap().too_small
    s = app.resize(80, 24)
    assert s.tab_names == ["a.txt"] and s.editor_lines[0] == "alpha" and s.left_w == 25


def test_keys_still_work_while_too_small(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=30, rows=8)
    app.type("X")
    app.press("ctrl+s")
    app.wait(lambda s: ws.read("a.txt") == "Xalpha\n", what="saved while too small")
    assert app.snap().too_small
    assert app.quit() == 0


@pytest.mark.parametrize("cols, left", [(100, 32), (80, 25), (60, 22), (46, 22), (200, 64)])
def test_resize_lays_the_panels_out_again(ltt, ws, cols, left):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), cols=120, rows=30)
    n = app.frames
    app.resize(cols, 20, settle=False)
    s = app.wait(lambda s: s.frame_no > n and (s.cols, s.rows) == (cols, 20), what=f"a {cols}x20 frame")
    for y in range(1, 18):  # the divider runs from the tab bar to the last content row
        assert s.lines[y].index("│") == left
    assert s.lines[1][left + 1:].startswith(" a.txt ×")
    assert s.lines[2][left + 1:].startswith("  1 alpha")
    assert s.status_line.startswith(" [EDITOR] Tab: a.txt") and s.help.startswith("Esc to tree")


def test_resize_away_and_back_within_one_tick_repaints_everything(ltt, ws):  # V12
    ws.write("proj/f.txt", "hello\nworld\n")
    app = ltt(ws.path("proj"), ws.path("proj/f.txt"))
    before = app.snap()
    n = app.frames
    app.resize(60, 20, settle=False)
    app.resize(100, 30, settle=False)  # back before ltt looks at the size again
    # the harness's screen lost what lay outside 60x20: only a full repaint brings it back
    app.wait(lambda s: s.frame_no > n and s.lines == before.lines, what="a full repaint at the old size")


@pytest.mark.parametrize("rows, top, hits", [(50, 38, 9), (30, 19, 8), (20, 12, 5), (12, 6, 3), (10, 5, 2)])
def test_resize_gives_the_results_panel_its_share(ltt, ws, rows, top, hits):
    # about 30% of the height, 4 to 10 rows; at the smallest height the editor keeps 3 rows
    ws.write("proj/a.txt", "".join(f"needle {i}\n" for i in range(60)))
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: len(s.results) == 8, what="hits")
    n = app.frames
    app.resize(100, rows, settle=False)
    s = app.wait(lambda s: s.frame_no > n and s.rows == rows, what=f"a 100x{rows} frame")
    assert s.results_top == top and len(s.results) == hits
    assert len(s.editor_rows) == top - 2 and s.prompt.startswith("Search file: needle")


def test_open_prompt_survives_a_resize(ltt, ws):
    ws.write("big.txt", "".join(f"line {i}\n" for i in range(1, 51)))
    app = ltt(ws.path("big.txt"))
    app.press("ctrl+l")
    app.type("12")
    s = app.resize(80, 20)
    assert s.status_line.startswith(" Go to: 12")
    s = app.press("enter")
    assert s.pos[0] == 12 and s.status == "Line 12 of 51."


def test_shrinking_keeps_the_cursor_line_in_view(ltt, ws):
    ws.write("big.txt", "".join(f"line {i}\n" for i in range(1, 101)))
    app = ltt(ws.path("big.txt") + ":25", rows=40)
    assert app.snap().editor_row(25).current
    s = app.resize(100, 12)
    row = s.editor_row(25)
    assert row is not None and row.current and row.text == "line 25"


# ---------------------------------------------------------------- what files may never do (D12)

def test_control_characters_in_a_file_show_as_symbols(ltt, ws):  # D12
    ws.write("a.txt", "esc\x1b[2Jbel\x07del\x7fff\x0cvt\x0bbs\x08end\na\tb\n")
    app = ltt(ws.path("a.txt"))
    s = app.snap()
    assert s.editor_lines[:2] == ["esc␛[2Jbel␇del␡ff␌vt␋bs␈end", "a   b"]  # a tab is drawn as spaces
    assert foreign_controls(session_output(app)) == []


def test_escape_sequences_in_highlighted_code_never_reach_the_terminal(ltt, ws):  # D12
    ws.write("t.cs", "int a = 1; // \x1b]0;PWNED\x07 comment\n"
                     "var s = \"\x1b[2J\x07\";\n"
                     "/* \x1b[?1049l */ int b;\n")
    app = ltt(ws.path("t.cs"))
    s = app.snap()
    assert s.editor_lines[:3] == ["int a = 1; // ␛]0;PWNED␇ comment", 'var s = "␛[2J␇";', "/* ␛[?1049l */ int b;"]
    x, y = s.find("␛]0")
    assert s.fg(x, y) == s.fg(x - 3, y) != s.fg(s.text_x, y)  # drawn as part of the comment's colour run
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""


def test_escape_sequences_in_a_file_never_reach_the_terminal(ltt, ws):  # D12
    ws.write("evil.txt", "a \x1b]0;PWNED\x07 b \x1b]2;PWNED2\x1b\\ c\n"
                         "d \x1b]52;c;SGFja2Vk\x07 e\n"
                         "f \x1b[?1049l\x1b[3J\x1bc g\n"
                         "h \x1b]11;?\x07 \x1b[6n \x1b[c i\n")
    app = ltt(ws.path("evil.txt"))
    s = app.snap()
    assert s.editor_lines[:4] == ["a ␛]0;PWNED␇ b ␛]2;PWNED2␛\\ c", "d ␛]52;c;SGFja2Vk␇ e",
                                  "f ␛[?1049l␛[3J␛c g", "h ␛]11;?␇ ␛[6n ␛[c i"]
    app.press("down", "down", "down", "pgdn")  # scroll it all through once more
    out = session_output(app)
    assert foreign_controls(out) == []
    assert b"\x1b]" not in out  # no OSC at all: no title, no clipboard write, no colour query
    assert app.snap().window_title == ""


def test_c1_controls_in_a_file_show_as_replacement_characters(ltt, ws):  # D12
    ws.write("c1.txt", "csi\u009b31m osc\u009d0;T\u0007 nel\u0085 end\n")
    app = ltt(ws.path("c1.txt"))
    assert app.snap().editor_lines[0] == "csi�31m osc�0;T␇ nel� end"
    assert foreign_controls(session_output(app)) == []


def test_c1_bytes_in_a_latin1_file_show_as_replacement_characters(ltt, ws):  # D12
    ws.write("l1.txt", b"csi \x9b?25l osc \x9d0;T\x07 \xe9t\xe9\n")
    app = ltt(ws.path("l1.txt"))
    assert app.snap().editor_lines[0] == "csi �?25l osc �0;T␇ été"
    assert foreign_controls(session_output(app)) == []


NAMES = {
    "esc\x1b]0;PWNED\x07.txt": "esc␛]0;PWNED␇.txt",
    "bel\x07\x1b[?1049l.txt": "bel␇␛[?1049l.txt",
    "c1\u009b31m.txt": "c1�31m.txt",
    "del\x7fx.txt": "del␡x.txt",
    "nl\nname\r.txt": "nl␊name␍.txt",
}


def test_control_characters_in_file_names_show_as_symbols(ltt, ws):  # D12
    for name in NAMES:
        ws.write("inj/" + name, "x\n")
    app = ltt(ws.path("inj"))
    s = app.snap()
    assert sorted(s.tree_names[1:]) == sorted(NAMES.values())
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""


def test_control_characters_in_a_name_show_in_the_tab_and_title(ltt, ws):  # D12
    evil = ws.write(os.path.join(ws.home, "t\x1b]0;PWNED\x07.txt"), "x\n")
    plain = ws.write(os.path.join(ws.home, "u.txt"), "u\n")
    app = ltt(evil, plain)
    s = app.snap()
    assert s.tab_names == ["t␛]0;PWNED␇.txt", "u.txt"]
    assert s.title == "ltt · ~/t␛]0;PWNED␇.txt [tab 1/2]"
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""


def test_control_characters_in_a_name_show_in_status_messages(ltt, ws):  # D12
    raw = ws.write("proj/s\x1b[2J\x07.txt", "one\n")
    app = ltt(ws.path("proj"), cols=len(raw) + 60)
    s = app.press("down", "enter")
    assert s.status == "Opened s␛[2J␇.txt (2 lines), tab 1/1. Ctrl+W closes."
    mark = app.output_mark()
    s = app.click(10, 0)  # a click on the title copies its path
    assert s.status == "Copied path: " + raw.replace("\x1b", "␛").replace("\x07", "␇")
    # the raw path goes to the clipboard only base64-encoded
    assert b"\x1b]52;c;" + base64.b64encode(raw.encode()) + b"\x07" in app.output_since(mark)
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""


def test_control_characters_in_search_results_show_as_symbols(ltt, ws):  # D12
    ws.write("proj/c.txt", "needle \x1b]0;PWNED\x07 here\n")
    ws.write("proj/n\x1b[31m.txt", "needle in the name\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    app.type("needle")
    s = app.wait(lambda s: len(s.results) == 2, what="two hits")
    assert sorted((h.location, h.snippet) for h in s.results) == [
        ("c.txt:1", "needle ␛]0;PWNED␇ here"), ("n␛[31m.txt:1", "needle in the name")]
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""


def test_control_characters_pasted_into_a_prompt_show_as_symbols(ltt, ws):  # D12
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = app.paste("al\x1b]0;PWNED\x07pha")
    assert s.prompt.startswith("Search folder: al␛]0;PWNED␇pha")
    assert foreign_controls(session_output(app)) == [] and s.window_title == ""
