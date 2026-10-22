"""Root folders of the left panel: several at once, Ctrl+O to add one (or open a file), Delete to take one away."""
import os
import sys

import pytest

from harness import keys as K
from tree_helpers import names, select

WIDE = 300  # room for messages with whole paths


def roots(s):
    return [r.name for r in s.tree if r.depth == 0]


def add(app, text):
    """Ctrl+O, the text, Enter; returns the first frame without the prompt.

    A new root starts being watched before the frame is drawn, which can take
    longer than press() waits for quiet: a background frame drawn just as
    Enter arrived would pass for the answer.
    """
    app.press("ctrl+o")
    app.type(text)
    app.press("enter")
    return app.wait(lambda s: s.mode != "PROMPT", what="the prompt to close")


def candidates(s):
    """Completion candidates listed in the help line (" Tab ▸ sub/  a.txt")."""
    line = s.lines[s.help_y]
    return line[len(" Tab ▸ "):].split() if line.startswith(" Tab ▸ ") else []


def ghost(s):
    """The grey suggestion after the typed text of a prompt."""
    return "".join(c.ch for c in s.cells[s.status_y] if c.fg == "DarkGray")


def typed(s):
    """The prompt's label and typed text, without the grey suggestion."""
    return "".join(c.ch for c in s.cells[s.status_y] if c.fg == "White").strip()


def proj(ws):
    ws.write("proj/sub/inner/f.txt", "f\n")
    ws.write("proj/sub/s.txt", "s\n")
    ws.write("proj/a.txt", "one\ntwo\nthree\nfour\n")
    return ws.path("proj")


# ---------------------------------------------------------------- roots from the command line

def test_several_roots_are_shown_in_the_order_given(ltt, ws):
    for name in ("c", "a", "b"):
        ws.write(f"{name}/{name}.txt", name)
    s = ltt(ws.path("c"), ws.path("a"), ws.path("b")).snap()
    assert names(s) == ["c", "c.txt", "a", "a.txt", "b", "b.txt"] and roots(s) == ["c", "a", "b"]
    assert all(r.expanded for r in s.tree if r.depth == 0)


def test_the_same_root_given_twice_is_shown_once(ltt, ws):
    # W5: a trailing slash made a second root
    s = ltt(proj(ws), ws.path("proj") + "/").snap()
    assert roots(s) == ["proj"]


def test_a_file_inside_a_root_adds_no_root_of_its_own(ltt, ws):
    # W5: a file argument inside an open root added its folder as a nested root
    s = ltt(proj(ws), ws.path("proj/sub/s.txt")).snap()
    assert roots(s) == ["proj"] and s.tab_names == ["s.txt"]
    assert s.tree_selected.name == "s.txt"


def test_a_folder_inside_another_root_can_be_a_root_too(ltt, ws):
    s = ltt(proj(ws), ws.path("proj/sub")).snap()
    assert roots(s) == ["proj", "sub"] and [r.depth for r in s.tree if r.name == "s.txt"] == [1]


def test_a_file_alone_makes_its_folder_the_root(ltt, ws):
    proj(ws)
    s = ltt(ws.path("proj/sub/s.txt")).snap()
    assert roots(s) == ["sub"] and s.tab_names == ["s.txt"] and s.mode == "EDITOR"


# ---------------------------------------------------------------- Ctrl+O

def test_ctrl_o_opens_the_add_folder_prompt(ltt, ws):
    ws.mkdir("empty")
    app = ltt(ws.path("empty"), cols=WIDE)
    s = app.press("ctrl+o")
    assert s.mode == "PROMPT" and s.prompt == "Add folder:"
    assert s.help == ("Enter adds the folder to the left panel · Tab/↑↓ completion · Esc cancels"
                      " · a file path (file:line too) opens the file")


def test_ctrl_o_lists_the_selected_folder_and_suggests_its_first_entry(ltt, ws):
    app = ltt(proj(ws))
    s = app.press("ctrl+o")
    assert candidates(s) == ["sub/", "a.txt"]  # folders first
    assert ghost(s) == "sub/"


def test_ctrl_o_adds_a_folder_by_its_full_path(ltt, ws):
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), cols=WIDE)
    s = add(app, ws.path("other"))
    assert s.status == (f"Folder added to the left panel: {ws.path('other')} (2 in total)."
                        " Delete on a root removes it.")
    assert roots(s) == ["proj", "other"] and s.tree_selected.name == "other"
    assert s.tree_row("o.txt").depth == 1 and s.mode == "TREE"


def test_an_added_root_is_scrolled_into_view(ltt, ws):
    for i in range(40):
        ws.write(f"proj/f{i:02d}.txt", "x")
    ws.write("other/o.txt", "o")
    app = ltt(ws.path("proj"))
    s = add(app, ws.path("other"))
    assert s.tree_selected.name == "other" and s.tree_selected.y == s.tree[-1].y and s.tree_row("proj") is None


