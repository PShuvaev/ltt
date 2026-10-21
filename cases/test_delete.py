"""Ctrl+Delete in the tree: the question, what answers it, what goes with the entry, what is never deleted."""
import os
import time

import pytest

from harness import keys as K
from tree_helpers import names, select

WIDE = 300  # room for messages with whole paths
QUESTION_FILE = 'Delete file "x.txt" from disk forever?  D deletes · Esc cancels'


def proj(ws):
    ws.write("proj/dir/a.txt", "aaaa\n")
    ws.write("proj/dir/deeper/b.txt", "bb\n")
    ws.write("proj/x.txt", "xxx\n")
    ws.write("proj/y.txt", "y\n")
    return ws.path("proj")


def ask(app, name):
    """Select the entry and press Ctrl+Delete; returns the frame with the question."""
    select(app, name)
    return app.press("ctrl+delete")


# ---------------------------------------------------------------- the question

def test_ctrl_delete_asks_before_deleting_a_file(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    s = ask(app, "x.txt")
    assert s.mode == "CONFIRM" and s.question == QUESTION_FILE
    assert ws.exists("proj/x.txt")


def test_ctrl_delete_on_a_folder_says_all_its_contents_go(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    s = ask(app, "dir")
    assert s.question == 'Delete folder with all its contents "dir" from disk forever?  D deletes · Esc cancels'


@pytest.mark.parametrize("key", ["d", "D", "y"])
def test_d_deletes_the_file(ltt, ws, key):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press(key)
    assert s.mode == "TREE" and s.status == "Deleted from disk: " + ws.path("proj/x.txt")
    assert names(s) == ["proj", "dir", "y.txt"] and not ws.exists("proj/x.txt")


def test_esc_cancels(ltt, ws):
    app = ltt(proj(ws))
    ask(app, "x.txt")
    s = app.press("esc")
    assert s.mode == "TREE" and s.status == "Cancelled." and ws.exists("proj/x.txt")
    assert s.tree_selected.name == "x.txt"


def test_ctrl_c_cancels(ltt, ws):
    app = ltt(proj(ws))
    ask(app, "x.txt")
    s = app.press("ctrl+c")
    assert s.mode == "TREE" and s.status == "Cancelled (Ctrl+C)." and ws.exists("proj/x.txt")


def test_enter_leaves_the_question_standing(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press("enter")
    assert s.mode == "CONFIRM" and s.question == QUESTION_FILE and ws.exists("proj/x.txt")


def test_other_keys_leave_the_question_standing(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    for key in ("n", "x", "s", "space", "1", "up", "down", "left", "right", "tab", "delete", "backspace",
                "home", "pgdn"):
        s = app.press(key)
        assert s.mode == "CONFIRM" and s.question == QUESTION_FILE, key
    assert ws.exists("proj/x.txt") and names(app.snap()) == ["proj", "dir", "x.txt", "y.txt"]


def test_a_paste_leaves_the_question_standing(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.paste("d")
    assert s.mode == "CONFIRM" and ws.exists("proj/x.txt")


def test_qwertz_z_does_not_delete(ltt, ws):
    # T3: the German z sits on the US y key; answers follow the typed letter, not the key position
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press(K.kitty_key("z", base="y"))
    assert s.mode == "CONFIRM" and ws.exists("proj/x.txt")


def test_cyrillic_letter_on_the_d_key_deletes(ltt, ws):
    # with the Russian layout on, the D key types в: the question is answered by the key
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press("в")
    assert s.status == "Deleted from disk: " + ws.path("proj/x.txt") and not ws.exists("proj/x.txt")


def test_cyrillic_letter_on_the_d_key_deletes_in_the_kitty_protocol(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press(K.kitty_key("в", base="d"))
    assert s.status == "Deleted from disk: " + ws.path("proj/x.txt")


def test_cyrillic_letter_on_another_key_does_not_delete(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    s = app.press("т")  # the N key
    assert s.mode == "CONFIRM" and ws.exists("proj/x.txt")


def test_the_mouse_leaves_the_question_standing(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    s = ask(app, "x.txt")
    s = app.click(6, s.tree_row("y.txt").y)
    assert s.mode == "CONFIRM" and s.question == QUESTION_FILE


# ---------------------------------------------------------------- what is deleted

def test_a_folder_is_deleted_with_everything_inside(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "dir")
    s = app.press("d")
    assert s.status == "Deleted from disk: " + ws.path("proj/dir")
    assert names(s) == ["proj", "x.txt", "y.txt"] and not ws.exists("proj/dir")


def test_a_deleted_entry_in_an_expanded_folder_leaves_the_tree(ltt, ws):
    app = ltt(proj(ws))
    select(app, "dir")
    app.press("right")
    ask(app, "a.txt")
    s = app.press("d")
    assert names(s) == ["proj", "dir", "deeper", "x.txt", "y.txt"] and ws.files("proj/dir") == ["deeper/b.txt"]


def test_a_file_deleted_in_a_nested_root_leaves_both_places(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/dir"))  # dir: inside proj and a root of its own
    select(app, "dir")
    app.press("right")
    ask(app, "a.txt")
    s = app.press("d")
    assert [r.name for r in s.tree if r.name == "a.txt"] == [] and not ws.exists("proj/dir/a.txt")
    assert [r.depth for r in s.tree if r.name == "deeper"] == [2, 1]


def test_the_selection_moves_to_the_next_entry(ltt, ws):
    app = ltt(proj(ws))
    ask(app, "x.txt")
    s = app.press("d")
    assert s.tree_selected.name == "y.txt"
    ask(app, "y.txt")
    s = app.press("d")
    assert s.tree_selected.name == "dir"  # the last one: the one above it


def test_a_symlinked_folder_loses_only_the_link(ltt, ws):
    ws.write("target/keep.txt", "keep\n")
    ws.write("proj/a.txt", "a\n")
    os.symlink(ws.path("target"), ws.path("proj/link"))
    app = ltt(ws.path("proj"))
    ask(app, "link")
    s = app.press("d")
    assert names(s) == ["proj", "a.txt"] and not os.path.lexists(ws.path("proj/link"))
    assert ws.read("target/keep.txt") == "keep\n"


def test_a_fifo_can_be_deleted(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    os.mkfifo(ws.path("proj/pipe"))
    app = ltt(ws.path("proj"))
    s = ask(app, "pipe")
    assert s.question.startswith('Delete file "pipe" from disk forever?')
    s = app.press("d")
    assert names(s) == ["proj", "a.txt"] and not os.path.lexists(ws.path("proj/pipe"))


def test_a_failed_delete_is_reported(ltt, ws):
    proj(ws)
    os.chmod(ws.path("proj/dir"), 0o555)  # nothing can be removed from it
    try:
        app = ltt(ws.path("proj"))
        select(app, "dir")
        app.press("right")
        ask(app, "a.txt")
        s = app.press("d")
        assert s.status.startswith("Delete error: ") and ws.exists("proj/dir/a.txt")
        assert s.tree_row("a.txt") is not None
    finally:
        os.chmod(ws.path("proj/dir"), 0o755)


def test_the_watcher_does_not_overwrite_the_deleted_message(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    ask(app, "x.txt")
    app.press("d")
    time.sleep(1.5)  # the watcher reports the deletion too; nothing visible changes
    assert app.snap().status == "Deleted from disk: " + ws.path("proj/x.txt")


def test_ctrl_delete_in_the_editor_never_deletes_the_file(ltt, ws):
    ws.write("proj/x.txt", "abc\n")
    app = ltt(ws.path("proj/x.txt"))
    s = app.press("ctrl+delete")
    assert s.mode == "EDITOR" and s.editor_lines[0] == "bc"  # a character, as Delete does
    assert ws.exists("proj/x.txt")


# ---------------------------------------------------------------- open tabs

def test_deleting_a_file_closes_its_tab(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/x.txt"), ws.path("proj/y.txt"), cols=WIDE)
    app.press("esc")
    ask(app, "x.txt")
    s = app.press("d")
    assert s.status == f"Deleted from disk: {ws.path('proj/x.txt')} (tabs closed: 1)"
    assert s.tab_names == ["y.txt"]


def test_deleting_a_folder_closes_the_tabs_inside(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/dir/a.txt"), ws.path("proj/dir/deeper/b.txt"), ws.path("proj/y.txt"),
              cols=WIDE)
    app.press("esc")
    ask(app, "dir")
    s = app.press("d")
    assert s.status == f"Deleted from disk: {ws.path('proj/dir')} (tabs closed: 2)"
    assert s.tab_names == ["y.txt"]


def test_deleting_the_only_tab_leaves_the_tree_focused(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/x.txt"))
    app.press("esc")
    ask(app, "x.txt")
    s = app.press("d")
    assert s.tabs == [] and s.mode == "TREE" and s.editor_hint.startswith("Pick a file on the left")
    assert app.press("tab").status == "Open a file with Enter first."


def test_the_question_warns_about_unsaved_edits_inside_a_folder(ltt, ws):
    # A3: a folder went with the unsaved edits of its open tabs, without a word
    app = ltt(proj(ws), ws.path("proj/dir/deeper/b.txt"), cols=WIDE)
    app.type("EDIT")
    app.press("esc")
    s = ask(app, "dir")
    assert s.question == ('Delete folder with all its contents "dir" from disk forever?'
                          ' 1 open tab(s) there have unsaved edits — they go too!  D deletes · Esc cancels')


def test_the_question_warns_about_unsaved_edits_of_the_file(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/x.txt"), cols=WIDE)
    app.type("EDIT")
    app.press("esc")
    s = ask(app, "x.txt")
    assert "1 open tab(s) there have unsaved edits — they go too!" in s.question


def test_clean_tabs_inside_raise_no_warning(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/dir/a.txt"), cols=WIDE)
    app.press("esc")
    s = ask(app, "dir")
    assert "unsaved" not in s.question


def test_esc_keeps_the_unsaved_edits_of_a_tab_inside(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/dir/a.txt"), cols=WIDE)
    app.type("EDIT")
    app.press("esc")
    ask(app, "dir")
    s = app.press("esc")
    assert s.tabs[0].dirty and ws.read("proj/dir/a.txt") == "aaaa\n"
    s = app.press("tab")
    assert s.editor_lines[0] == "EDITaaaa"


# ---------------------------------------------------------------- never deleted

def test_a_root_is_never_deleted(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    s = app.press("ctrl+delete")
    assert s.mode == "TREE"
    assert s.status == "Roots are never deleted from disk. Plain Delete removes a root from the panel."
    assert ws.exists("proj/x.txt")


def test_a_folder_holding_another_root_is_protected(ltt, ws):
    # W10: only the roots themselves were protected, not the folders above them
    ws.write("top/mid/leaf/f.txt", "f\n")
    ws.write("top/other.txt", "o\n")
    app = ltt(ws.path("top"), ws.path("top/mid/leaf"), cols=WIDE)
    s = ask(app, "mid")
    assert s.mode == "TREE" and s.status == "Protected path - never deleted from disk: " + ws.path("top/mid")
    assert ws.exists("top/mid/leaf/f.txt")


def test_a_folder_that_is_another_root_is_protected(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/dir"), cols=WIDE)
    s = ask(app, "dir")
    assert s.mode == "TREE" and s.status == "Protected path - never deleted from disk: " + ws.path("proj/dir")
    assert ws.exists("proj/dir/a.txt")


def test_a_root_reached_through_a_symlinked_path_is_protected(ltt, ws):
    # V28: paths were compared as written; real/proj is the root alias/proj/src's folder above it
    ws.write("real/proj/src/s.txt", "s\n")
    os.symlink(ws.path("real"), ws.path("alias"))
    app = ltt(ws.path("alias/proj/src"), ws.root, cols=WIDE)
    select(app, "real")
    app.press("right")
    s = ask(app, "proj")
    assert s.status == "Protected path - never deleted from disk: " + ws.path("real/proj")
    s = ask(app, "alias")
    assert s.status == "Protected path - never deleted from disk: " + ws.path("alias")
    assert ws.exists("real/proj/src/s.txt") and os.path.islink(ws.path("alias"))


def test_the_home_folder_is_protected(ltt, ws):
    ws.write("home/notes.txt", "n\n")
    app = ltt(ws.root, cols=WIDE)
    s = ask(app, "home")
    assert s.status == "Protected path - never deleted from disk: " + ws.home
    assert ws.exists("home/notes.txt")


def test_files_in_the_home_folder_can_be_deleted(ltt, ws):
    ws.write("home/notes.txt", "n\n")
    app = ltt(ws.home)
    ask(app, "notes.txt")
    app.press("d")
    assert not ws.exists("home/notes.txt")
