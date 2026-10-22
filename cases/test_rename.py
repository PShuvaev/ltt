"""Ctrl+R: rename a file or folder from the tree or the open file from the editor; tabs follow; refusals."""
import os

import pytest

from prompts_helpers import candidates, ghost, prompt_line, rename, select_in_tree
from vim_helpers import with_vim

RENAME_HELP = "Enter renames · Esc cancels · type just the new name (no path); open tabs update themselves"
JUST_A_NAME = "Enter just the new name, with no path or special characters."


def case_insensitive(ws, rel):
    """Whether the volume under ws treats `rel` and its upper-case spelling as one entry."""
    return os.path.exists(ws.path(os.path.join(os.path.dirname(rel), os.path.basename(rel).upper())))


# ---- the prompt ----

def test_ctrl_r_in_the_tree_prefills_the_name(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    s = app.press("ctrl+r")
    assert s.mode == "PROMPT" and prompt_line(s) == " Rename: a.txt" and s.help == RENAME_HELP
    assert s.cursor == (len(" Rename: a.txt"), s.status_y)


def test_long_name_keeps_its_end_in_view(ltt, ws):
    # U5: the rename prompt scrolls sideways like the others
    name = "a-very-long-file-name-" + "x" * 80 + ".txt"
    ws.write("proj/" + name, "a")
    app = ltt(ws.path("proj"), ws.path("proj/" + name), cols=80, rows=16)
    s = app.press("ctrl+r")
    line = prompt_line(s)
    assert line.startswith(" Rename: …") and line.endswith("xxxx.txt") and s.cursor == (79, s.status_y)


def test_rename_prompt_offers_no_completion(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    app.press("ctrl+r", "ctrl+u")
    s = app.type("b")
    assert ghost(s) == "" and candidates(s) == [] and s.help == RENAME_HELP


# ---- renaming ----

def test_rename_a_file_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    s = rename(app, "renamed.txt")
    assert s.status == "Renamed: a.txt → renamed.txt" and s.mode == "TREE"
    assert s.tree_selected.name == "renamed.txt" and s.tree_row("a.txt") is None
    assert ws.files("proj") == ["b.txt", "renamed.txt"] and ws.read("proj/renamed.txt") == "a"


def test_rename_a_folder_in_the_tree(ltt, ws):
    ws.write("proj/dir/x.txt", "x")
    ws.write("proj/dir/deeper/y.txt", "y")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "dir")
    s = rename(app, "folder2")
    assert s.status == "Renamed: dir → folder2" and s.tree_selected.name == "folder2"
    assert ws.files("proj") == ["folder2/deeper/y.txt", "folder2/x.txt"]


def test_rename_from_the_editor_renames_the_open_file(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.press("esc")
    select_in_tree(app, "b.txt")
    app.press("tab")  # back to the editor; the tree selection stays on b.txt
    assert prompt_line(app.press("ctrl+r")) == " Rename: a.txt"
    app.press("ctrl+u")
    app.type("c.txt")
    s = app.press("enter")
    assert s.status == "Renamed: a.txt → c.txt" and s.mode == "EDITOR"
    assert s.tab_names == ["c.txt"] and s.title_path.endswith("/proj/c.txt")
    assert ws.files("proj") == ["b.txt", "c.txt"] and ws.read("proj/c.txt") == "a"


def test_rename_with_spaces_and_unicode(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    s = rename(app, "мой файл 📝.txt")
    assert os.listdir(ws.path("proj")) == ["мой файл 📝.txt"] and s.active_tab.name == "мой файл 📝.txt"


def test_open_tab_follows_a_rename_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.press("esc")
    s = rename(app, "c.txt")  # a.txt is selected in the tree
    assert s.tab_names == ["c.txt"]
    s = app.press("tab")
    assert s.title_path.endswith("/proj/c.txt")
    app.type("Y")
    assert app.press("ctrl+s").status == "Saved: c.txt"
    assert ws.files("proj") == ["c.txt"] and ws.read("proj/c.txt") == "Ya"


def test_dirty_tab_keeps_its_edits_and_saves_under_the_new_name(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.type("X")
    s = rename(app, "c.txt")
    assert s.active_tab.name == "c.txt" and s.active_tab.dirty and s.editor_lines == ["Xa"]
    assert ws.read("proj/c.txt") == "a"
    s = app.press("ctrl+s")  # no question: the file did not change, it only moved
    assert s.status == "Saved: c.txt" and ws.files("proj") == ["c.txt"] and ws.read("proj/c.txt") == "Xa"


def test_tabs_inside_a_renamed_folder_follow(ltt, ws):
    ws.write("proj/dir/x.txt", "x")
    ws.write("proj/dir/deeper/y.txt", "y")
    app = ltt(ws.path("proj"), ws.path("proj/dir/x.txt"), ws.path("proj/dir/deeper/y.txt"))
    app.type("X")  # x.txt: unsaved edits
    app.press("esc")
    select_in_tree(app, "dir")
    s = rename(app, "moved")
    assert [(t.name, t.dirty) for t in s.tabs] == [("x.txt", True), ("y.txt", False)]
    s = app.press("alt+2")
    assert s.title_path.endswith("/proj/moved/deeper/y.txt") and s.editor_lines == ["y"]
    s = app.press("alt+1")
    assert s.title_path.endswith("/proj/moved/x.txt")
    assert app.press("ctrl+s").status == "Saved: x.txt"
    assert ws.files("proj") == ["moved/deeper/y.txt", "moved/x.txt"] and ws.read("proj/moved/x.txt") == "Xx"


def test_rename_to_another_extension_changes_the_highlighting(ltt, ws):
    with_vim(ws, syntax=False)
    ws.write("proj/code.txt", "class Foo {}")
    app = ltt(ws.path("proj"), ws.path("proj/code.txt"))
    s = app.snap()
    row = s.editor_rows[0]
    assert s.fg(s.text_x, row.y) == "Gray"  # plain text
    rename(app, "code.cs")
    app.wait(lambda s: s.fg(s.text_x, row.y) != "Gray", what="`class` coloured as a C# keyword")


# ---- case-only renames (the volume is case-insensitive) ----

def test_case_only_rename_of_a_file(ltt, ws):
    # W9: goes through a temporary name; nothing may be left under it
    ws.write("proj/a.txt", "a")
    if not case_insensitive(ws, "proj/a.txt"):
        pytest.skip("needs a case-insensitive volume")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.type("X")
    s = rename(app, "A.txt")
    assert s.status == "Renamed: a.txt → A.txt"
    assert os.listdir(ws.path("proj")) == ["A.txt"]
    assert s.active_tab.name == "A.txt" and s.active_tab.dirty and s.tree_row("A.txt") is not None
    assert app.press("ctrl+s").status == "Saved: A.txt"
    assert os.listdir(ws.path("proj")) == ["A.txt"] and ws.read("proj/A.txt") == "Xa"


def test_case_only_rename_of_a_folder(ltt, ws):
    ws.write("proj/dir/x.txt", "x")
    if not case_insensitive(ws, "proj/dir"):
        pytest.skip("needs a case-insensitive volume")
    app = ltt(ws.path("proj"), ws.path("proj/dir/x.txt"))
    app.press("esc")
    select_in_tree(app, "dir")
    s = rename(app, "DIR")
    assert s.status == "Renamed: dir → DIR" and s.tree_selected.name == "DIR"
    assert os.listdir(ws.path("proj")) == ["DIR"]
    assert app.press("alt+1").title_path.endswith("/proj/DIR/x.txt")


def test_name_taken_in_another_case_is_refused(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/B.txt", "b")
    if not case_insensitive(ws, "proj/a.txt"):
        pytest.skip("needs a case-insensitive volume")
    app = ltt(ws.path("proj"), cols=300)
    select_in_tree(app, "a.txt")
    s = rename(app, "b.txt")
    assert s.status == "Name already taken: " + ws.path("proj/b.txt")
    assert sorted(os.listdir(ws.path("proj"))) == ["B.txt", "a.txt"] and ws.read("proj/B.txt") == "b"


# ---- roots ----

def test_root_given_with_a_trailing_slash_can_be_renamed(ltt, ws):
    # W5: `ltt ~/proj/` named the root "" and broke its rename
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj") + "/")
    s = app.press("ctrl+r")
    assert prompt_line(s) == " Rename: proj"
    app.press("ctrl+u")
    app.type("proj2")
    s = app.press("enter")
    assert s.status == "Renamed: proj → proj2"
    assert s.tree[0].name == "proj2" and s.tree_selected.name == "proj2" and s.tree_row("a.txt") is not None
    assert not ws.exists("proj") and ws.read("proj2/a.txt") == "a"


def test_renaming_a_root_moves_its_tabs(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.press("esc", "home")
    rename(app, "proj2")
    s = app.press("alt+1")
    assert s.title_path.endswith("/proj2/a.txt")
    app.type("Y")
    assert app.press("ctrl+s").status == "Saved: a.txt"
    assert not ws.exists("proj") and ws.read("proj2/a.txt") == "Ya"


def test_renamed_root_is_still_watched(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    rename(app, "proj2")
    ws.write("proj2/new.txt", "made by an agent")
    app.wait_tree("new.txt")


def test_root_inside_a_renamed_root_follows_it(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/s.txt", "s")
    app = ltt(ws.path("proj"), ws.path("proj/sub"))  # the second root lies inside the first
    rename(app, "proj2")  # the first root is selected
    s = app.press("end", "up")  # the second root
    assert (s.tree_selected.name, s.tree_selected.depth) == ("sub", 0)
    assert s.title_path.endswith("/proj2/sub")


def test_root_inside_a_renamed_folder_follows_it(ltt, ws):
    ws.write("proj/x/y/s.txt", "s")
    app = ltt(ws.path("proj"), ws.path("proj/x/y"))  # the second root lies inside a folder of the first
    select_in_tree(app, "x")
    rename(app, "z")
    s = app.press("end", "up")  # the second root
    assert (s.tree_selected.name, s.tree_selected.depth) == ("y", 0)
    assert s.title_path.endswith("/proj/z/y") and s.tree_row("s.txt") is not None


# ---- refusals ----

@pytest.mark.parametrize("name", ["x/y.txt", "sub/", ".", ".."])
def test_a_path_or_dot_name_is_refused(ltt, ws, name):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    s = rename(app, name)
    assert s.status == JUST_A_NAME and s.mode == "TREE" and ws.files("proj") == ["a.txt"]


def test_empty_name_is_refused(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    app.press("ctrl+r", "ctrl+u")
    s = app.press("enter")
    assert s.status == JUST_A_NAME and ws.files("proj") == ["a.txt"]


def test_unchanged_name(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    app.press("ctrl+r")
    s = app.press("enter")
    assert s.status == "The name is unchanged." and ws.files("proj") == ["a.txt"]


def test_taken_name_is_refused(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), cols=300)
    select_in_tree(app, "a.txt")
    s = rename(app, "b.txt")
    assert s.status == "Name already taken: " + ws.path("proj/b.txt")
    assert ws.read("proj/a.txt") == "a" and ws.read("proj/b.txt") == "b"


def test_taken_folder_name_is_refused(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"), cols=300)
    select_in_tree(app, "a.txt")
    s = rename(app, "sub")
    assert s.status == "Name already taken: " + ws.path("proj/sub")
    assert ws.files("proj") == ["a.txt", "sub/c.txt"]


def test_rename_onto_the_name_of_an_open_tab_leaves_one_tab_per_path(ltt, ws):
    ws.write("proj/x.txt", "x")
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/x.txt"), ws.path("proj/a.txt"))
    ws.remove("proj/x.txt")  # an agent deleted it: the tab keeps its text
    app.wait(lambda s: s.tabs[0].missing, what="the ✗ on x.txt")
    app.press("alt+2")
    s = rename(app, "x.txt")  # free on disk, so allowed
    assert len(set(s.tab_names)) == len(s.tab_names)


def test_rename_onto_a_gone_file_whose_tab_has_unsaved_edits_is_refused(ltt, ws):
    ws.write("proj/x.txt", "x")
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/x.txt"), ws.path("proj/a.txt"), cols=160)
    app.type("mine ")  # into x.txt, the active tab
    ws.remove("proj/x.txt")
    app.wait(lambda s: s.tabs[0].missing, what="the ✗ on x.txt")
    app.press("alt+2")
    s = rename(app, "x.txt")
    assert s.status == "Name held by unsaved edits in the tab x.txt — save or close it first."
    assert ws.files("proj") == ["a.txt"]
    assert [(t.name, t.dirty, t.missing) for t in s.tabs] == [("x.txt", True, True), ("a.txt", False, False)]


def test_rename_of_a_file_deleted_on_disk_is_an_error(ltt, ws):
    ws.write("proj/gone.txt", "g")
    app = ltt(ws.path("proj"), ws.path("proj/gone.txt"))
    ws.remove("proj/gone.txt")
    app.wait(lambda s: s.active_tab.missing, what="the ✗ on the tab")
    s = rename(app, "back.txt")
    assert s.status.startswith("Rename error: ") and s.mode == "EDITOR" and ws.files("proj") == []


# ---- leaving the prompt ----

def test_esc_cancels(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    app.press("ctrl+r", "ctrl+u")
    app.type("other.txt")
    s = app.press("esc")
    assert (s.mode, s.status) == ("TREE", "Cancelled.") and ws.files("proj") == ["a.txt"]


def test_esc_returns_to_the_editor_when_started_there(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    app.press("ctrl+r")
    s = app.press("esc")
    assert (s.mode, s.status) == ("EDITOR", "Cancelled.")


def test_refused_name_returns_to_the_editor_when_started_there(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"))
    s = rename(app, "x/y.txt")
    assert (s.mode, s.status) == ("EDITOR", JUST_A_NAME)


def test_ctrl_c_cancels(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "a.txt")
    app.press("ctrl+r", "ctrl+u")
    app.type("other.txt")
    s = app.press("ctrl+c")
    assert s.status == "Cancelled (Ctrl+C)." and ws.files("proj") == ["a.txt"]



@pytest.mark.parametrize("typed, after", [(None, " notes.txt "), ("2.txt ", " notes2.txt ")],
                         ids=["unchanged", "edited"])
def test_spaces_the_name_has_around_it_stay(ltt, ws, typed, after):  # an idea from Emacs's file tests
    ws.write("proj/ notes.txt ", "n")
    app = ltt(ws.path("proj"))
    app.press("down", "ctrl+r")
    if typed:
        app.press(*["backspace"] * 5)
        app.type(typed)
    app.press("enter")
    assert ws.files("proj") == [after]