def test_ctrl_o_relative_path_starts_from_the_selected_folder(ltt, ws):
    # P1: relative paths went from the process's working folder
    ws.mkdir("proj/sub/inner")
    ws.mkdir("elsewhere/inner")
    app = ltt(proj(ws), cwd=ws.path("elsewhere"), cols=WIDE)
    select(app, "sub")
    s = add(app, "inner")
    assert s.status.startswith(f"Folder added to the left panel: {ws.path('proj/sub/inner')} (")
    assert roots(s) == ["proj", "inner"] and s.tree_row("f.txt") is not None


def test_ctrl_o_relative_path_starts_from_a_selected_files_folder(ltt, ws):
    ws.mkdir("elsewhere/inner")
    app = ltt(proj(ws), cwd=ws.path("elsewhere"), cols=WIDE)
    select(app, "sub")
    app.press("right")
    select(app, "s.txt")
    s = add(app, "inner")
    assert s.status.startswith(f"Folder added to the left panel: {ws.path('proj/sub/inner')} (")


def test_ctrl_o_relative_path_starts_from_the_open_files_folder(ltt, ws):
    ws.mkdir("elsewhere/inner")
    app = ltt(proj(ws), ws.path("proj/sub/s.txt"), cwd=ws.path("elsewhere"), cols=WIDE)
    assert app.snap().mode == "EDITOR"
    s = add(app, "inner")
    assert s.status.startswith(f"Folder added to the left panel: {ws.path('proj/sub/inner')} (")


def test_ctrl_o_parent_path_adds_a_sibling_folder(ltt, ws):
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), cols=WIDE)
    s = add(app, "../other")
    assert roots(s) == ["proj", "other"]
    assert s.status.startswith(f"Folder added to the left panel: {ws.path('other')} (2 in total)")


def test_ctrl_o_tilde_is_the_home_folder(ltt, ws):
    ws.write("home/notes/n.txt", "n\n")
    app = ltt(proj(ws), cols=WIDE)
    s = add(app, "~/notes")
    assert roots(s) == ["proj", "notes"] and s.title == "ltt · ~/notes"
    assert s.status.startswith(f"Folder added to the left panel: {ws.path('home/notes')} (")


def test_ctrl_o_missing_folder_is_reported(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    s = add(app, "nope")
    assert s.status == "Folder not found: " + ws.path("proj/nope")
    assert roots(s) == ["proj"] and s.mode == "TREE"


def test_ctrl_o_says_when_nothing_matches(ltt, ws):
    app = ltt(proj(ws))
    app.press("ctrl+o")
    s = app.type("zz")
    assert s.help == "No matches · Enter uses it as-is · Esc cancels · ~ and relative paths are supported"


def test_ctrl_o_with_nothing_typed_cancels(ltt, ws):
    app = ltt(proj(ws))
    s = app.press("ctrl+o", "enter")
    assert s.status == "Adding a folder cancelled." and roots(s) == ["proj"] and s.mode == "TREE"


def test_ctrl_o_esc_cancels(ltt, ws):
    ws.mkdir("other")
    app = ltt(proj(ws))
    app.press("ctrl+o")
    app.type(ws.path("other"))
    s = app.press("esc")
    assert s.status == "Cancelled." and roots(s) == ["proj"] and s.mode == "TREE"


def test_ctrl_o_refuses_a_folder_already_in_the_panel(ltt, ws):
    # W5: the same root could be added twice
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), ws.path("other"), cols=WIDE)
    select(app, "a.txt")
    s = add(app, ws.path("other"))
    assert s.status == "Folder already in the panel: " + ws.path("other")
    assert roots(s) == ["proj", "other"] and s.tree_selected.name == "other"


@pytest.mark.skipif(sys.platform != "darwin", reason="a case-insensitive file system")
def test_ctrl_o_refuses_a_root_spelled_in_another_case(ltt, ws):
    app = ltt(proj(ws))
    select(app, "a.txt")
    s = add(app, ws.path("PROJ"))
    assert s.status.startswith("Folder already in the panel: ") and roots(s) == ["proj"]


