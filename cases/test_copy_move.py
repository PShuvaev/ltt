"""Copying and moving files and folders in the tree: Ctrl+C / Ctrl+X pick an entry up, Ctrl+V pastes it."""
import base64
import os
import time

import pytest

from copy_move_helpers import (NOTHING_PICKED, WIDE, contents, needs_unix_permissions, other_disk, rows,  # noqa: F401
                               select, status_is)


def start(ltt, ws, *roots, cols=160, **kw):
    return ltt(*[ws.path(r) for r in roots], cols=cols, **kw)


# ---------------------------------------------------------------- picking up

@pytest.mark.parametrize("path, label", [("proj/a/notes.txt", "notes.txt"), ("proj/a/docs", "docs/")])
def test_ctrl_c_picks_up_and_help_says_what_ctrl_v_does(ltt, ws, path, label):
    ws.write("proj/a/notes.txt", "n\n")
    ws.write("proj/a/docs/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    select(app, path)
    s = app.press("ctrl+c")
    assert s.status == f"Copied {label} and its path — Ctrl+V in a folder of any root pastes it."
    assert s.help.startswith(f"Ctrl+V copies {label} here · Esc forgets it · ↑↓ select")


def test_ctrl_x_picks_up_and_help_says_ctrl_v_moves(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    s = app.press("ctrl+x")
    assert s.status == "Cut notes.txt — Ctrl+V in a folder of any root moves it there."
    assert s.help.startswith("Ctrl+V moves notes.txt here · Esc forgets it · ↑↓ select")


@pytest.mark.parametrize("name", ["docs", "мои заметки"])
def test_ctrl_c_puts_the_path_on_the_clipboard(ltt, ws, name):
    ws.write(f"proj/{name}/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    select(app, f"proj/{name}")
    mark = app.output_mark()
    app.press("ctrl+c")
    path = ws.path(f"proj/{name}")
    assert b"\x1b]52;c;" + base64.b64encode(path.encode()) + b"\x07" in app.output_since(mark)
    # the fake pbcopy logs its run after it stored the text: wait for both
    app.wait(lambda s: ws.clipboard == path and ws.clipboard_log, timeout=5, what="the fake pbcopy got the path")
    assert len(ws.clipboard_log) == 1 and ws.clipboard_log[0].startswith("copy")


def test_ctrl_x_leaves_the_clipboard_alone(ltt, ws):
    ws.write("proj/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/notes.txt")
    mark = app.output_mark()
    s = app.press("ctrl+x")
    assert s.status.startswith("Cut notes.txt")
    assert b"\x1b]52;" not in app.output_since(mark)
    assert ws.clipboard is None and ws.clipboard_log == []


def test_ctrl_c_in_the_tree_picks_up_even_with_text_selected_in_the_editor(ltt, ws):
    ws.write("proj/doc.txt", "some text\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/doc.txt")
    app.press("enter")
    app.wait_mode("EDITOR")
    app.press("ctrl+a")
    s = app.press("esc")
    assert s.mode == "TREE" and s.pos[3] > 0  # the editor keeps its selection
    s = app.press("ctrl+c")
    assert s.status == "Copied doc.txt and its path — Ctrl+V in a folder of any root pastes it."
    app.wait(lambda s: ws.clipboard == ws.path("proj/doc.txt"), timeout=5, what="the path, not the text, copied")


def test_ctrl_c_in_tree_then_ctrl_v_in_editor_pastes_the_path(ltt, ws):
    ws.write("proj/doc.txt", "text\n")
    ws.mkdir("proj/sub")
    app = start(ltt, ws, "proj")
    select(app, "proj/sub")
    app.press("ctrl+c")
    path = ws.path("proj/sub")
    app.wait(lambda s: ws.clipboard == path, timeout=5, what="the path on the clipboard")
    select(app, "proj/doc.txt")
    app.press("enter")
    app.wait_mode("EDITOR")
    app.press("ctrl+v")
    status_is(app, f"Pasted: {len(path)} chars")
    app.press("ctrl+s")
    app.wait_status("Saved: doc.txt")
    assert ws.read("proj/doc.txt") == path + "text\n"


def test_picked_entry_is_magenta_while_not_selected(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    s = app.press("ctrl+c")
    assert rows(s)["proj/a/notes.txt"].fg == "White"  # selected: the selection colours win
    s = select(app, "proj/b")
    assert rows(s)["proj/a/notes.txt"].fg == "Magenta"
    s = select(app, "proj/a")
    assert rows(s)["proj/a/notes.txt"].fg == "Magenta"


def test_picked_folder_is_magenta_too(ltt, ws):
    ws.write("proj/docs/one.txt", "one\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+x")
    s = select(app, "proj/b")
    assert rows(s)["proj/docs"].fg == "Magenta"


def test_esc_forgets_the_picked_entry(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj/b")
    s = app.press("esc")
    assert s.status == "notes.txt is no longer picked up."
    assert rows(s)["proj/a/notes.txt"].fg == "Gray"
    assert s.help.startswith("↑↓ select")
    s = app.press("ctrl+v")
    assert s.status == NOTHING_PICKED
    assert ws.files("proj") == ["a/notes.txt"]


def test_ctrl_v_with_nothing_picked_up_explains(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a")
    s = app.press("ctrl+v")
    assert s.status == NOTHING_PICKED
    assert ws.files("proj") == ["a/notes.txt"]


def test_second_pick_up_replaces_the_first(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    ws.write("proj/b.txt", "bb\n")
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/a.txt")
    app.press("ctrl+c")
    select(app, "proj/b.txt")
    app.press("ctrl+x")
    s = select(app, "proj/dst")
    assert rows(s)["proj/a.txt"].fg == "Gray" and rows(s)["proj/b.txt"].fg == "Magenta"
    app.press("ctrl+v")
    status_is(app, "Moved b.txt into dst/.")
    assert ws.files("proj") == ["a.txt", "dst/b.txt"]


def test_help_hint_shows_only_in_the_tree(ltt, ws):
    ws.write("proj/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/notes.txt")
    app.press("enter")
    app.wait_mode("EDITOR")
    app.press("esc")
    app.press("ctrl+c")
    s = app.press("tab")
    assert s.mode == "EDITOR" and s.help.startswith("Esc to tree ·")
    s = app.press("esc")
    assert s.mode == "TREE" and s.help.startswith("Ctrl+V copies notes.txt here · Esc forgets it")


def test_esc_forgets_the_pick_before_it_hides_search_results(ltt, ws):
    ws.write("proj/x/needle.txt", "a needle here\n")
    ws.write("proj/y/other.txt", "nothing\n")
    app = start(ltt, ws, "proj")
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: s.results, what="search results")
    app.press("enter")  # opens the hit; the results panel stays
    app.wait_mode("EDITOR")
    app.press("esc")
    select(app, "proj/y")
    app.press("ctrl+c")
    s = app.press("esc")
    assert s.status == "y/ is no longer picked up." and s.results_top is not None
    s = app.press("esc")
    assert s.status == "Search panel hidden (Ctrl+G brings it back)." and s.results_top is None


def test_ctrl_v_over_focused_search_results_pastes_nothing(ltt, ws):
    ws.write("proj/x/needle.txt", "a needle here\n")
    ws.mkdir("proj/y")
    app = start(ltt, ws, "proj")
    select(app, "proj/x/needle.txt")
    app.press("ctrl+c")
    select(app, "proj")  # Ctrl+F searches the selected folder; Ctrl+V here would paste into proj
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: s.results, what="search results")
    s = app.press("esc")  # to the results panel
    assert s.mode == "SEARCH"
    s = app.press("ctrl+v")
    assert s.mode == "SEARCH" and s.status.startswith("Found: 1.")
    assert ws.files("proj") == ["x/needle.txt"]


# ---------------------------------------------------------------- copying

def test_copy_file_into_another_folder(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.write("proj/b/keep.txt", "keep\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Copied notes.txt into b/.")
    assert ws.read("proj/b/notes.txt") == "hello\n" and ws.read("proj/a/notes.txt") == "hello\n"
    assert rows(s)["proj/b/notes.txt"].selected and s.title_path.endswith("/proj/b/notes.txt")
    assert s.help.startswith("Ctrl+V copies notes.txt here")  # still picked up: it can be pasted again


def test_copy_folder_with_everything_inside(ltt, ws):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.write("proj/a/docs/sub/two.txt", "two\n")
    ws.write("proj/a/docs/sub/deeper/bytes.bin", bytes(range(256)) * 40)
    ws.write("proj/a/docs/run.sh", "#!/bin/sh\necho hi\n", mode=0o755)
    ws.mkdir("proj/a/docs/empty")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/docs")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Copied docs/ into b/ (4 files).")
    assert contents(ws.path("proj/b/docs")) == contents(ws.path("proj/a/docs"))
    assert os.path.isdir(ws.path("proj/b/docs/empty"))
    assert os.stat(ws.path("proj/b/docs/run.sh")).st_mode & 0o777 == 0o755


def test_paste_with_a_file_selected_goes_into_its_folder(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.write("proj/b/keep.txt", "k\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj/b/keep.txt")
    app.press("ctrl+v")
    status_is(app, "Copied notes.txt into b/.")
    assert ws.read("proj/b/notes.txt") == "n\n"


def test_paste_into_a_root(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj")
    app.press("ctrl+v")
    s = status_is(app, "Copied notes.txt into proj/.")
    assert ws.read("proj/notes.txt") == "n\n" and rows(s)["proj/notes.txt"].selected


def test_paste_into_a_collapsed_folder_shows_the_copy(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.write("proj/b/deep/keep.txt", "k\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    s = select(app, "proj/b")
    assert not rows(s)["proj/b"].expanded
    app.press("ctrl+v")
    s = status_is(app, "Copied notes.txt into b/.")
    assert rows(s)["proj/b"].expanded and rows(s)["proj/b/notes.txt"].selected


def test_copy_between_roots(ltt, ws):
    ws.write("A/docs/one.txt", "one\n")
    ws.write("A/docs/sub/two.txt", "two\n")
    ws.write("B/keep.txt", "k\n")
    app = start(ltt, ws, "A", "B")
    select(app, "A/docs")
    app.press("ctrl+c")
    select(app, "B")
    app.press("ctrl+v")
    s = status_is(app, "Copied docs/ into B/ (2 files).")
    assert contents(ws.path("B/docs")) == contents(ws.path("A/docs"))
    assert rows(s)["B/docs"].selected


def test_copy_a_whole_root_into_another_root(ltt, ws):
    ws.write("A/one.txt", "one\n")
    ws.write("A/sub/two.txt", "two\n")
    ws.mkdir("B")
    app = start(ltt, ws, "A", "B")
    s = select(app, "A")
    s = app.press("ctrl+c")
    assert s.status == "Copied A/ and its path — Ctrl+V in a folder of any root pastes it."
    select(app, "B")
    app.press("ctrl+v")
    s = status_is(app, "Copied A/ into B/ (2 files).")
    assert contents(ws.path("B/A")) == {"one.txt": b"one\n", os.path.join("sub", "two.txt"): b"two\n"}
    assert ws.files("A") == ["one.txt", "sub/two.txt"]
    assert rows(s)["B/A"].selected and rows(s)["A"].fg == "Magenta"


def test_copy_into_the_same_folder_numbers_the_name(ltt, ws):
    ws.write("proj/docs/notes.txt", "n\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs/notes.txt")
    app.press("ctrl+c")
    app.press("ctrl+v")  # its own folder: the selected file's folder
    s = status_is(app, "Copied notes.txt into docs/ as notes 2.txt.")
    assert rows(s)["proj/docs/notes 2.txt"].selected
    app.press("ctrl+v")
    status_is(app, "Copied notes.txt into docs/ as notes 3.txt.")
    assert ws.files("proj/docs") == ["notes 2.txt", "notes 3.txt", "notes.txt"]
    assert ws.read("proj/docs/notes 3.txt") == "n\n"


def test_copy_folder_next_to_itself_numbers_the_name(ltt, ws):
    ws.write("proj/docs/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+c")
    select(app, "proj")
    app.press("ctrl+v")
    s = status_is(app, "Copied docs/ into proj/ as docs 2 (1 files).")
    assert rows(s)["proj/docs 2"].selected  # the copy is selected: the next Ctrl+V would go inside it
    select(app, "proj")
    app.press("ctrl+v")
    status_is(app, "Copied docs/ into proj/ as docs 3 (1 files).")
    assert ws.files("proj") == ["docs 2/one.txt", "docs 3/one.txt", "docs/one.txt"]


def test_taken_name_is_never_overwritten(ltt, ws):
    ws.write("proj/a/notes.txt", "new\n")
    ws.write("proj/b/notes.txt", "old\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Copied notes.txt into b/ as notes 2.txt.")
    assert ws.read("proj/b/notes.txt") == "old\n" and ws.read("proj/b/notes 2.txt") == "new\n"


@pytest.mark.parametrize("taken_by", ["folder", "file", "dangling link"])
def test_name_taken_by_another_kind_of_entry_is_never_replaced(ltt, ws, taken_by):
    if taken_by == "folder":  # a file pasted where a folder has its name
        ws.write("proj/a/data", "new\n")
        ws.write("proj/b/data/keep.txt", "k\n")
    elif taken_by == "file":  # a folder pasted where a file has its name
        ws.write("proj/a/data/inside.txt", "i\n")
        ws.write("proj/b/data", "old\n")
    else:
        ws.write("proj/a/data", "new\n")
        ws.mkdir("proj/b")
        os.symlink("nowhere", ws.path("proj/b/data"))
    app = start(ltt, ws, "proj")
    select(app, "proj/a/data")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    app.wait_status(" as data 2")
    assert ws.exists("proj/b/data 2")
    if taken_by == "dangling link":
        assert os.readlink(ws.path("proj/b/data")) == "nowhere"
    else:
        assert os.path.isdir(ws.path("proj/b/data")) == (taken_by == "folder")


def test_names_with_spaces_and_other_alphabets(ltt, ws):
    ws.write("proj/папка b/заметки 1.txt", "текст\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/папка b/заметки 1.txt")
    s = app.press("ctrl+c")
    assert s.status.startswith("Copied заметки 1.txt and its path")
    app.press("ctrl+v")
    status_is(app, "Copied заметки 1.txt into папка b/ as заметки 1 2.txt.")
    assert ws.read("proj/папка b/заметки 1 2.txt") == "текст\n"


@pytest.mark.parametrize("name, is_dir, second", [
    ("archive.tar.gz", False, "archive.tar 2.gz"),  # the number goes before the last extension
    (".env", False, ".env 2"),                      # a dot file has no extension
    ("Makefile", False, "Makefile 2"),
    ("v1.2", True, "v1.2 2"),                       # a folder's name has no extension
])
def test_number_goes_before_a_files_extension(ltt, ws, name, is_dir, second):
    if is_dir:
        ws.write(f"proj/{name}/inside.txt", "i\n")
    else:
        ws.write(f"proj/{name}", "x\n")
    app = start(ltt, ws, "proj")
    select(app, f"proj/{name}")
    app.press("ctrl+c")
    select(app, "proj")
    app.press("ctrl+v")
    s = app.wait_status(f" as {second}")
    assert ws.exists(f"proj/{second}") and rows(s)[f"proj/{second}"].selected


def test_name_differing_only_in_case_is_not_overwritten(ltt, ws):
    ws.write("proj/src/Notes.txt", "upper\n")
    ws.write("proj/dst/notes.txt", "lower\n")
    case_blind = os.path.exists(ws.path("proj/dst/NOTES.TXT"))
    app = start(ltt, ws, "proj")
    select(app, "proj/src/Notes.txt")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    if case_blind:  # macOS: Notes.txt and notes.txt are one file
        status_is(app, "Copied Notes.txt into dst/ as Notes 2.txt.")
        assert sorted(os.listdir(ws.path("proj/dst"))) == ["Notes 2.txt", "notes.txt"]
    else:
        status_is(app, "Copied Notes.txt into dst/.")
        assert sorted(os.listdir(ws.path("proj/dst"))) == ["Notes.txt", "notes.txt"]
    assert ws.read("proj/dst/notes.txt") == "lower\n"


def test_copy_is_green_as_new_once_not_selected(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.write("proj/b/keep.txt", "k\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Copied notes.txt into b/.")
    assert rows(s)["proj/b/notes.txt"].fg == "White"  # selected
    s = select(app, "proj/b/keep.txt")
    r = rows(s)
    assert r["proj/b/notes.txt"].fg == "Green" and r["proj/b/keep.txt"].fg == "White"
    assert r["proj/a/notes.txt"].fg == "Magenta"  # the original is still picked up


def test_files_inside_a_pasted_folder_are_green(ltt, ws):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/docs")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Copied docs/ into b/ (1 files).")
    app.press("right")  # expand the selected copy
    s = select(app, "proj/b")
    assert rows(s)["proj/b/docs"].fg == "Green" and rows(s)["proj/b/docs/one.txt"].fg == "Green"


def test_copy_in_a_git_repository_shows_as_new(ltt, ws):
    ws.repo("proj", {"a/one.txt": "one\n", "b/keep.txt": "keep\n"})
    app = start(ltt, ws, "proj")
    select(app, "proj/a/one.txt")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Copied one.txt into b/.")
    select(app, "proj/a")
    app.wait(lambda s: rows(s)["proj/b/one.txt"].fg == "Green" and rows(s)["proj/b/keep.txt"].fg == "Gray",
             what="git reports the copy as untracked")


def test_copy_of_a_file_with_unsaved_edits_copies_the_disk_version(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj", cols=WIDE)
    select(app, "proj/a/notes.txt")
    app.press("enter")
    app.wait_mode("EDITOR")
    app.type("X")
    app.press("esc")
    app.press("ctrl+c")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Copied notes.txt into b/.")
    assert ws.read("proj/b/notes.txt") == "hello\n"
    s = app.press("tab")
    assert s.title_path == ws.path("proj/a/notes.txt") and s.tabs[0].dirty and s.editor_lines[0] == "Xhello"


# ---------------------------------------------------------------- what a paste refuses

def test_folder_pasted_into_itself_is_refused(ltt, ws):
    ws.write("proj/docs/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+c")
    s = app.press("ctrl+v")
    assert s.status == "docs/ cannot go inside itself."
    assert ws.files("proj") == ["docs/one.txt"]


@pytest.mark.parametrize("target", ["proj/docs/sub", "proj/docs/sub/deep", "proj/docs/one.txt"])
def test_folder_pasted_inside_itself_is_refused(ltt, ws, target):
    ws.write("proj/docs/one.txt", "one\n")
    ws.write("proj/docs/sub/deep/two.txt", "two\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+c")
    select(app, target)
    s = app.press("ctrl+v")
    assert s.status == "docs/ cannot go inside itself."
    assert ws.files("proj") == ["docs/one.txt", "docs/sub/deep/two.txt"]


def test_folder_pasted_through_a_link_into_itself_is_refused(ltt, ws):
    ws.write("proj/docs/sub/two.txt", "two\n")
    os.symlink(ws.path("proj/docs/sub"), ws.path("proj/alias"))  # proj/alias is docs/sub
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+c")
    select(app, "proj/alias")
    s = app.press("ctrl+v")
    assert s.status == "docs/ cannot go inside itself."
    assert ws.files("proj/docs") == ["sub/two.txt"]


def test_root_pasted_into_its_own_folder_is_refused(ltt, ws):
    ws.write("proj/a/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    app.press("ctrl+c")  # the root is selected at the start
    select(app, "proj/a")
    s = app.press("ctrl+v")
    assert s.status == "proj/ cannot go inside itself."
    assert ws.files("proj") == ["a/one.txt"]


def test_picked_entry_gone_from_disk_is_forgotten(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+c")
    ws.remove("proj/a/notes.txt")
    app.wait(lambda s: "proj/a/notes.txt" not in rows(s), what="the tree drops the deleted file")
    select(app, "proj/b")
    s = app.press("ctrl+v")
    assert s.status == "notes.txt is no longer on disk."
    assert s.help.startswith("↑↓ select")
    assert app.press("ctrl+v").status == NOTHING_PICKED
    assert ws.files("proj") == []


# ---------------------------------------------------------------- moving

def test_move_file_into_another_folder(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.write("proj/b/keep.txt", "k\n")
    inode = os.stat(ws.path("proj/a/notes.txt")).st_ino
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Moved notes.txt into b/.")
    assert ws.files("proj") == ["b/keep.txt", "b/notes.txt"]
    assert os.stat(ws.path("proj/b/notes.txt")).st_ino == inode  # one disk: a rename, not a copy
    r = rows(s)
    assert "proj/a/notes.txt" not in r and r["proj/b/notes.txt"].selected
    assert s.help.startswith("↑↓ select")  # nothing is picked up any more


def test_moved_entry_is_green_in_its_new_place(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.write("proj/b/keep.txt", "k\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    s = select(app, "proj/b/keep.txt")
    assert rows(s)["proj/b/notes.txt"].fg == "Green"


def test_move_folder_into_another_folder(ltt, ws):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.write("proj/a/docs/sub/two.txt", "two\n")
    ws.mkdir("proj/b")
    before = contents(ws.path("proj/a/docs"))
    app = start(ltt, ws, "proj")
    select(app, "proj/a/docs")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Moved docs/ into b/.")
    assert contents(ws.path("proj/b/docs")) == before and not ws.exists("proj/a/docs")
    assert "proj/a/docs" not in rows(s) and rows(s)["proj/b/docs"].selected


def test_move_between_roots(ltt, ws):
    ws.write("A/notes.txt", "n\n")
    ws.mkdir("B")
    app = start(ltt, ws, "A", "B")
    select(app, "A/notes.txt")
    app.press("ctrl+x")
    select(app, "B")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into B/.")
    assert ws.files("A") == [] and ws.files("B") == ["notes.txt"]


@pytest.mark.parametrize("path, label, target", [
    ("proj/a/notes.txt", "notes.txt", "proj/a"),
    ("proj/a/notes.txt", "notes.txt", "proj/a/notes.txt"),
    ("proj/a/notes.txt", "notes.txt", "proj/a/other.txt"),
    ("proj/a/docs", "docs/", "proj/a"),
])
def test_move_into_its_own_folder_is_already_here(ltt, ws, path, label, target):
    ws.write("proj/a/notes.txt", "n\n")
    ws.write("proj/a/other.txt", "o\n")
    ws.write("proj/a/docs/one.txt", "one\n")
    app = start(ltt, ws, "proj")
    select(app, path)
    app.press("ctrl+x")
    select(app, target)
    s = app.press("ctrl+v")
    assert s.status == f"{label} is already here."
    assert ws.files("proj") == ["a/docs/one.txt", "a/notes.txt", "a/other.txt"]
    assert s.help.startswith(f"Ctrl+V moves {label} here")


def test_move_onto_a_taken_name_numbers_it(ltt, ws):
    ws.write("proj/a/notes.txt", "new\n")
    ws.write("proj/b/notes.txt", "old\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/ as notes 2.txt.")
    assert ws.read("proj/b/notes.txt") == "old\n" and ws.read("proj/b/notes 2.txt") == "new\n"
    assert not ws.exists("proj/a/notes.txt")


def test_folder_may_go_into_a_sibling_whose_name_starts_like_it(ltt, ws):
    ws.write("proj/docs/one.txt", "one\n")
    ws.write("proj/docs2/two.txt", "two\n")  # not inside docs, though "docs2" starts with "docs"
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+x")
    select(app, "proj/docs2")
    app.press("ctrl+v")
    status_is(app, "Moved docs/ into docs2/.")
    assert ws.files("proj") == ["docs2/docs/one.txt", "docs2/two.txt"]


def test_folder_cut_into_its_subfolder_is_refused(ltt, ws):
    ws.write("proj/docs/sub/two.txt", "two\n")
    app = start(ltt, ws, "proj")
    select(app, "proj/docs")
    app.press("ctrl+x")
    select(app, "proj/docs/sub")
    s = app.press("ctrl+v")
    assert s.status == "docs/ cannot go inside itself."
    assert ws.files("proj") == ["docs/sub/two.txt"]


def test_nothing_to_paste_after_a_move(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    select(app, "proj/a/notes.txt")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    select(app, "proj/a")
    assert app.press("ctrl+v").status == NOTHING_PICKED
    assert ws.files("proj") == ["b/notes.txt"]


def test_ctrl_x_on_a_root_is_refused(ltt, ws):
    ws.write("proj/a/notes.txt", "n\n")
    ws.mkdir("other")
    app = start(ltt, ws, "proj", "other")
    s = app.press("ctrl+x")  # the root proj is selected at the start
    assert s.status == "A root stays where it is: Ctrl+C copies it instead."
    assert s.help.startswith("↑↓ select")
    select(app, "other")
    assert app.press("ctrl+v").status == NOTHING_PICKED
    assert ws.files("proj") == ["a/notes.txt"] and ws.files("other") == []


def test_ctrl_x_on_a_folder_holding_another_root_is_refused(ltt, ws):
    # W10: delete protection covers the folders above other roots; the same protection stops a move
    ws.write("proj/outer/inner/y.txt", "y\n")
    app = start(ltt, ws, "proj", "proj/outer/inner", cols=WIDE)
    select(app, "proj/outer")
    s = app.press("ctrl+x")
    assert s.status == f"Protected path - never moved: {ws.path('proj/outer')}"
    assert s.help.startswith("↑↓ select")


def test_ctrl_x_on_a_folder_holding_a_root_opened_through_a_link_is_refused(ltt, ws):
    # V28: the protection compares real paths; the second root is written through a link into proj/outer
    ws.write("proj/outer/inner/y.txt", "y\n")
    os.symlink(ws.path("proj/outer"), ws.path("via"))
    app = start(ltt, ws, "proj", "via/inner", cols=WIDE)
    select(app, "proj/outer")
    s = app.press("ctrl+x")
    assert s.status == f"Protected path - never moved: {ws.path('proj/outer')}"


@pytest.mark.parametrize("cut", ["proj/me", "proj/above"])
def test_ctrl_x_on_the_home_folder_or_above_it_is_refused(ltt, ws, cut):
    ws.write("proj/me/x.txt", "x\n")
    ws.write("proj/above/me/y.txt", "y\n")
    home = ws.path("proj/me") if cut == "proj/me" else ws.path("proj/above/me")
    app = start(ltt, ws, "proj", cols=WIDE, env={"HOME": home})
    select(app, cut)
    s = app.press("ctrl+x")
    assert s.status == f"Protected path - never moved: {ws.path(cut)}"


def test_paste_refuses_a_cut_folder_that_now_holds_a_root(ltt, ws):
    ws.write("proj/a/inner/z.txt", "z\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj", cols=WIDE)
    select(app, "proj/a")
    app.press("ctrl+x")
    app.press("ctrl+o")
    app.paste(ws.path("proj/a/inner"))
    app.press("enter")
    app.wait_status("Folder added to the left panel")
    select(app, "proj/b")
    s = app.press("ctrl+v")
    assert s.status == f"Protected path - never moved: {ws.path('proj/a')}"
    assert ws.files("proj") == ["a/inner/z.txt"]


# ---------------------------------------------------------------- open tabs follow a move

def _open(app, path):
    select(app, path)
    app.press("enter")
    app.wait_mode("EDITOR")


def test_moved_file_keeps_its_tab(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj", cols=WIDE)
    _open(app, "proj/a/notes.txt")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    s = app.press("tab")
    assert s.title_path == ws.path("proj/b/notes.txt")
    assert s.tab_names == ["notes.txt"] and s.editor_lines[0] == "hello"
    time.sleep(1.2)  # past a check of open tabs against the disk: the file is not gone
    s = app.snap()
    assert not s.tabs[0].missing and not s.tabs[0].changed and not s.tabs[0].dirty


def test_moved_dirty_tab_saves_to_the_new_place(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    _open(app, "proj/a/notes.txt")
    app.type("X")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    s = app.press("tab")
    assert s.tabs[0].dirty
    app.press("ctrl+s")
    status_is(app, "Saved: notes.txt")
    assert ws.read("proj/b/notes.txt") == "Xhello\n" and not ws.exists("proj/a/notes.txt")


def test_moved_tab_keeps_its_undo_history(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    _open(app, "proj/a/notes.txt")
    app.type("X")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    app.press("tab")
    s = app.press("ctrl+z")
    assert s.editor_lines[0] == "hello" and s.status == "Undone. Ctrl+Y redoes it."


def test_moving_a_folder_moves_the_tabs_inside(ltt, ws):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.write("proj/a/docs/sub/two.txt", "two\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj", cols=WIDE)
    _open(app, "proj/a/docs/one.txt")
    app.press("esc")
    _open(app, "proj/a/docs/sub/two.txt")
    app.type("2")
    app.press("esc")
    select(app, "proj/a/docs")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved docs/ into b/.")
    s = app.press("tab")
    assert s.title_path == ws.path("proj/b/docs/sub/two.txt")
    app.press("ctrl+s")
    status_is(app, "Saved: two.txt")
    s = app.press("alt+1")
    assert s.title_path == ws.path("proj/b/docs/one.txt") and s.editor_lines[0] == "one"
    assert ws.files("proj") == ["b/docs/one.txt", "b/docs/sub/two.txt"]
    assert ws.read("proj/b/docs/sub/two.txt") == "2two\n"


def test_tab_changed_outside_before_a_move_still_asks_before_saving(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    _open(app, "proj/a/notes.txt")
    app.type("X")
    app.press("esc")
    ws.write("proj/a/notes.txt", "an agent wrote this\n")
    app.wait(lambda s: s.tabs[0].changed, what="the tab marked as changed on disk")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Moved notes.txt into b/.")
    assert s.tabs[0].changed
    app.press("tab")
    s = app.press("ctrl+s")
    assert s.mode == "CONFIRM" and s.question.startswith("notes.txt changed on disk since it was opened!")
    app.press("esc")
    assert ws.read("proj/b/notes.txt") == "an agent wrote this\n"


def test_moved_tab_follows_a_numbered_name(ltt, ws):
    ws.write("proj/a/notes.txt", "new\n")
    ws.write("proj/b/notes.txt", "old\n")
    app = start(ltt, ws, "proj", cols=WIDE)
    _open(app, "proj/a/notes.txt")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/ as notes 2.txt.")
    s = app.press("tab")
    assert s.title_path == ws.path("proj/b/notes 2.txt") and s.tab_names == ["notes 2.txt"]
    assert s.editor_lines[0] == "new"


def test_moved_clean_tab_follows_changes_at_its_new_path(ltt, ws):
    ws.write("proj/a/notes.txt", "hello\n")
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj")
    _open(app, "proj/a/notes.txt")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into b/.")
    ws.write("proj/b/notes.txt", "changed at the new place\n")
    app.wait(lambda s: s.editor_lines[:1] == ["changed at the new place"], what="the tab reloads its moved file")


def test_move_leaves_tabs_of_a_folder_with_a_longer_name_alone(ltt, ws):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.write("proj/a/docs2/other.txt", "other\n")  # "docs2/…" starts with "docs" too
    ws.mkdir("proj/b")
    app = start(ltt, ws, "proj", cols=WIDE)
    _open(app, "proj/a/docs2/other.txt")
    app.press("esc")
    select(app, "proj/a/docs")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    status_is(app, "Moved docs/ into b/.")
    s = app.press("tab")
    assert s.title_path == ws.path("proj/a/docs2/other.txt") and not s.tabs[0].missing


@pytest.mark.xfail(strict=True, reason="BUG: a file moved onto the path of a tab whose file was deleted "
                                       "leaves two tabs on one path (V19 for ltt's own moves)")
def test_file_moved_onto_the_path_of_a_tab_whose_file_is_gone(ltt, ws):
    ws.write("proj/a/note.txt", "from a\n")
    ws.write("proj/b/note.txt", "from b\n")
    app = start(ltt, ws, "proj")
    _open(app, "proj/b/note.txt")
    app.press("esc")
    _open(app, "proj/a/note.txt")
    app.press("esc")
    ws.remove("proj/b/note.txt")
    app.wait(lambda s: s.tabs[0].missing, what="the first tab marked as gone from disk")
    app.press("ctrl+x")
    select(app, "proj/b")
    app.press("ctrl+v")
    s = status_is(app, "Moved note.txt into b/.")
    labels = s.tab_names
    assert len(set(labels)) == len(labels), f"two tabs on one path: {labels}"


# ---------------------------------------------------------------- links, pipes, unreadable entries

def test_links_in_a_copied_folder_stay_links(ltt, ws):
    ws.write("proj/src/top.txt", "top\n")
    os.symlink("top.txt", ws.path("proj/src/link.txt"))
    os.symlink("nowhere.txt", ws.path("proj/src/dangling.txt"))
    os.symlink(ws.path("proj/src"), ws.path("proj/src/loop"))  # followed, the copy would never end
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/src")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    status_is(app, "Copied src/ into dst/ (4 files).")
    copy = ws.path("proj/dst/src")
    assert {n: os.readlink(os.path.join(copy, n)) for n in ("link.txt", "dangling.txt", "loop")} == {
        "link.txt": "top.txt", "dangling.txt": "nowhere.txt", "loop": ws.path("proj/src")}
    assert ws.read("proj/dst/src/top.txt") == "top\n" and not os.path.islink(os.path.join(copy, "top.txt"))


def test_copy_of_a_link_to_a_folder_is_a_link(ltt, ws):
    ws.write("proj/real/a.txt", "a\n")
    os.symlink(ws.path("proj/real"), ws.path("proj/alias"))
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/alias")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    status_is(app, "Copied alias/ into dst/ (1 files).")
    assert os.readlink(ws.path("proj/dst/alias")) == ws.path("proj/real")


def test_link_to_a_folder_may_go_inside_that_folder(ltt, ws):
    ws.write("proj/real/a.txt", "a\n")
    os.symlink(ws.path("proj/real"), ws.path("proj/alias"))
    app = start(ltt, ws, "proj")
    select(app, "proj/alias")
    app.press("ctrl+c")
    select(app, "proj/real")
    app.press("ctrl+v")
    status_is(app, "Copied alias/ into real/ (1 files).")  # just a link: nothing to recurse into
    assert os.readlink(ws.path("proj/real/alias")) == ws.path("proj/real")


def test_pipe_in_a_copied_folder_is_skipped(ltt, ws):
    ws.write("proj/src/a.txt", "a\n")
    os.mkfifo(ws.path("proj/src/pipe"))
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/src")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    status_is(app, "Copied src/ into dst/ (1 files). Skipped 1 pipe(s) or device(s).")
    assert sorted(os.listdir(ws.path("proj/dst/src"))) == ["a.txt"]


def test_copy_of_a_lone_pipe_creates_nothing(ltt, ws):
    ws.mkdir("proj")
    os.mkfifo(ws.path("proj/pipe"))
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/pipe")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    app.wait_status("Skipped 1 pipe(s) or device(s).")
    assert os.listdir(ws.path("proj/dst")) == []


@pytest.mark.xfail(strict=True, reason="BUG: copying a lone pipe says 'Copied pipe into dst/.' though nothing was copied")
def test_copy_of_a_lone_pipe_does_not_claim_a_copy(ltt, ws):
    ws.mkdir("proj")
    os.mkfifo(ws.path("proj/pipe"))
    ws.mkdir("proj/dst")
    app = start(ltt, ws, "proj")
    select(app, "proj/pipe")
    app.press("ctrl+c")
    select(app, "proj/dst")
    app.press("ctrl+v")
    s = app.wait_status("Skipped 1 pipe(s) or device(s).")
    assert not s.status.startswith("Copied pipe into dst/")


@needs_unix_permissions
def test_unreadable_file_in_a_copied_folder_is_reported(ltt, ws):
    ws.write("proj/docs/one.txt", "one\n")
    secret = ws.write("proj/docs/secret.txt", "s\n", mode=0o000)
    ws.mkdir("proj/b")
    try:
        app = start(ltt, ws, "proj", cols=WIDE)
        select(app, "proj/docs")
        app.press("ctrl+c")
        select(app, "proj/b")
        app.press("ctrl+v")
        status_is(app, f"Copied docs/ into b/ (1 files). 1 failed: Access to the path '{secret}' is denied.")
        assert ws.files("proj/b") == ["docs/one.txt"]
    finally:
        os.chmod(secret, 0o644)


@needs_unix_permissions
def test_copy_of_an_unreadable_file_is_an_error(ltt, ws):
    secret = ws.write("proj/secret.txt", "s\n", mode=0o000)
    ws.mkdir("proj/b")
    try:
        app = start(ltt, ws, "proj", cols=WIDE)
        select(app, "proj/secret.txt")
        app.press("ctrl+c")
        select(app, "proj/b")
        app.press("ctrl+v")
        status_is(app, f"Copy error: Access to the path '{secret}' is denied.")
        assert ws.files("proj/b") == []
    finally:
        os.chmod(secret, 0o644)


@needs_unix_permissions
def test_copy_into_a_read_only_folder_is_an_error(ltt, ws):
    ws.write("proj/notes.txt", "n\n")
    ro = ws.mkdir("proj/ro")
    os.chmod(ro, 0o555)
    try:
        app = start(ltt, ws, "proj", cols=WIDE)
        select(app, "proj/notes.txt")
        app.press("ctrl+c")
        select(app, "proj/ro")
        app.press("ctrl+v")
        status_is(app, f"Copy error: Access to the path '{ws.path('proj/ro/notes.txt')}' is denied.")
        assert os.listdir(ro) == []
    finally:
        os.chmod(ro, 0o755)


@needs_unix_permissions
def test_failed_folder_move_leaves_everything_in_place(ltt, ws):
    ws.write("proj/ro/docs/one.txt", "one\n")
    ws.mkdir("proj/b")
    ro = ws.path("proj/ro")
    os.chmod(ro, 0o555)  # docs cannot leave a read-only folder
    try:
        app = start(ltt, ws, "proj", cols=WIDE)
        select(app, "proj/ro/docs")
        app.press("ctrl+x")
        select(app, "proj/b")
        app.press("ctrl+v")
        s = app.wait_status("Move error: ")
        assert ws.files("proj") == ["ro/docs/one.txt"]
        assert s.help.startswith("Ctrl+V moves docs/ here")  # still picked up
    finally:
        os.chmod(ro, 0o755)


@needs_unix_permissions
def test_failed_file_move_leaves_nothing_at_the_target(ltt, ws):
    ws.write("proj/ro/notes.txt", "n\n")
    ws.mkdir("proj/b")
    ro = ws.path("proj/ro")
    os.chmod(ro, 0o555)
    try:
        app = start(ltt, ws, "proj", cols=WIDE)
        select(app, "proj/ro/notes.txt")
        app.press("ctrl+x")
        select(app, "proj/b")
        app.press("ctrl+v")
        status_is(app, f"Move error: Access to the path '{ws.path('proj/ro/notes.txt')}' is denied.")
        assert ws.files("proj") == ["ro/notes.txt"]
    finally:
        os.chmod(ro, 0o755)


# ---------------------------------------------------------------- another disk

def test_folder_copied_to_another_disk(ws, other_disk, ltt):
    ws.write("proj/docs/one.txt", "one\n")
    ws.write("proj/docs/sub/two.txt", "two\n")
    app = ltt(ws.path("proj"), other_disk, cols=160)
    select(app, "proj/docs")
    app.press("ctrl+c")
    select(app, "C")
    app.press("ctrl+v")
    s = status_is(app, "Copied docs/ into C/ (2 files).")
    assert contents(os.path.join(other_disk, "docs")) == contents(ws.path("proj/docs"))
    assert rows(s)["C/docs"].selected
    assert app.quit() == 0


def test_file_moved_to_another_disk(ws, other_disk, ltt):
    ws.write("proj/a/notes.txt", "hello\n")
    app = ltt(ws.path("proj"), other_disk, cols=160)
    select(app, "proj/a/notes.txt")
    app.press("ctrl+x")
    select(app, "C")
    app.press("ctrl+v")
    s = status_is(app, "Moved notes.txt into C/.")
    assert not ws.exists("proj/a/notes.txt")
    with open(os.path.join(other_disk, "notes.txt")) as f:
        assert f.read() == "hello\n"
    assert rows(s)["C/notes.txt"].selected and "proj/a/notes.txt" not in rows(s)
    assert app.quit() == 0


def test_dirty_tab_of_a_file_moved_to_another_disk_saves_without_asking(ws, other_disk, ltt):
    # The other disk keeps whole seconds only: the moved file's time differs, but the tab knew
    # the file as it was, so it takes the new time and saving does not ask about a change.
    path = ws.write("proj/a/notes.txt", "hello\n")
    os.utime(path, ns=(1_700_000_000_123_456_789, 1_700_000_000_123_456_789))
    app = ltt(ws.path("proj"), other_disk, cols=160)
    _open(app, "proj/a/notes.txt")
    app.type("X")
    app.press("esc")
    app.press("ctrl+x")
    select(app, "C")
    app.press("ctrl+v")
    status_is(app, "Moved notes.txt into C/.")
    moved = os.path.join(other_disk, "notes.txt")
    assert os.stat(moved).st_mtime_ns != 1_700_000_000_123_456_789  # the premise: the time changed
    app.press("tab")
    s = app.press("ctrl+s")
    assert s.mode == "EDITOR"
    status_is(app, "Saved: notes.txt")
    with open(moved) as f:
        assert f.read() == "Xhello\n"
    assert app.quit() == 0


def test_folder_moved_to_another_disk(ws, other_disk, ltt):
    ws.write("proj/a/docs/one.txt", "one\n")
    ws.write("proj/a/docs/sub/two.txt", "two\n")
    before = contents(ws.path("proj/a/docs"))
    app = ltt(ws.path("proj"), other_disk, cols=WIDE)
    select(app, "proj/a/docs")
    app.press("ctrl+x")
    select(app, "C")
    app.press("ctrl+v")
    s = app.wait(lambda s: s.status.startswith(("Moved", "Move error", "Copied")), what="the move's outcome")
    assert s.status == "Moved docs/ into C/."
    assert contents(os.path.join(other_disk, "docs")) == before and not ws.exists("proj/a/docs")
    assert app.quit() == 0


@needs_unix_permissions
def test_folder_moved_across_disks_with_an_unreadable_file_stays(ws, other_disk, ltt):
    ws.write("proj/a/docs/one.txt", "one\n")
    secret = ws.write("proj/a/docs/secret.txt", "s\n", mode=0o000)
    try:
        app = ltt(ws.path("proj"), other_disk, cols=WIDE)
        select(app, "proj/a/docs")
        app.press("ctrl+x")
        select(app, "C")
        app.press("ctrl+v")
        s = app.wait(lambda s: s.status.startswith(("Moved", "Move error", "Copied")), what="the move's outcome")
        assert s.status == ("Copied docs/ into C/, but the original stays: "
                            f"1 failed: Access to the path '{secret}' is denied.")
        assert ws.files("proj/a/docs") == ["one.txt", "secret.txt"]
        assert app.quit() == 0
    finally:
        os.chmod(secret, 0o644)
