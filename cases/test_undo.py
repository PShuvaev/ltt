"""Undo and redo: Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z; how edits group into steps; the saved state; the cursor; long histories."""
from save_helpers import save


def text_is(app, *lines):
    """Wait until the editor shows exactly these lines (the whole file in view)."""
    return app.wait(lambda s: s.editor_lines == list(lines), what=f"the text {list(lines)!r}")


def test_a_run_of_typing_is_one_undo_step(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("hello world", each=True)
    text_is(app, "hello world")
    app.press("ctrl+z")
    s = app.wait_status("Undone. Ctrl+Y redoes it.")
    assert s.editor_lines == [""] and not s.title_dirty


def test_ctrl_y_redoes(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("hello")
    app.press("ctrl+z")
    text_is(app, "")
    app.press("ctrl+y")
    s = app.wait_status("Redone. Ctrl+Z undoes it.")
    assert s.editor_lines == ["hello"] and s.title_dirty


def test_ctrl_shift_z_redoes(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("hello")
    app.press("ctrl+z")
    text_is(app, "")
    app.press("ctrl+shift+z")
    s = app.wait_status("Redone. Ctrl+Z undoes it.")
    assert s.editor_lines == ["hello"]


def test_undo_and_redo_with_the_kitty_keyboard(ltt, ws):
    app = ltt(ws.write("a.txt", ""), keyboard="kitty")
    app.type("hello")
    app.press("ctrl+z")
    text_is(app, "")
    app.press("ctrl+shift+z")
    text_is(app, "hello")
    app.press("ctrl+z", "ctrl+y")
    assert app.wait_status("Redone.").editor_lines == ["hello"]


def test_nothing_to_undo(ltt, ws):
    app = ltt(ws.write("a.txt", "text\n"))
    app.press("ctrl+z")
    s = app.wait_status("Nothing to undo.")
    assert s.editor_lines == ["text", ""] and not s.title_dirty and s.mode == "EDITOR"


def test_nothing_to_redo(ltt, ws):
    app = ltt(ws.write("a.txt", "text\n"))
    app.press("ctrl+y")
    s = app.wait_status("Nothing to redo.")
    assert s.editor_lines == ["text", ""] and not s.title_dirty


def test_nothing_to_undo_without_an_open_file(ltt, ws):
    ws.write("proj/a.txt", "text\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+z")
    s = app.wait_status("Nothing to undo.")
    assert s.mode == "TREE" and s.tabs == []


def test_enter_is_an_undo_step_of_its_own(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("ab\ncd")
    text_is(app, "ab", "cd")
    app.press("ctrl+z")
    text_is(app, "ab", "")
    app.press("ctrl+z")
    text_is(app, "ab")
    app.press("ctrl+z")
    text_is(app, "")


def test_tab_is_an_undo_step_of_its_own(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("a")
    app.press("tab")
    app.type("b")
    text_is(app, "a   b")
    app.press("ctrl+z")  # spaces at the end do not show: the cursor tells the steps apart
    app.wait(lambda s: s.editor_lines == ["a"] and s.pos[2] == 5, what="b undone, the cursor after the tab")
    app.press("ctrl+z")
    app.wait(lambda s: s.editor_lines == ["a"] and s.pos[2] == 2, what="the tab undone")
    app.press("ctrl+z")
    text_is(app, "")


def test_moving_the_cursor_starts_a_new_step(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    app.press("left")
    app.type("X")
    text_is(app, "abXc")
    app.press("ctrl+z")
    text_is(app, "abc")
    app.press("ctrl+z")
    text_is(app, "")


def test_a_run_of_backspaces_is_one_step(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    app.press("end")
    app.write(b"\x7f" * 6)
    app.settle()
    text_is(app, "hello", "")
    app.press("ctrl+z")
    text_is(app, "hello world", "")


def test_a_run_of_deletes_is_one_step(ltt, ws):
    app = ltt(ws.write("a.txt", "hello world\n"))
    for _ in range(6):
        app.press("delete")
    text_is(app, "world", "")
    app.press("ctrl+z")
    text_is(app, "hello world", "")


def test_word_deletes_group_with_backspaces(ltt, ws):
    app = ltt(ws.write("a.txt", "one two three\n"))
    app.press("end", "alt+backspace", "alt+backspace", "backspace")
    text_is(app, "one", "")
    app.press("ctrl+z")
    text_is(app, "one two three", "")


def test_typing_after_backspace_is_a_new_step(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    app.press("backspace")
    app.type("d")
    text_is(app, "abd")
    app.press("ctrl+z")
    text_is(app, "ab")
    app.press("ctrl+z")
    text_is(app, "abc")
    app.press("ctrl+z")
    text_is(app, "")


def test_a_terminal_paste_is_an_undo_step_of_its_own(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("ab")
    app.paste("XY")
    app.type("cd")
    text_is(app, "abXYcd")
    app.press("ctrl+z")
    text_is(app, "abXY")
    app.press("ctrl+z")
    text_is(app, "ab")
    app.press("ctrl+z")
    text_is(app, "")


def test_a_clipboard_paste_is_an_undo_step_of_its_own(ltt, ws):
    ws.set_clipboard("XY")
    app = ltt(ws.write("a.txt", ""))
    app.type("ab")
    app.press("ctrl+v")
    app.type("cd")
    text_is(app, "abXYcd")
    app.press("ctrl+z")
    text_is(app, "abXY")
    app.press("ctrl+z")
    text_is(app, "ab")


def test_typing_over_a_selection_is_a_step_of_its_own(ltt, ws):  # D9
    app = ltt(ws.write("a.txt", ""))
    app.type("hello")
    app.press("home", "shift+end")  # selected, the cursor back where the typing stopped
    app.type("bye")
    text_is(app, "bye")
    app.press("ctrl+z")
    s = text_is(app, "hello")
    assert s.pos == (1, 1, 6, 5)  # the replaced selection is back
    app.press("ctrl+z")
    text_is(app, "")


def test_deleting_a_selection_is_one_step_that_brings_it_back(ltt, ws):
    app = ltt(ws.write("a.txt", "one\ntwo\nthree\n"))
    app.press("shift+down", "shift+down", "backspace")
    text_is(app, "three", "")
    app.press("ctrl+z")
    s = text_is(app, "one", "two", "three", "")
    assert s.pos == (3, 4, 1, len("one\ntwo\n"))


def test_saving_ends_the_undo_step(ltt, ws):  # D9: undo can stop at the saved text
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    save(app, "a.txt")
    app.type("def")
    app.wait(lambda s: s.editor_lines == ["abcdef"] and s.title_dirty)
    app.press("ctrl+z")
    s = text_is(app, "abc")
    assert not s.title_dirty and not s.tabs[0].dirty  # back at the saved text: no ●


def test_undo_past_the_saved_text_is_dirty_again(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    save(app, "a.txt")
    app.press("ctrl+z")
    s = text_is(app, "")
    assert s.title_dirty and s.tabs[0].dirty
    app.press("ctrl+y")
    s = text_is(app, "abc")
    assert not s.title_dirty and not s.tabs[0].dirty
    assert ws.read("a.txt") == "abc"


def test_undoing_every_edit_makes_the_tab_clean(ltt, ws):
    app = ltt(ws.write("a.txt", "text\n"))
    app.type("ab\ncd")
    app.wait(lambda s: s.title_dirty)
    app.press("ctrl+z", "ctrl+z", "ctrl+z")
    s = text_is(app, "text", "")
    assert not s.title_dirty and not s.tabs[0].dirty
    app.press("ctrl+y")
    assert app.wait(lambda s: s.title_dirty).tabs[0].dirty


def test_typing_and_deleting_it_again_leaves_the_tab_clean(ltt, ws):
    app = ltt(ws.write("a.txt", "text\n"))
    app.type("ab")
    app.wait(lambda s: s.title_dirty)
    app.press("backspace", "backspace")
    s = text_is(app, "text", "")
    assert not s.title_dirty and not s.tabs[0].dirty


def test_a_new_edit_clears_the_redo_history(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    app.press("ctrl+z")
    text_is(app, "")
    app.type("x")
    app.press("ctrl+y")
    s = app.wait_status("Nothing to redo.")
    assert s.editor_lines == ["x"]


def test_redo_replays_the_steps_in_order(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("a\nb")
    app.press("ctrl+z", "ctrl+z", "ctrl+z")
    text_is(app, "")
    app.press("ctrl+y")
    text_is(app, "a")
    app.press("ctrl+y")
    text_is(app, "a", "")
    app.press("ctrl+y")
    text_is(app, "a", "b")
    app.press("ctrl+y")
    app.wait_status("Nothing to redo.")


def test_undo_brings_back_your_edits_after_taking_the_disk_version(ltt, ws):
    p = ws.write("proj/a.txt", "base\n")
    app = ltt(p)
    app.type("mine ")
    ws.write("proj/a.txt", "the agent's text\n")
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on the tab")
    assert app.press("ctrl+s").mode == "CONFIRM"
    app.press("r")
    s = app.wait_status("Took the disk version of a.txt; Ctrl+Z brings back your edits.")
    assert s.editor_lines == ["the agent's text", ""] and not s.tabs[0].dirty
    app.press("ctrl+z")
    s = text_is(app, "mine base", "")
    assert s.tabs[0].dirty and ws.read("proj/a.txt") == "the agent's text\n"


def test_redo_of_a_reload_keeps_the_cursor_off_half_an_emoji(ltt, ws):  # V2
    p = ws.write("proj/a.txt", "abc\n")
    app = ltt(p)
    app.press("right")  # the cursor after "a": inside the emoji that replaces it
    ws.write("proj/a.txt", "👀bc\n")
    app.wait_status("Reloaded a.txt")
    app.press("ctrl+z")
    text_is(app, "abc", "")
    app.press("ctrl+y")
    text_is(app, "👀bc", "")
    app.type("X")
    save(app, "a.txt")
    assert ws.read_bytes("proj/a.txt").decode("utf-8") in ("X👀bc\n", "👀Xbc\n")


def test_ctrl_z_in_the_tree_undoes_and_moves_to_the_editor(ltt, ws):
    app = ltt(ws.write("proj/a.txt", "text\n"))
    app.type("X")
    assert app.press("esc").mode == "TREE"
    app.press("ctrl+z")
    s = app.wait_status("Undone. Ctrl+Y redoes it.")
    assert s.mode == "EDITOR" and s.editor_lines == ["text", ""]


def test_ctrl_y_in_the_tree_redoes_and_moves_to_the_editor(ltt, ws):
    app = ltt(ws.write("proj/a.txt", "text\n"))
    app.type("X")
    app.press("ctrl+z")
    assert app.press("esc").mode == "TREE"
    app.press("ctrl+y")
    s = app.wait_status("Redone. Ctrl+Z undoes it.")
    assert s.mode == "EDITOR" and s.editor_lines == ["Xtext", ""]


def test_ctrl_z_in_the_tree_with_nothing_to_undo_stays_there(ltt, ws):
    app = ltt(ws.write("proj/a.txt", "text\n"))
    app.press("esc")
    app.press("ctrl+z")
    assert app.wait_status("Nothing to undo.").mode == "TREE"


def test_ctrl_z_in_a_prompt_does_not_undo(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("abc")
    app.press("ctrl+l", "ctrl+z")
    s = app.snap()
    assert s.mode == "PROMPT" and s.prompt.startswith("Go to:")
    app.press("esc")
    assert app.wait_status("Cancelled.").editor_lines == ["abc"]


def test_undo_puts_the_cursor_back_where_the_edit_was(ltt, ws):
    app = ltt(ws.write("a.txt", "line one\nline two\nline three\n"))
    app.press("down", "end")
    app.type("!!")
    assert app.press("ctrl+home").pos == (1, 4, 1, 0)
    app.press("ctrl+z")
    s = text_is(app, "line one", "line two", "line three", "")
    assert s.pos == (2, 4, 9, 0)


def test_redo_puts_the_cursor_after_the_edit(ltt, ws):
    app = ltt(ws.write("a.txt", "line one\nline two\nline three\n"))
    app.press("down", "end")
    app.type("!!")
    app.press("ctrl+z", "ctrl+home", "ctrl+y")
    s = text_is(app, "line one", "line two!!", "line three", "")
    assert s.pos == (2, 4, 11, 0)


def test_undo_brings_the_edited_line_into_view(ltt, ws):
    app = ltt(ws.write("a.txt", "".join(f"line {i}\n" for i in range(1, 201))))
    app.press("ctrl+l")
    app.type("150\n")
    app.wait(lambda s: s.pos[0] == 150)
    app.type("X")
    app.press("ctrl+home")
    assert app.wait(lambda s: s.editor_rows[0].number == 1).editor_row(150) is None
    app.press("ctrl+z")
    s = app.wait(lambda s: s.editor_row(150) is not None and s.pos[0] == 150)
    assert s.editor_row(150).text == "line 150" and s.editor_row(150).current


def test_each_tab_has_its_own_history(ltt, ws):
    a, b = ws.write("a.txt", "a\n"), ws.write("b.txt", "b\n")
    app = ltt(a, b)
    app.type("X")
    app.press("ctrl+pgdn")
    app.wait(lambda s: s.active_tab.name == "b.txt")
    app.press("ctrl+z")
    s = app.wait_status("Nothing to undo.")
    assert [(t.name, t.dirty) for t in s.tabs] == [("a.txt", True), ("b.txt", False)]
    app.press("ctrl+pgup")
    app.wait(lambda s: s.active_tab.name == "a.txt")
    app.press("ctrl+z")
    s = text_is(app, "a", "")
    assert not s.tabs[0].dirty


def test_the_history_keeps_the_last_200_steps(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    app.type("\n" * 205)  # every line break is a step of its own
    app.wait(lambda s: s.pos[:2] == (206, 206))
    app.write(b"\x1a" * 205)  # Ctrl+Z
    app.settle()
    s = app.wait(lambda s: s.status == "Nothing to undo.")
    assert s.pos == (6, 6, 1, 0)  # the 5 oldest steps fell off
    app.write(b"\x19" * 200)  # Ctrl+Y
    app.settle()
    s = app.wait(lambda s: s.pos[:2] == (206, 206))
    assert s.status == "Redone. Ctrl+Z undoes it."
    app.press("ctrl+y")
    app.wait_status("Nothing to redo.")


def test_a_long_run_of_typing_is_still_one_step(ltt, ws):
    app = ltt(ws.write("a.txt", ""))
    text = "lorem ipsum dolor sit amet " * 40
    app.type(text)
    app.wait(lambda s: s.pos[2] == len(text) + 1)
    app.press("ctrl+z")
    s = text_is(app, "")
    assert s.pos == (1, 1, 1, 0)
