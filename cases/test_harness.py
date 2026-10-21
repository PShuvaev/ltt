"""The harness itself, checked against plain ltt behaviour: screen parsing, keys, mouse, signals, clipboard."""
import base64
import signal

from harness import keys as K


def test_layout_and_colours(ltt, ws):
    ws.write("proj/docs/readme.md", "# hi\n")
    ws.write("proj/a.txt", "one\ntwo\n")
    app = ltt(ws.path("proj"))
    s = app.snap()
    assert s.left_w == 32 and s.lines[2][32] == "│"
    root = s.tree[0]
    assert (root.name, root.depth, root.is_dir, root.expanded, root.selected) == ("proj", 0, True, True, True)
    assert root.bg == "DarkCyan" and root.fg == "White" and root.size == ""
    rows = {r.name: r for r in s.tree}
    assert rows["docs"].is_dir and not rows["docs"].expanded and rows["docs"].depth == 1
    assert not rows["a.txt"].is_dir and rows["a.txt"].depth == 1 and rows["a.txt"].fg == "Gray"
    assert rows["a.txt"].size == "8"
    assert s.editor_hint.startswith("Pick a file on the left")
    app.press("end", "enter")  # the last row is a.txt (files after folders? whatever is last) — open it
    s = app.wait(lambda s: s.tabs, what="a tab")
    assert s.mode == "EDITOR" and s.pos[:3] == (1, s.pos[1], 1)
    sel = s.tree_selected
    assert sel is not None and sel.bg == "DarkGray"  # the tree's selection dims while the editor has focus
    assert s.tabs[0].active and not s.tabs[0].dirty
    assert s.editor_rows[0].number == 1 and s.editor_rows[0].current


def test_lone_esc_then_keys(ltt, ws):
    ws.write("a.txt", "one two three\n")
    app = ltt(ws.path("a.txt"))
    app.wait_mode("EDITOR")
    app.press("esc")
    assert app.snap().mode == "TREE"
    app.press("tab", "end", "alt+b")  # Alt+B (Esc b) is a word jump, not Esc then b
    s = app.snap()
    assert s.mode == "EDITOR" and s.pos[2] == len("one two ") + 1
    assert s.editor_lines[0] == "one two three"


def test_resize_redraws_at_new_size(ltt, ws):
    ws.write("a.txt", "x\n")
    app = ltt(ws.path("a.txt"), cols=100, rows=30)
    s = app.resize(80, 24)
    assert (s.cols, s.rows) == (80, 24)
    s = app.wait(lambda s: s.mode == "EDITOR" and s.lines[2][s.left_w] == "│")
    assert s.left_w == 25
    s = app.resize(30, 8)
    assert app.wait(lambda s: s.too_small).lines[0].startswith(" Terminal too small")


def test_sigterm_keeps_unsaved_edits(ltt, ws):
    ws.write("note.txt", "hello\n")
    app = ltt(ws.path("note.txt"))
    app.type("XYZ")
    app.wait(lambda s: s.title_dirty)
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    assert len(ws.recovery_files()) >= 1
    tail = app.output[app.output.rfind(b"\x1b[?2026l"):]  # everything after the last frame
    assert b"\x1b[?1049l" in tail and b"\x1b[?1000l" in tail and b"\x1b[<u" in tail


def test_hang_up_keeps_unsaved_edits(ltt, ws):
    ws.write("note.txt", "hello\n")
    app = ltt(ws.path("note.txt"))
    app.type("HUP")
    app.wait(lambda s: s.title_dirty)
    app.hang_up()
    assert app.wait_exit() == 129
    assert len(ws.recovery_files()) >= 1


def test_mouse_click_and_double_click_in_tree(ltt, ws):
    ws.write("proj/a.txt", "aaa\n")
    ws.write("proj/b.txt", "bbbb\n")
    app = ltt(ws.path("proj"))
    row = app.snap().tree_row("b.txt")
    s = app.click(6, row.y)
    assert s.tree_selected.name == "b.txt" and s.tabs == []
    app.double_click(6, row.y)
    s = app.wait(lambda s: s.tab_names == ["b.txt"], what="b.txt opened by a double-click")
    assert s.editor_lines[0] == "bbbb"


def test_kitty_layout_key(ltt, ws):
    # AZERTY: the key labelled A sits where US has Q; Ctrl+A must select all, not quit.
    ws.write("a.txt", "some text\n")
    app = ltt(ws.path("a.txt"))
    app.press(K.kitty_key("ctrl+a", base="q"))
    s = app.wait_status("Selected all")
    assert app.alive and s.pos[3] == len("some text\n")


def test_bracketed_paste_and_clipboard(ltt, ws):
    ws.write("a.txt", "abc\n")
    app = ltt(ws.path("a.txt"))
    app.paste("if x:\n    a()\n")
    s = app.wait(lambda s: s.editor_lines[:3] == ["if x:", "    a()", "abc"], what="pasted text as it is")
    app.press("ctrl+z")
    app.wait(lambda s: s.editor_lines[0] == "abc", what="one undo removes the whole paste")
    mark = app.output_mark()
    app.press("ctrl+a", "ctrl+c")
    app.wait_status("Copied to the clipboard")
    app.wait(lambda s: ws.clipboard_log, timeout=5, what="the fake pbcopy ran")  # its log line comes last
    assert ws.clipboard == "abc\n"
    assert b"\x1b]52;c;" + base64.b64encode(b"abc\n") in app.output_since(mark)
    assert ws.clipboard_log[-1] == "copy LC_ALL=en_US.UTF-8"