def test_ctrl_o_refuses_a_root_spelled_with_a_trailing_slash(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    select(app, "a.txt")
    s = add(app, ws.path("proj") + "/")
    assert s.status.startswith("Folder already in the panel: ")
    assert roots(s) == ["proj"] and s.tree_selected.name == "proj"


def test_ctrl_o_on_a_file_opens_it(ltt, ws):
    app = ltt(proj(ws))
    s = add(app, ws.path("proj/sub/s.txt"))
    assert s.mode == "EDITOR" and s.tab_names == ["s.txt"] and s.editor_lines[:1] == ["s"]
    assert roots(s) == ["proj"] and s.tree_selected.name == "s.txt"


def test_ctrl_o_on_a_file_and_line_opens_it_there(ltt, ws):
    app = ltt(proj(ws))
    s = add(app, "a.txt:3")
    assert s.tab_names == ["a.txt"] and s.pos[:3] == (3, 5, 1)


def test_ctrl_o_on_a_file_line_and_column_puts_the_cursor_there(ltt, ws):
    app = ltt(proj(ws))
    s = add(app, "a.txt:2:3")
    assert s.pos[:3] == (2, 5, 3)


def test_ctrl_o_on_a_file_outside_the_roots_adds_its_folder(ltt, ws):
    ws.write("other/o.txt", "o1\no2\n")
    app = ltt(proj(ws))
    s = add(app, ws.path("other/o.txt:2"))
    assert roots(s) == ["proj", "other"] and s.tab_names == ["o.txt"] and s.pos[0] == 2
    assert s.tree_selected.name == "o.txt"


def test_ctrl_o_on_a_file_named_with_two_leading_dots(ltt, ws):
    # W8: a path whose relative part starts with .. counted as outside the root
    ws.write("proj/..foo", "dots\n")
    app = ltt(proj(ws))
    s = add(app, "..foo")
    assert s.tab_names == ["..foo"] and roots(s) == ["proj"] and s.tree_selected.name == "..foo"


@pytest.mark.xfail(strict=True, reason="BUG: whether a relative 'name:12' exists is checked in the process's "
                                       "working folder, not in the selected one (P1)")
def test_ctrl_o_on_a_relative_file_with_a_colon_in_its_name(ltt, ws):
    ws.write("proj/log:12", "colon\n")
    ws.mkdir("elsewhere")
    app = ltt(proj(ws), cwd=ws.path("elsewhere"))
    s = add(app, "log:12")
    assert s.tab_names == ["log:12"]


def test_ctrl_o_tab_completes_a_folder(ltt, ws):
    app = ltt(proj(ws), cols=WIDE)
    app.press("ctrl+o")
    s = app.type("su")
    assert ghost(s) == "b/"
    s = app.press("tab")
    assert typed(s) == "Add folder: sub/" and ghost(s) == "inner/"  # now suggesting inside it
    s = app.press("enter")
    assert roots(s) == ["proj", "sub"]


def test_ctrl_o_right_arrow_accepts_the_suggestion(ltt, ws):
    ws.mkdir("proj/Docs")
    app = ltt(proj(ws))
    app.press("ctrl+o")
    app.type("doc")
    s = app.press("right")
    assert s.prompt == "Add folder: Docs/"  # spelled as on disk


def test_ctrl_o_down_and_up_cycle_through_candidates(ltt, ws):
    ws.mkdir("proj/sub2")
    app = ltt(proj(ws))
    app.press("ctrl+o")
    app.type("su")
    assert app.press("down").prompt == "Add folder: sub/"
    assert app.press("down").prompt == "Add folder: sub2/"
    assert app.press("down").prompt == "Add folder: sub/"
    assert app.press("up").prompt == "Add folder: sub2/"


def test_a_root_added_with_ctrl_o_is_watched(ltt, ws):
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws))
    add(app, ws.path("other"))
    ws.write("other/new.txt", "n\n")
    s = app.wait_tree("new.txt")
    assert s.tree_row("new.txt").depth == 1


# ---------------------------------------------------------------- taking a root away

def test_delete_on_a_root_removes_it_from_the_panel(ltt, ws):
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), ws.path("other"), cols=WIDE)
    select(app, "proj")
    s = app.press("delete")
    assert s.status == f"Folder removed from the panel: {ws.path('proj')} (files on disk untouched)."
    assert names(s) == ["other", "o.txt"] and s.tree_selected.name == "other"
    assert ws.files("proj") == ["a.txt", "sub/inner/f.txt", "sub/s.txt"]


def test_delete_on_the_last_root_is_refused(ltt, ws):
    app = ltt(proj(ws))
    s = app.press("delete")
    assert s.status == "The last folder cannot be removed from the panel." and roots(s) == ["proj"]


def test_delete_elsewhere_explains_itself(ltt, ws):
    ws.mkdir("other")
    app = ltt(proj(ws), ws.path("other"), cols=WIDE)
    select(app, "a.txt")
    s = app.press("delete")
    assert s.status == "Delete only removes a root folder from the panel; Ctrl+Delete deletes from disk."
    assert roots(s) == ["proj", "other"] and ws.exists("proj/a.txt")


