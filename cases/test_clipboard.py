"""The clipboard (Ctrl+C / Ctrl+X / Ctrl+V through pbcopy, pbpaste and OSC 52) and the terminal's bracketed paste."""
import base64
import os
import shutil
import sys
import time

import pytest

from harness import keys as K
from harness import shared
from save_helpers import paste_bytes, save, tool_dir

COPY_TOOL = "pbcopy" if sys.platform == "darwin" else "wl-copy"
PASTE_TOOL = "pbpaste" if sys.platform == "darwin" else "wl-paste"
EMPTY = "Clipboard is empty or cannot be read here — the terminal's own paste still works."


def osc52(text):
    return b"\x1b]52;c;" + base64.b64encode(text.encode()) + b"\x07"


def text_is(app, *lines):
    """Wait until the editor shows exactly these lines (the whole file in view)."""
    return app.wait(lambda s: s.editor_lines == list(lines), what=f"the text {list(lines)!r}")


def running(pid):
    try:
        os.kill(pid, 0)  # signal 0: only asks whether the process is there
    except ProcessLookupError:
        return False
    return True


# ---------------------------------------------------------------- Ctrl+C

def test_ctrl_c_copies_the_selection(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    mark = app.output_mark()
    app.press("shift+end", "ctrl+c")
    s = app.wait_status("Copied to the clipboard: 11 chars")
    assert s.editor_lines == ["hello world", ""] and s.pos[3] == 11 and not s.title_dirty
    app.wait(lambda s: ws.clipboard == "hello world", timeout=5, what="the copy tool got the text")
    assert osc52("hello world") in app.output_since(mark)


def test_ctrl_c_joins_lines_with_lf_even_in_a_crlf_file(ltt, ws):
    app = ltt(ws.write("a.txt", b"one\r\ntwo\r\nthree\r\n"))
    app.press("shift+down", "shift+down", "ctrl+c")
    app.wait_status("Copied to the clipboard: 8 chars")
    app.wait(lambda s: ws.clipboard == "one\ntwo\n", timeout=5, what="two lines on the clipboard")


def test_ctrl_c_copies_non_ascii_text_as_utf8(ltt, ws):
    text = "héllo 日本語 ⚡ 👀 e\u0301\n"
    app = ltt(ws.write("a.txt", text))
    mark = app.output_mark()
    app.press("ctrl+a", "ctrl+c")
    app.wait_status("Copied to the clipboard:")
    app.wait(lambda s: ws.clipboard == text, timeout=5, what="the text on the clipboard")
    assert osc52(text) in app.output_since(mark)


@pytest.mark.skipif(sys.platform != "darwin", reason="pbcopy/pbpaste pick their encoding from the locale on macOS")
def test_the_clipboard_tools_run_with_a_utf8_locale(ltt, ws):  # C1
    log, clip = ws.clip_log, ws.clip_file
    bin_dir = tool_dir(ws, "bin-locale", pbpaste=f'echo "paste LC_ALL=$LC_ALL" >> "{log}"\n/bin/cat "{clip}"\n')
    app = ltt(ws.write("a.txt", "héllo\n"), env={"LANG": "C", "LC_ALL": "C", "LC_CTYPE": "C"}, path=bin_dir)
    app.press("shift+end", "ctrl+c")
    app.wait(lambda s: ws.clipboard == "héllo", timeout=5, what="the copy")
    ws.set_clipboard(" ünï")
    app.press("end", "ctrl+v")
    text_is(app, "héllo ünï", "")
    assert ws.clipboard_log == ["copy LC_ALL=en_US.UTF-8", "paste LC_ALL=en_US.UTF-8"]


def test_ctrl_c_without_a_copy_tool_still_writes_osc52(ltt, ws):
    app = ltt(ws.write("a.txt", "hello\n"), path=ws.bin_without(COPY_TOOL))
    mark = app.output_mark()
    app.press("shift+end", "ctrl+c")
    app.wait_status("Copied to the clipboard: 5 chars")
    assert osc52("hello") in app.output_since(mark)
    assert ws.clipboard is None and ws.clipboard_log == []


def test_ctrl_c_without_a_selection_hints_and_never_quits(ltt, ws):
    app = ltt(ws.write("a.txt", "hello\n"))
    mark = app.output_mark()
    app.press("ctrl+c", "ctrl+c", "ctrl+c")
    s = app.wait_status("Ctrl+C copies a selection. Ctrl+Q quits.")
    assert app.alive and s.mode == "EDITOR" and s.editor_lines == ["hello", ""]
    assert b"\x1b]52;" not in app.output_since(mark) and ws.clipboard_log == []


@pytest.mark.parametrize("opener,label", [("ctrl+l", "Go to:"), ("ctrl+f", "Search file:"),
                                          ("ctrl+n", "New file:"), ("ctrl+r", "Rename:")])
def test_ctrl_c_cancels_a_prompt(ltt, ws, opener, label):
    app = ltt(ws.write("a.txt", "hello\n"))
    s = app.press(opener)
    assert s.mode == "PROMPT" and s.prompt.startswith(label)
    app.press("ctrl+c")
    s = app.wait_status("Cancelled (Ctrl+C).")
    assert s.mode == "EDITOR" and app.alive and ws.clipboard_log == []


@pytest.mark.parametrize("asker", ["ctrl+w", "ctrl+q"])
def test_ctrl_c_cancels_a_question(ltt, ws, asker):
    app = ltt(ws.write("a.txt", "hello\n"))
    app.type("X")
    assert app.press(asker).mode == "CONFIRM"
    app.press("ctrl+c")
    s = app.wait_status("Cancelled (Ctrl+C).")
    assert app.alive and s.tab_names == ["a.txt"] and s.tabs[0].dirty


def test_copies_reach_the_clipboard_in_order(ltt, ws):
    app = ltt(ws.write("a.txt", "first\nsecond\n"))
    app.press("shift+end", "ctrl+c", "down", "home", "shift+end", "ctrl+c")
    app.wait_status("Copied to the clipboard: 6 chars")
    app.wait(lambda s: len(ws.clipboard_log) == 2, timeout=5, what="two copies")
    assert ws.clipboard == "second"


def hanging_tool(pid_file):
    """A clipboard tool that never ends; it leaves its pid behind."""
    return f'echo $$ > "{pid_file}.part" && /bin/mv "{pid_file}.part" "{pid_file}"\nexec /bin/sleep 30\n'


def test_a_hung_copy_tool_neither_freezes_the_ui_nor_lives_on(ltt, ws):  # C2
    pid_file = os.path.join(ws.sys, "copy.pid")
    app = ltt(ws.write("a.txt", "hello\n"), path=tool_dir(ws, "bin-hung", **{COPY_TOOL: hanging_tool(pid_file)}))
    app.press("shift+end", "ctrl+c")
    app.wait_status("Copied to the clipboard: 5 chars")
    app.wait(lambda s: os.path.exists(pid_file), timeout=5, what="the tool started")
    pid = int(open(pid_file).read())
    assert running(pid)  # the copy is done on screen while the tool still hangs (ltt gives it 3 s)
    app.press("end")
    app.type("!")
    text_is(app, "hello!", "")
    app.wait(lambda s: not running(pid), timeout=8, what="the hung tool killed")


def test_a_hung_paste_tool_is_given_up_after_a_moment(ltt, ws):
    pid_file = os.path.join(ws.sys, "paste.pid")
    app = ltt(ws.write("a.txt", "hello\n"), cols=140,
              path=tool_dir(ws, "bin-hung", **{PASTE_TOOL: hanging_tool(pid_file)}))
    t0 = time.monotonic()
    app.press("ctrl+v")
    app.wait_status(EMPTY)
    assert time.monotonic() - t0 < 5  # Ctrl+V waits for the tool, but not for long (1.5 s)
    pid = int(open(pid_file).read())
    app.wait(lambda s: not running(pid), timeout=5, what="the hung tool killed")
    app.type("!")
    text_is(app, "!hello", "")


# ---------------------------------------------------------------- Ctrl+X

def test_ctrl_x_cuts_the_selection(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    mark = app.output_mark()
    app.press("end", "shift+alt+left", "ctrl+x")
    s = app.wait_status("Cut to the clipboard.")
    assert s.editor_lines == ["hello", ""] and s.pos == (1, 2, 7, 0) and s.title_dirty
    app.wait(lambda s: ws.clipboard == "world", timeout=5, what="the cut text on the clipboard")
    assert osc52("world") in app.output_since(mark)


def test_ctrl_x_of_whole_lines(ltt, ws):
    app = ltt(ws.write("a.txt", "one\ntwo\nthree\n"))
    app.press("shift+down", "shift+down", "ctrl+x")
    text_is(app, "three", "")
    app.wait(lambda s: ws.clipboard == "one\ntwo\n", timeout=5, what="two lines on the clipboard")
    save(app, "a.txt")
    assert ws.read("a.txt") == "three\n"


def test_ctrl_x_is_one_undo_step(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    app.press("end", "shift+alt+left", "ctrl+x")
    app.wait_status("Cut to the clipboard.")
    app.press("ctrl+z")
    s = text_is(app, "hello world", "")
    assert s.pos == (1, 2, 7, 5) and not s.title_dirty


def test_ctrl_x_without_a_selection_does_nothing(ltt, ws):
    app = ltt(ws.write("a.txt", "hello\n"))
    before = app.snap().status
    s = app.press("ctrl+x")
    assert s.editor_lines == ["hello", ""] and not s.title_dirty and s.status == before
    assert app.alive and ws.clipboard_log == []


# ---------------------------------------------------------------- Ctrl+V

def test_ctrl_v_pastes_the_clipboard_at_the_cursor(ltt, ws):
    ws.set_clipboard("brave new ")
    app = ltt(ws.write("a.txt", "hello world\n"))
    app.press("alt+right", "right", "ctrl+v")
    s = app.wait_status("Pasted: 10 chars")
    assert s.editor_lines == ["hello brave new world", ""] and s.pos == (1, 2, 17, 0)
    assert ws.clipboard_log == ["paste LC_ALL=en_US.UTF-8"]


def test_ctrl_v_pastes_lines_without_auto_indent(ltt, ws):
    ws.set_clipboard("one\n  two\nthree")
    app = ltt(ws.write("a.txt", "    indented\n"))
    app.press("end", "ctrl+v")
    s = text_is(app, "    indentedone", "  two", "three", "")
    assert s.pos == (3, 4, 6, 0)


@pytest.mark.parametrize("clip", ["one\r\ntwo\r\n", "one\rtwo\r", "one\ntwo\n"], ids=["crlf", "cr", "lf"])
def test_ctrl_v_line_breaks_take_the_files_line_ending(ltt, ws, clip):
    ws.set_clipboard(clip)
    app = ltt(ws.write("a.txt", b"end\r\n"))
    app.press("ctrl+v")
    app.wait_status("Pasted: 8 chars")
    text_is(app, "one", "two", "end", "")
    save(app, "a.txt")
    assert ws.read_bytes("a.txt") == b"one\r\ntwo\r\nend\r\n"


@pytest.mark.parametrize("clip", [None, ""], ids=["never_set", "empty"])
def test_ctrl_v_with_an_empty_clipboard(ltt, ws, clip):
    if clip is not None:
        ws.set_clipboard(clip)
    app = ltt(ws.write("a.txt", "hello\n"), cols=140)  # wide enough for the whole message
    app.press("ctrl+v")
    s = app.wait_status(EMPTY)
    assert s.editor_lines == ["hello", ""] and not s.title_dirty


def test_ctrl_v_without_a_paste_tool(ltt, ws):
    ws.set_clipboard("text")
    app = ltt(ws.write("a.txt", "hello\n"), cols=140, path=ws.bin_without(PASTE_TOOL))
    app.press("ctrl+v")
    s = app.wait_status(EMPTY)
    assert s.editor_lines == ["hello", ""] and not s.title_dirty


def test_ctrl_v_with_only_control_characters(ltt, ws):
    ws.set_clipboard("\x07\x1b\x00\x08")
    app = ltt(ws.write("a.txt", "hello\n"))
    app.press("ctrl+v")
    s = app.wait_status("Nothing pasteable on the clipboard.")
    assert s.editor_lines == ["hello", ""] and not s.title_dirty


def test_ctrl_v_drops_control_characters_but_keeps_tabs(ltt, ws):
    ws.set_clipboard("a\tb\x1b[31mc\x07d")
    app = ltt(ws.write("a.txt", ""))
    app.press("ctrl+v")
    app.wait_status("Pasted: 9 chars")
    save(app, "a.txt")
    assert ws.read("a.txt") == "a\tb[31mcd"


def test_ctrl_v_pastes_non_ascii_text(ltt, ws):
    text = "héllo 日本語 ⚡ 👀 e\u0301"
    ws.set_clipboard(text)
    app = ltt(ws.write("a.txt", ""))
    app.press("ctrl+v")
    app.wait_status("Pasted:")
    save(app, "a.txt")
    assert ws.read("a.txt") == text


def test_ctrl_v_replaces_the_selection(ltt, ws):
    ws.set_clipboard("there")
    app = ltt(ws.write("a.txt", "hello world\n"))
    app.press("end", "shift+alt+left", "ctrl+v")
    text_is(app, "hello there", "")
    app.press("ctrl+z")
    s = text_is(app, "hello world", "")
    assert s.pos == (1, 2, 7, 5)


def _huge_clipboard():
    with shared("save-clipboard-4m") as (folder, ready):
        path = os.path.join(folder, "clip.txt")
        if not ready:
            with open(path, "wb") as f:
                f.write((b"x" * 64 + b"\n") * 63_100)  # 4,101,500 chars
    return path


def test_ctrl_v_cuts_a_huge_clipboard_at_4_000_000_chars(ltt, ws):
    src = _huge_clipboard()
    shutil.copyfile(src, ws.clip_file)
    app = ltt(ws.write("a.txt", ""))
    app.press("ctrl+v")
    s = app.wait_status("Pasted: 4000000 chars", timeout=20)
    assert s.pos[:2] == (61_539, 61_539)  # 61,538 whole lines and 30 characters of the next
    save(app, "a.txt")
    with open(src, "rb") as f:
        assert ws.read_bytes("a.txt") == f.read(4_000_000)


# ---------------------------------------------------------------- the terminal's paste

def test_paste_inserts_the_text_exactly(ltt, ws):  # T1: no auto-indent staircase
    app = ltt(ws.write("a.txt", "abc\n"))
    app.paste("if x:\n    a()\n    b()\n")
    s = text_is(app, "if x:", "    a()", "    b()", "abc", "")
    assert s.status == "Pasted: 22 chars"


def test_paste_in_the_middle_of_a_line_splits_it(ltt, ws):
    app = ltt(ws.write("a.txt", "abc\n"))
    app.press("right")
    app.paste("1\n2")
    s = text_is(app, "a1", "2bc", "")
    assert s.pos == (2, 3, 2, 0)


def test_paste_into_an_indented_line_adds_no_indent(ltt, ws):
    app = ltt(ws.write("a.py", "    pass\n"))
    app.press("end")
    app.paste("\nx = 1\ny = 2")
    s = text_is(app, "    pass", "x = 1", "y = 2", "")
    assert s.pos == (3, 4, 6, 0)


def test_paste_is_one_undo_step(ltt, ws):  # T1
    app = ltt(ws.write("a.txt", "abc\n"))
    app.paste("one\ntwo\nthree\n")
    text_is(app, "one", "two", "three", "abc", "")
    app.press("ctrl+z")
    s = text_is(app, "abc", "")
    assert s.pos == (1, 2, 1, 0) and not s.title_dirty
    app.press("ctrl+y")
    s = text_is(app, "one", "two", "three", "abc", "")
    assert s.pos == (4, 5, 1, 0)


@pytest.mark.parametrize("nl", [b"\r", b"\n", b"\r\n"], ids=["cr", "lf", "crlf"])
def test_paste_line_breaks_of_any_kind_take_the_files_line_ending(ltt, ws, nl):
    app = ltt(ws.write("a.txt", b"end\r\n"))
    paste_bytes(app, b"one" + nl + b"two" + nl)
    app.wait_status("Pasted: 8 chars")
    text_is(app, "one", "two", "end", "")
    save(app, "a.txt")
    assert ws.read_bytes("a.txt") == b"one\r\ntwo\r\nend\r\n"


def test_paste_with_mixed_line_breaks(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    paste_bytes(app, b"a\r\nb\nc\rd")
    text_is(app, "a", "b", "c", "d")


def test_paste_keeps_tabs(ltt, ws):
    app = ltt(ws.write("Makefile", ""))
    app.paste("all:\n\techo hi\n\t\tdeep\n")
    save(app, "Makefile")
    assert ws.read("Makefile") == "all:\n\techo hi\n\t\tdeep\n"


def test_paste_drops_control_characters(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    paste_bytes(app, b"a\x1b[31mb\x07c\x00d\x7fe")
    app.wait_status("Pasted: 9 chars")
    save(app, "a.txt")
    assert ws.read("a.txt") == "a[31mbcde"


def test_paste_of_non_ascii_text(ltt, ws):
    text = "日本語 ⚡ é 👀 e\u0301"
    app = ltt(ws.write("a.txt", ""))
    app.paste(text)
    app.wait_status("Pasted:")
    save(app, "a.txt")
    assert ws.read("a.txt") == text


def test_paste_replaces_the_selection(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    app.press("end", "shift+alt+left")
    app.paste("there")
    text_is(app, "hello there", "")
    app.press("ctrl+z")
    s = text_is(app, "hello world", "")
    assert s.pos == (1, 2, 7, 5)


def test_paste_into_the_search_prompt_is_one_line(ltt, ws):  # T1
    app = ltt(ws.write("a.txt", "foo bar\nbaz\n"))
    app.press("ctrl+f")
    app.paste("foo\nbar\n")
    s = app.wait(lambda s: s.mode == "PROMPT" and s.prompt.startswith("Search file: foo bar "), what="the query")
    assert "found: 1" in s.prompt
    assert s.editor_lines[:2] == ["foo bar", "baz"] and not s.title_dirty


def test_paste_into_the_search_prompt_turns_tabs_into_spaces(ltt, ws):
    app = ltt(ws.write("a.txt", "foo bar\n"))
    app.press("ctrl+f")
    app.paste("foo\tbar")
    app.wait(lambda s: s.prompt and s.prompt.startswith("Search file: foo bar "), what="the query")


def test_paste_into_the_go_to_prompt_waits_for_enter(ltt, ws):  # T1: a pasted line break used to fire it
    app = ltt(ws.write("a.txt", "".join(f"line {i}\n" for i in range(1, 11))))
    app.press("ctrl+l")
    app.paste("7\n")
    s = app.wait(lambda s: s.prompt == "Go to: 7", what="7 in the prompt")
    assert s.mode == "PROMPT"
    app.press("enter")
    assert app.wait_status("Line 7 of 11.").pos[0] == 7


def test_paste_into_the_new_file_prompt_waits_for_enter(ltt, ws):
    app = ltt(ws.write("proj/a.txt", "x\n"))
    app.press("ctrl+n")
    app.paste("b.txt\n")
    app.wait(lambda s: s.prompt and s.prompt.endswith("/proj/b.txt"), what="the name in the prompt")
    assert not ws.exists("proj/b.txt")
    app.press("enter")
    app.wait_status("File created and opened:")
    assert ws.exists("proj/b.txt")


def test_paste_in_the_tree_hints_and_changes_nothing(ltt, ws):
    app = ltt(ws.write("proj/a.txt", "hello\n"))
    app.press("esc")
    app.paste("text")
    s = app.wait_status("Pasting goes into the editor: open a file and press Tab to get there.")
    assert s.mode == "TREE" and not s.tabs[0].dirty
    app.press("tab")
    text_is(app, "hello", "")


def test_paste_without_an_open_file_hints(ltt, ws):
    ws.write("proj/a.txt", "hello\n")
    app = ltt(ws.path("proj"))
    app.paste("text")
    s = app.wait_status("Pasting goes into the editor")
    assert s.tabs == [] and s.mode == "TREE"


def test_a_paste_never_answers_a_question(ltt, ws):
    app = ltt(ws.write("a.txt", "hello\n"))
    app.type("X")
    app.press("ctrl+w")
    s = app.paste("d")  # D would close the tab without saving
    assert s.mode == "CONFIRM" and s.tabs[0].dirty
    app.press("esc")
    assert app.wait_status("Cancelled.").editor_lines == ["Xhello", ""]


def test_a_big_paste_arrives_whole(ltt, ws):  # fix/pty/t9.py
    text = "".join(f"line {i} of the pasted text, some words here\n" for i in range(5000))
    app = ltt(ws.write("a.txt", "start\n"))
    app.paste(text)
    s = app.wait_status(f"Pasted: {len(text)} chars")
    assert len(text) == 228_890 and s.pos[:2] == (5001, 5002)
    save(app, "a.txt")
    assert ws.read("a.txt") == text + "start\n"


def test_a_4_mb_paste_arrives_whole(ltt, ws):  # reviewB/t6; only Ctrl+V cuts at 4,000,000 chars
    text = ("0123456789abcdefghijklmnopqrstuvwxyz" * 2 + "\n") * 57_456
    app = ltt(ws.write("a.txt", "start\n"))
    app.paste(text)
    s = app.wait_status(f"Pasted: {len(text)} chars", timeout=30)
    assert len(text) > 4_000_000 and s.pos[:2] == (57_457, 57_458)
    save(app, "a.txt")
    assert ws.read("a.txt") == text + "start\n"


def test_a_paste_cut_short_keeps_what_arrived(ltt, ws):
    app = ltt(ws.write("a.txt", "end\n"))
    app.write(K.PASTE_START + b"partial")  # the end marker never comes
    s = app.wait(lambda s: s.editor_lines[:1] == ["partialend"], timeout=6, what="the paste given up waiting")
    assert s.status == "Pasted: 7 chars"


def test_an_empty_paste_does_nothing(ltt, ws):
    app = ltt(ws.write("a.txt", "end\n"))
    app.write(K.PASTE_START + K.PASTE_END + b"x")
    app.settle()
    s = text_is(app, "xend", "")
    assert "Pasted" not in s.status


def test_a_paste_of_control_characters_only(ltt, ws):
    app = ltt(ws.write("a.txt", "end\n"))
    paste_bytes(app, b"\x07\x01\x1b")
    s = app.wait_status("Nothing pasteable.")
    assert s.editor_lines == ["end", ""] and not s.title_dirty