def test_removing_a_root_keeps_its_open_tabs(ltt, ws):
    ws.write("other/o.txt", "o1\n")
    app = ltt(proj(ws), ws.path("other"), ws.path("other/o.txt"))
    app.press("esc")
    select(app, "other")
    s = app.press("delete")
    assert roots(s) == ["proj"] and s.tab_names == ["o.txt"]
    s = app.press("tab")
    assert s.mode == "EDITOR" and s.editor_lines[:1] == ["o1"]
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: o.txt")
    assert ws.read("other/o.txt") == "Xo1\n"


def test_a_tab_of_a_removed_root_still_follows_its_file(ltt, ws):
    ws.write("other/o.txt", "o1\n")
    app = ltt(proj(ws), ws.path("other"), ws.path("other/o.txt"))
    app.press("esc")
    select(app, "other")
    app.press("delete")
    ws.write("other/o.txt", "rewritten\n")
    app.wait_editor(["rewritten"])


def test_right_click_on_a_root_removes_it(ltt, ws):
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), ws.path("other"))
    s = app.click(4, app.snap().tree_row("other").y, button=K.RIGHT)
    assert roots(s) == ["proj"] and s.status.startswith("Folder removed from the panel: ")
    assert ws.exists("other/o.txt")


def test_mouse_back_and_forward_buttons_leave_the_roots_alone(ltt, ws):
    # T9: the extra buttons read as clicks, one of them as a right click that removed a root
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws), ws.path("other"))
    y = app.snap().tree_row("other").y
    for button in (128, 129, 130, 131):
        app.write(K.mouse(button, 4, y, True) + K.mouse(button, 4, y, False))
    s = app.settle(frame=False)  # ignored input: no frame follows
    assert roots(s) == ["proj", "other"] and s.tree_selected.name == "proj"


@pytest.mark.xfail(strict=True, reason="BUG: removing a root above the selection (right click) moves the "
                                       "selection to another entry")
def test_removing_a_root_above_the_selection_keeps_it(ltt, ws):
    for root in "ABC":
        ws.write(f"{root}/{root.lower()}1.txt", "1")
        ws.write(f"{root}/{root.lower()}2.txt", "22")
    app = ltt(ws.path("A"), ws.path("B"), ws.path("C"))
    select(app, "b1.txt")
    s = app.click(3, 1, button=K.RIGHT)  # on root A
    assert roots(s) == ["B", "C"]
    assert s.tree_selected.name == "b1.txt"


# ---------------------------------------------------------------- renaming a root

def test_renaming_a_root_given_with_a_trailing_slash(ltt, ws):
    # W5: the root's path kept the slash and the rename failed
    ws.write("other/o.txt", "o\n")
    app = ltt(proj(ws) + "/", ws.path("other"), cols=WIDE)
    select(app, "proj")
    app.press("ctrl+r", "ctrl+u")
    app.type("renamed")
    s = app.press("enter")
    assert s.status == "Renamed: proj → renamed"
    assert roots(s) == ["renamed", "other"] and s.tree_row("renamed").expanded
    assert s.tree_selected.name == "renamed" and s.title_path == ws.path("renamed")
    assert ws.exists("renamed/a.txt") and not ws.exists("proj")


def test_renaming_a_root_carries_its_open_tabs_along(ltt, ws):
    app = ltt(proj(ws), ws.path("proj/a.txt"), cols=WIDE)
    app.press("esc")
    select(app, "proj")
    app.press("ctrl+r", "ctrl+u")
    app.type("moved")
    app.press("enter")
    s = app.press("tab")
    assert s.title_path == ws.path("moved/a.txt") and s.tab_names == ["a.txt"]
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("moved/a.txt").startswith("Xone")


def test_a_renamed_root_is_still_watched(ltt, ws):
    app = ltt(proj(ws))
    app.press("ctrl+r", "ctrl+u")
    app.type("renamed")
    app.press("enter")
    ws.write("renamed/new.txt", "n\n")
    s = app.wait_tree("new.txt")
    assert roots(s) == ["renamed"] and s.tree_row("new.txt").depth == 1


@pytest.mark.parametrize("how", ["command_line", "ctrl_o"])
def test_a_folder_through_a_link_is_the_same_root(ltt, ws, how):  # Vim's Test_swap_symlink
    ws.write("proj/a.c", "1\n")
    os.symlink(ws.path("proj"), ws.path("alias"))
    if how == "command_line":
        app = ltt(ws.path("proj"), ws.path("alias/a.c"))
        s = app.snap()
        assert s.tab_names == ["a.c"]
    else:
        app = ltt(ws.path("proj"))
        s = add(app, ws.path("alias"))
        assert s.status.startswith("Folder already in the panel")
    assert roots(s) == ["proj"]
