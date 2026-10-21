"""Open files changed behind ltt's back — the agent next door (A1, A2, V2, V18, V19, S2, W2).

A clean tab takes the new text (one undo step); a tab with unsaved edits
keeps them and is marked ⚠ (changed on disk) or ✗ (gone), and saving it
asks first. Open tabs are compared with the disk every second and right
after the folder watcher reports a change; a tab follows a rename only
through the watcher.
"""
import os
import sys
import time

import pytest

W = 160  # columns: room for the longest status message and question


def tab(s, name):
    return next((t for t in s.tabs if t.name == name), None)


def wait_tab(app, name, **marks):
    """Wait until the tab `name` is there with these marks (dirty=, changed=, missing=)."""
    def ok(s):
        t = tab(s, name)
        return t is not None and all(getattr(t, k) == v for k, v in marks.items())
    return app.wait(ok, what=f"tab {name} with {marks}")


def open_file(ltt, ws, rel="proj/a.txt", text="line one\nline two\n", **kw):
    """ltt with one file open (its folder is the root), the editor focused."""
    ws.write(rel, text)
    return ltt(ws.path(rel), cols=W, **kw)


def open_folded(ltt, ws, rel="sub/a.txt", text="line one\nline two\n", cols=W):
    """ltt on proj with proj/<rel> open and its folder collapsed in the tree: a rename or a
    deletion there changes no visible row, so the tab's own message is the last word."""
    ws.write("proj/" + rel, text)
    app = ltt(ws.path("proj"), ws.path("proj/" + rel), cols=cols)
    app.press("esc", "left", "left", "tab")  # to the tree, up to the folder, fold it, back
    s = app.snap()
    assert s.mode == "EDITOR" and not s.tree_row(rel.split("/")[0]).expanded
    return app


def watching(app, ws, folder="proj"):
    """Wait until the folder watcher reports changes in folder (a visible root)."""
    ws.write(os.path.join(folder, "canary.tmp"), "")
    app.wait_tree("canary.tmp")


def mtime_plus(ws, rel, seconds):
    st = os.stat(ws.path(rel))
    os.utime(ws.path(rel), ns=(st.st_atime_ns, st.st_mtime_ns + int(seconds * 1e9)))


# ---------------------------------------------------------------- a clean tab follows the disk (A1)

def test_clean_tab_reloads_when_the_file_changes(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", "agent wrote this\nline two\n")
    s = app.wait_status("Reloaded a.txt: it changed on disk (Ctrl+Z brings back the previous text).")
    assert s.editor_lines[:2] == ["agent wrote this", "line two"]
    t = s.tabs[0]
    assert not (t.dirty or t.changed or t.missing) and not s.title_dirty


def test_ctrl_z_after_a_reload_brings_back_the_previous_text(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", "agent wrote this\nline two\n")
    app.wait_status("Reloaded a.txt")
    s = app.press("ctrl+z")
    assert s.editor_lines[:2] == ["line one", "line two"]
    assert s.status == "Undone. Ctrl+Y redoes it."
    assert s.tabs[0].dirty and not s.tabs[0].changed  # the old text is now an edit over the agent's
    assert ws.read("proj/a.txt") == "agent wrote this\nline two\n"


def test_ctrl_y_takes_the_reloaded_text_again(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", "agent wrote this\nline two\n")
    app.wait_status("Reloaded a.txt")
    app.press("ctrl+z")
    s = app.press("ctrl+y")
    assert s.editor_lines[:2] == ["agent wrote this", "line two"]
    assert not s.tabs[0].dirty


def test_saving_the_undone_reload_needs_no_question(ltt, ws):
    # the reload made the agent's version the known one: saving over it is a plain save
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", "agent wrote this\nline two\n")
    app.wait_status("Reloaded a.txt")
    app.press("ctrl+z")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "line one\nline two\n"


def test_redo_of_a_reload_keeps_emoji_whole(ltt, ws):
    # V2: redo of a reload put the cursor between the halves of a surrogate pair (saved U+FFFD)
    app = open_file(ltt, ws, "proj/e.txt", "abcdef\n")
    app.press("right", "right", "right")  # index 3: inside the second emoji of the new text
    ws.write("proj/e.txt", "😀😀😀\n")
    app.wait_status("Reloaded e.txt")
    app.press("ctrl+z")
    app.wait_editor(["abcdef"])
    app.press("ctrl+y")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: e.txt")
    assert ws.read_bytes("proj/e.txt").decode("utf-8") == "😀X😀😀\n"


def test_reload_keeps_the_cursor_on_its_line_when_lines_come_above(ltt, ws):
    app = open_file(ltt, ws, text="".join(f"line {i}\n" for i in range(1, 11)))
    app.press(*["down"] * 7, "end")  # end of "line 8"
    ws.write("proj/a.txt", "new 1\nnew 2\n" + "".join(f"line {i}\n" for i in range(1, 11)))
    s = app.wait_status("Reloaded a.txt")
    assert s.pos[:3] == (10, 13, 7)  # still at the end of "line 8", now two lines down


def test_reload_below_the_cursor_leaves_it_in_place(ltt, ws):
    app = open_file(ltt, ws)
    app.press("down", "right", "right")
    ws.write("proj/a.txt", "line one\nline two\nline three\nline four\n")
    s = app.wait_status("Reloaded a.txt")
    assert s.pos[:3] == (2, 5, 3)


def test_reload_of_a_shorter_file_keeps_the_cursor_inside(ltt, ws):
    app = open_file(ltt, ws, text="".join(f"a long line number {i}\n" for i in range(1, 11)))
    app.press(*["down"] * 8, "end")
    ws.write("proj/a.txt", "short\n")
    s = app.wait_status("Reloaded a.txt")
    assert s.pos[:2] == (2, 2)
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "short\nX"


def test_reload_drops_the_selection(ltt, ws):
    app = open_file(ltt, ws)
    s = app.press("shift+right", "shift+right", "shift+right")
    assert s.pos[3] == 3
    ws.write("proj/a.txt", "line ONE\nline two\n")
    s = app.wait_status("Reloaded a.txt")
    assert s.pos[3] == 0
    s = app.type("X")  # types at the cursor, deletes nothing
    assert s.editor_lines[0] == "linXe ONE"


def test_reload_keeps_the_same_text_in_view(ltt, ws):
    app = open_file(ltt, ws, text="".join(f"line {i}\n" for i in range(1, 101)))
    s = app.press("pgdn", "pgdn")
    top = s.editor_rows[0]
    assert top.number > 1
    ws.write("proj/a.txt", "new 1\nnew 2\nnew 3\n" + "".join(f"line {i}\n" for i in range(1, 101)))
    s = app.wait_status("Reloaded a.txt")
    assert (s.editor_rows[0].number, s.editor_rows[0].text) == (top.number + 3, top.text)


def test_reload_takes_the_new_line_endings(ltt, ws):
    app = open_file(ltt, ws, text="one\r\ntwo\r\n")
    ws.write("proj/a.txt", "one\nthree\n")
    app.wait_status("Reloaded a.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read_bytes("proj/a.txt") == b"Xone\nthree\n"


def test_reloaded_lines_are_marked_against_the_file_as_opened(ltt, ws):
    app = open_file(ltt, ws, text="one\ntwo\nthree\n")
    ws.write("proj/a.txt", "one\nTWO\nthree\nfour\n")
    s = app.wait_status("Reloaded a.txt")
    rows = s.editor_rows
    assert [r.text for r in rows[:4]] == ["one", "TWO", "three", "four"]
    assert rows[0].mark == " "
    assert (rows[1].mark, rows[1].mark_color) == ("▎", "Yellow")  # modified
    assert (rows[3].mark, rows[3].mark_color) == ("▎", "Green")   # added


def test_file_emptied_outside_reloads_to_an_empty_tab(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", "")
    s = app.wait_status("Reloaded a.txt")
    assert s.editor_lines == [""] and s.pos[:3] == (1, 1, 1)
    s = app.press("ctrl+z")
    assert s.editor_lines[:2] == ["line one", "line two"]


# ---------------------------------------------------------------- unsaved edits are never overwritten silently (A1)

def changed_under_edits(ltt, ws):
    """a.txt open with "MINE " typed in, then rewritten by the agent: the tab is ● ⚠."""
    app = open_file(ltt, ws)
    app.type("MINE ")
    ws.write("proj/a.txt", "agent version\n")
    wait_tab(app, "a.txt", dirty=True, changed=True)
    return app


def test_dirty_tab_keeps_its_edits_and_is_marked_changed(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    ws.write("proj/a.txt", "agent version\n")
    s = app.wait_status("a.txt changed on disk — your unsaved edits are kept; saving will ask before overwriting.")
    t = s.tabs[0]
    assert t.dirty and t.changed and not t.missing and s.title_dirty
    assert s.editor_lines[:2] == ["MINE line one", "line two"]
    assert ws.read("proj/a.txt") == "agent version\n"


def test_ctrl_s_on_a_changed_tab_asks_before_overwriting(ltt, ws):
    app = changed_under_edits(ltt, ws)
    s = app.press("ctrl+s")
    assert s.mode == "CONFIRM"
    assert s.question == ("a.txt changed on disk since it was opened!  S overwrites it · "
                          "R takes the disk version (Ctrl+Z brings yours back) · Esc cancels")
    assert ws.read("proj/a.txt") == "agent version\n"


def test_s_overwrites_the_disk_version(ltt, ws):
    app = changed_under_edits(ltt, ws)
    app.press("ctrl+s", "s")
    s = app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "MINE line one\nline two\n"
    assert not (s.tabs[0].dirty or s.tabs[0].changed)


def test_r_takes_the_disk_version_and_ctrl_z_brings_the_edits_back(ltt, ws):
    app = changed_under_edits(ltt, ws)
    app.press("ctrl+s", "r")
    s = app.wait_status("Took the disk version of a.txt; Ctrl+Z brings back your edits.")
    assert s.editor_lines[0] == "agent version"
    assert not (s.tabs[0].dirty or s.tabs[0].changed)
    s = app.press("ctrl+z")
    assert s.editor_lines[:2] == ["MINE line one", "line two"]
    assert s.tabs[0].dirty and not s.tabs[0].changed
    assert ws.read("proj/a.txt") == "agent version\n"


def test_esc_cancels_the_save_and_keeps_both_versions(ltt, ws):
    app = changed_under_edits(ltt, ws)
    app.press("ctrl+s", "esc")
    s = app.wait_status("Cancelled.")
    assert s.tabs[0].dirty and s.tabs[0].changed
    assert s.editor_lines[0] == "MINE line one"
    assert ws.read("proj/a.txt") == "agent version\n"


def test_other_keys_leave_the_overwrite_question_standing(ltt, ws):
    app = changed_under_edits(ltt, ws)
    app.press("ctrl+s")
    s = app.press("enter", "x", "space")
    assert s.mode == "CONFIRM" and s.question.startswith("a.txt changed on disk since it was opened!")
    assert ws.read("proj/a.txt") == "agent version\n"


def test_undoing_every_edit_of_a_changed_tab_lets_it_follow_the_disk(ltt, ws):
    app = changed_under_edits(ltt, ws)
    app.press("ctrl+z")  # clean again: nothing of yours left to keep
    s = app.wait(lambda s: s.editor_lines[0] == "agent version", what="the disk version")
    assert not (s.tabs[0].dirty or s.tabs[0].changed)


def test_answer_given_after_the_file_changed_still_asks(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    s = app.press("ctrl+w")
    assert s.question.startswith('Tab "a.txt" has changes!')
    ws.write("proj/a.txt", "agent version\n")  # while the question waits
    s = app.press("s")
    assert s.question.startswith("a.txt changed on disk since it was opened!")
    app.press("esc")
    s = app.wait_status("Cancelled.")
    assert s.tab_names == ["a.txt"] and ws.read("proj/a.txt") == "agent version\n"


# ---------------------------------------------------------------- deleted outside (A2)

def test_deleted_file_keeps_its_tab_and_text(ltt, ws):
    app = open_folded(ltt, ws)
    ws.remove("proj/sub/a.txt")
    s = app.wait_status("a.txt was deleted on disk — the tab keeps its text; saving would recreate the file.")
    t = s.tabs[0]
    assert t.missing and not (t.dirty or t.changed)
    assert s.editor_lines[:2] == ["line one", "line two"]


def test_saving_a_deleted_file_asks_and_s_recreates_it(ltt, ws):
    app = open_file(ltt, ws)
    ws.remove("proj/a.txt")
    wait_tab(app, "a.txt", missing=True)
    s = app.press("ctrl+s")
    assert s.question == "a.txt was deleted on disk!  S saves it again · Esc cancels"
    app.press("s")
    s = app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "line one\nline two\n"
    assert not s.tabs[0].missing


def test_esc_does_not_recreate_a_deleted_file(ltt, ws):
    app = open_file(ltt, ws)
    ws.remove("proj/a.txt")
    wait_tab(app, "a.txt", missing=True)
    app.press("ctrl+s", "esc")
    s = app.wait_status("Cancelled.")
    assert s.tabs[0].missing and not ws.exists("proj/a.txt")


def test_unsaved_edits_of_a_deleted_file_are_saved_on_s(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    ws.remove("proj/a.txt")
    wait_tab(app, "a.txt", dirty=True, missing=True, changed=False)
    s = app.press("ctrl+s")
    assert s.question.startswith("a.txt was deleted on disk!")
    app.press("s")
    s = app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "MINE line one\nline two\n"
    assert not (s.tabs[0].dirty or s.tabs[0].missing)


def test_file_moved_away_and_back_clears_the_missing_mark(ltt, ws):
    app = open_file(ltt, ws)
    away = os.path.join(ws.tmp, "a.txt")  # outside the root
    os.rename(ws.path("proj/a.txt"), away)
    wait_tab(app, "a.txt", missing=True)
    os.rename(away, ws.path("proj/a.txt"))  # the same version again: nothing to reload
    s = wait_tab(app, "a.txt", missing=False)
    assert not (s.tabs[0].dirty or s.tabs[0].changed)
    assert app.press("ctrl+z").status == "Nothing to undo."


def test_clean_tab_of_a_deleted_and_recreated_file_reloads(ltt, ws):
    app = open_file(ltt, ws)
    ws.remove("proj/a.txt")
    wait_tab(app, "a.txt", missing=True)
    ws.write("proj/a.txt", "born again\n")
    s = app.wait(lambda s: s.editor_lines[0] == "born again" and not s.tabs[0].missing, what="reloaded")
    assert not (s.tabs[0].dirty or s.tabs[0].changed)


def test_dirty_tab_of_a_deleted_and_recreated_file_is_marked_changed(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    ws.remove("proj/a.txt")
    wait_tab(app, "a.txt", missing=True)
    ws.write("proj/a.txt", "born again\n")
    s = wait_tab(app, "a.txt", dirty=True, changed=True, missing=False)
    assert s.editor_lines[0] == "MINE line one"
    s = app.press("ctrl+s")
    assert s.question.startswith("a.txt changed on disk since it was opened!")


def test_file_replaced_by_a_folder_is_not_saved_over_it(ltt, ws):
    app = open_file(ltt, ws)
    ws.remove("proj/a.txt")
    ws.mkdir("proj/a.txt")
    wait_tab(app, "a.txt", missing=True)
    app.type("X")
    app.press("ctrl+s", "s")
    s = app.wait_status("Save error:")
    assert s.tabs[0].dirty and os.path.isdir(ws.path("proj/a.txt"))
    assert sorted(os.listdir(ws.path("proj"))) == ["a.txt"]  # no temporary file left behind


# ---------------------------------------------------------------- renamed or moved outside (A2, V18, V19)

def test_renamed_file_takes_its_tab_along(ltt, ws):
    app = open_folded(ltt, ws, cols=320)
    watching(app, ws)
    ws.rename("proj/sub/a.txt", "proj/sub/b.txt")
    s = app.wait_status(f"a.txt was moved to {ws.path('proj/sub/b.txt')} — its tab follows.")
    assert s.tab_names == ["b.txt"] and s.title_path == ws.path("proj/sub/b.txt")
    t = s.tabs[0]
    assert not (t.dirty or t.changed or t.missing)


def test_save_after_an_outside_rename_writes_the_new_name(ltt, ws):
    app = open_file(ltt, ws)
    watching(app, ws)
    ws.rename("proj/a.txt", "proj/b.txt")
    wait_tab(app, "b.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: b.txt")
    assert ws.read("proj/b.txt") == "Xline one\nline two\n"
    assert not ws.exists("proj/a.txt")


def test_tab_follows_a_move_into_another_folder(ltt, ws):
    ws.mkdir("proj/sub")
    app = open_file(ltt, ws)
    watching(app, ws)
    ws.rename("proj/a.txt", "proj/sub/a.txt")
    s = app.wait(lambda s: s.title_path.endswith("/proj/sub/a.txt"), what="the title on the new path")
    assert s.tab_names == ["a.txt"] and not s.tabs[0].missing
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/sub/a.txt") == "Xline one\nline two\n" and not ws.exists("proj/a.txt")


def test_tab_follows_its_folder_being_renamed(ltt, ws):
    ws.write("proj/docs/x.txt", "inside\n")
    app = ltt(ws.path("proj"), ws.path("proj/docs/x.txt"), cols=W)
    watching(app, ws)
    ws.rename("proj/docs", "proj/notes")
    s = app.wait(lambda s: s.title_path.endswith("/proj/notes/x.txt"), what="the title on the new path")
    assert s.tab_names == ["x.txt"] and not s.tabs[0].missing
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: x.txt")
    assert ws.read("proj/notes/x.txt") == "Xinside\n" and not ws.exists("proj/docs")


def test_dirty_tab_follows_a_rename_and_saves_without_asking(ltt, ws):
    app = open_file(ltt, ws)
    watching(app, ws)
    app.type("MINE ")
    ws.rename("proj/a.txt", "proj/b.txt")
    s = wait_tab(app, "b.txt", dirty=True, changed=False, missing=False)
    assert s.editor_lines[0] == "MINE line one"
    app.press("ctrl+s")
    app.wait_status("Saved: b.txt")
    assert ws.read("proj/b.txt") == "MINE line one\nline two\n"


def test_tab_renamed_to_a_name_already_open_shows_its_folder(ltt, ws):
    ws.write("proj/docs/readme.md", "docs\n")
    ws.write("proj/notes.md", "notes\n")
    ws.mkdir("proj/other")
    app = ltt(ws.path("proj"), ws.path("proj/docs/readme.md"), ws.path("proj/notes.md"), cols=W)
    watching(app, ws)
    ws.rename("proj/notes.md", "proj/other/readme.md")
    s = app.wait(lambda s: s.tab_names == ["readme.md · docs", "readme.md · other"], what="both labels with a folder")
    assert not any(t.missing for t in s.tabs)


def test_file_moved_out_of_every_root_shows_as_deleted(ltt, ws):
    app = open_file(ltt, ws)
    watching(app, ws)
    os.rename(ws.path("proj/a.txt"), os.path.join(ws.tmp, "a.txt"))
    app.wait_tree("a.txt", present=False)  # the watcher has had its say
    s = wait_tab(app, "a.txt", missing=True)
    assert s.tab_names == ["a.txt"]


def test_renamed_then_edited_clean_tab_reloads_the_edit(ltt, ws):
    # V18: a file renamed and then edited outside was taken as unchanged (t11.py #1)
    app = open_file(ltt, ws)
    watching(app, ws)
    ws.rename("proj/a.txt", "proj/b.txt")
    time.sleep(0.3)  # on purpose: the edit comes a moment later, before the watcher's batch is due
    ws.append("proj/b.txt", "agent edit\n")
    s = app.wait(lambda s: s.tab_names == ["b.txt"] and s.editor_lines[2] == "agent edit", what="followed and reloaded")
    assert not (s.tabs[0].dirty or s.tabs[0].changed)
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: b.txt")
    assert ws.read("proj/b.txt") == "Xline one\nline two\nagent edit\n"


def test_renamed_then_edited_dirty_tab_asks_before_saving(ltt, ws):
    # V18: the next Ctrl+S overwrote the agent's edit without a question
    app = open_file(ltt, ws)
    watching(app, ws)
    app.type("MINE ")
    ws.rename("proj/a.txt", "proj/b.txt")
    time.sleep(0.3)  # on purpose, as above
    ws.append("proj/b.txt", "agent edit\n")
    s = wait_tab(app, "b.txt", dirty=True, changed=True, missing=False)
    assert s.editor_lines[0] == "MINE line one"
    s = app.press("ctrl+s")
    assert s.question.startswith("b.txt changed on disk since it was opened!")
    app.press("esc")
    assert ws.read("proj/b.txt") == "line one\nline two\nagent edit\n"


@pytest.mark.xfail(sys.platform == "darwin", strict=True,
                   reason="BUG: a rename with a write to the new name right after it (within ~5 ms) reaches ltt as "
                          "a deletion and a new file (FSEvents): the tab shows ✗ instead of following")
def test_renamed_and_at_once_edited_files_take_their_tabs_along(ltt, ws):
    for i in (1, 2, 3):
        ws.write(f"proj/a{i}.txt", f"text {i}\n")
    app = ltt(ws.path("proj"), *(ws.path(f"proj/a{i}.txt") for i in (1, 2, 3)), cols=W)
    watching(app, ws)
    for i in (1, 2, 3):  # three times: one try could be split by the scheduler
        ws.rename(f"proj/a{i}.txt", f"proj/b{i}.txt")
        ws.append(f"proj/b{i}.txt", "agent edit\n")  # no pause: `mv a b && echo >> b`
    app.wait(lambda s: {"b1.txt", "b2.txt", "b3.txt"} <= set(s.tree_names), what="the watcher's batch")
    app.wait(lambda s: s.tab_names == ["b1.txt", "b2.txt", "b3.txt"], timeout=2, what="the tabs on the new names")


def test_file_moved_onto_another_open_file(ltt, ws):
    # V19: two tabs ended up on one path and every key of a folder search failed (t11.py #2)
    ws.write("proj/a.txt", "alpha line\n")
    ws.write("proj/b.txt", "beta line\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"), cols=W)
    watching(app, ws)
    app.type("X")
    app.press("alt+2")
    app.type("Y")
    ws.rename("proj/a.txt", "proj/b.txt")
    wait_tab(app, "a.txt", dirty=True, missing=True)
    s = wait_tab(app, "b.txt", dirty=True, changed=True)
    assert s.tab_names == ["a.txt", "b.txt"]
    app.press("ctrl+f", "ctrl+f")  # search all folders
    app.type("li")
    s = app.wait(lambda s: "found: 1" in s.status_line, what="the folder search answered")
    assert [(h.location, h.snippet) for h in s.results] == [("b.txt:1", "Ybeta line")]  # the unsaved text


def test_clean_file_moved_onto_another_open_file(ltt, ws):
    ws.write("proj/a.txt", "alpha line\n")
    ws.write("proj/b.txt", "beta line\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"), cols=W)
    watching(app, ws)
    ws.rename("proj/a.txt", "proj/b.txt")
    s = wait_tab(app, "a.txt", missing=True)
    assert s.editor_lines[0] == "alpha line"  # a.txt is active: its text stays
    app.press("alt+2")
    s = app.wait(lambda s: s.editor_lines[0] == "alpha line" and s.active_tab.name == "b.txt", what="b.txt reloaded")
    assert not (s.tabs[1].dirty or s.tabs[1].changed or s.tabs[1].missing)


# ---------------------------------------------------------------- other ways a file changes

def test_atomic_replace_reloads_a_clean_tab(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/.a.txt.tmp", "agent v2\n")
    os.replace(ws.path("proj/.a.txt.tmp"), ws.path("proj/a.txt"))  # a new inode under the old name
    s = app.wait(lambda s: s.editor_lines[0] == "agent v2", what="reloaded")
    assert s.tab_names == ["a.txt"]
    t = s.tabs[0]
    assert not (t.dirty or t.changed or t.missing)


def test_atomic_replace_marks_a_dirty_tab_changed(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    ws.write("proj/.a.txt.tmp", "agent v2\n")
    os.replace(ws.path("proj/.a.txt.tmp"), ws.path("proj/a.txt"))
    s = wait_tab(app, "a.txt", dirty=True, changed=True, missing=False)
    assert s.editor_lines[0] == "MINE line one"


def test_touch_without_a_change_reloads_nothing(ltt, ws):
    app = open_file(ltt, ws)
    mtime_plus(ws, "proj/a.txt", 5)
    s = app.wait_status("Reloaded a.txt")
    assert s.editor_lines[:2] == ["line one", "line two"] and not s.tabs[0].dirty
    assert app.press("ctrl+z").status == "Nothing to undo."  # no undo step for an empty reload


def test_touch_marks_a_dirty_tab_changed(ltt, ws):
    # a new modification time is a new version: saving asks, though the text is the same
    app = open_file(ltt, ws)
    app.type("MINE ")
    mtime_plus(ws, "proj/a.txt", 5)
    wait_tab(app, "a.txt", dirty=True, changed=True)
    assert app.press("ctrl+s").question.startswith("a.txt changed on disk since it was opened!")


def test_inactive_clean_tab_reloads_in_the_background(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/b.txt", "beta\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"), cols=W)
    ws.write("proj/b.txt", "beta by the agent\n")
    s = app.wait_status("Reloaded b.txt: it changed on disk")
    assert s.active_tab.name == "a.txt" and s.editor_lines[0] == "alpha"
    assert not tab(s, "b.txt").changed
    s = app.press("alt+2")
    assert s.editor_lines[0] == "beta by the agent" and not s.tabs[1].dirty


def test_inactive_dirty_tab_is_marked_changed(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/b.txt", "beta\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"), cols=W)
    app.press("alt+2")
    app.type("MINE ")
    app.press("alt+1")
    ws.write("proj/b.txt", "beta by the agent\n")
    s = app.wait_status("b.txt changed on disk — your unsaved edits are kept")
    assert s.active_tab.name == "a.txt" and not s.tabs[0].changed
    assert tab(s, "b.txt").changed and tab(s, "b.txt").dirty
    s = app.press("alt+2")
    assert s.editor_lines[0] == "MINE beta"


def test_burst_of_writes_ends_on_the_last_version(ltt, ws):
    app = open_file(ltt, ws)
    for i in range(1, 51):
        ws.write("proj/a.txt", "".join(f"version {i} line {k}\n" for k in range(i % 7 + 1)))
    want = [f"version 50 line {k}" for k in range(50 % 7 + 1)] + [""]
    s = app.wait(lambda s: s.editor_lines == want, what="the last version")
    t = s.tabs[0]
    assert not (t.dirty or t.changed or t.missing)


def test_burst_of_writes_under_edits_keeps_them_and_r_takes_the_last(ltt, ws):
    app = open_file(ltt, ws)
    app.type("MINE ")
    for i in range(1, 51):
        ws.write("proj/a.txt", f"version {i}\n")
    s = wait_tab(app, "a.txt", dirty=True, changed=True)
    assert s.editor_lines[0] == "MINE line one"
    app.press("ctrl+s", "r")
    app.wait_status("Took the disk version of a.txt")
    assert app.snap().editor_lines[0] == "version 50"


def test_file_turned_binary_marks_the_clean_tab(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", b"\x00\x01\x02binary\n")
    s = app.wait_status("a.txt changed on disk but cannot be read: The file looks binary; editing is disabled.")
    assert s.tabs[0].changed and not s.tabs[0].dirty
    assert s.editor_lines[:2] == ["line one", "line two"]
    assert app.press("ctrl+s").question.startswith("a.txt changed on disk since it was opened!")


@pytest.mark.xfail(strict=True, reason="BUG: a clean tab whose file cannot be read is re-read every second "
                                       "and its message comes back over any other status")
def test_file_turned_binary_is_reported_once(ltt, ws):
    app = open_file(ltt, ws)
    ws.write("proj/a.txt", b"\x00\x01\x02binary\n")
    app.wait_status("cannot be read")
    s = app.press("esc")
    assert s.status.startswith("File panel.")
    time.sleep(2.5)  # on purpose: more than two disk checks
    assert app.snap().status.startswith("File panel.")


@pytest.mark.xfail(strict=True, reason="BUG: a tab opened through a symlink compares the link's own time and "
                                       "size, never the target's: it never reloads (test_save.py has the save side)")
def test_symlinked_tab_reloads_when_the_target_changes(ltt, ws):
    ws.write("proj/real.txt", "original\n")
    os.symlink(ws.path("proj/real.txt"), ws.path("proj/link.txt"))  # as CLAUDE.md -> AGENTS.md
    app = ltt(ws.path("proj/link.txt"), cols=W)
    ws.write("proj/real.txt", "agent edit\n")
    s = app.wait_status("Reloaded link.txt", timeout=3)
    assert s.editor_lines[0] == "agent edit"


# ---------------------------------------------------------------- agent-like workflows

def test_file_rewritten_while_typing_keeps_every_keystroke(ltt, ws):
    app = open_file(ltt, ws)
    app.type("abc", each=True)
    ws.write("proj/a.txt", "agent rewrote it\n")
    app.type("def", each=True)
    wait_tab(app, "a.txt", dirty=True, changed=True)
    s = app.type("ghi", each=True)
    assert s.editor_lines[:2] == ["abcdefghiline one", "line two"] and s.pos[:3] == (1, 3, 10)
    assert ws.read("proj/a.txt") == "agent rewrote it\n"


def two_branches(ws):
    """proj: a repository on main (a.txt, b.txt) with a branch `other` where a.txt differs and b.txt is gone."""
    ws.repo("proj", {"a.txt": "main text\n", "b.txt": "only on main\n"})
    ws.git("checkout", "-q", "-b", "other", cwd="proj")
    ws.write("proj/a.txt", "other text\n")
    ws.git("rm", "-q", "b.txt", cwd="proj")
    ws.git("commit", "-q", "-am", "other", cwd="proj")
    ws.git("checkout", "-q", "main", cwd="proj")


def test_git_checkout_reloads_a_clean_tab(ltt, ws):
    two_branches(ws)
    app = ltt(ws.path("proj/a.txt"), cols=W)
    ws.git("checkout", "-q", "other", cwd="proj")
    app.wait(lambda s: s.editor_lines[0] == "other text", what="the other branch")
    ws.git("checkout", "-q", "main", cwd="proj")
    s = app.wait(lambda s: s.editor_lines[0] == "main text", what="main again")
    assert not (s.tabs[0].dirty or s.tabs[0].changed)


def test_git_checkout_marks_a_dirty_tab_changed(ltt, ws):
    two_branches(ws)
    app = ltt(ws.path("proj/a.txt"), cols=W)
    app.type("MINE ")
    ws.git("checkout", "-q", "other", cwd="proj")
    s = wait_tab(app, "a.txt", dirty=True, changed=True)
    assert s.editor_lines[0] == "MINE main text"
    assert ws.read("proj/a.txt") == "other text\n"


def test_git_checkout_of_a_branch_without_the_file_marks_it_gone(ltt, ws):
    two_branches(ws)
    app = ltt(ws.path("proj/b.txt"), cols=W)
    ws.git("checkout", "-q", "other", cwd="proj")
    s = wait_tab(app, "b.txt", missing=True)
    assert s.editor_lines[0] == "only on main"
    ws.git("checkout", "-q", "main", cwd="proj")
    s = wait_tab(app, "b.txt", missing=False)
    assert s.editor_lines[0] == "only on main" and not (s.tabs[0].dirty or s.tabs[0].changed)


def test_git_stash_and_pop_reload_a_clean_tab(ltt, ws):
    ws.repo("proj", {"a.txt": "committed\n"})
    app = ltt(ws.path("proj/a.txt"), cols=W)
    ws.write("proj/a.txt", "committed\nagent line\n")
    app.wait(lambda s: s.editor_lines[:2] == ["committed", "agent line"], what="the agent's edit")
    ws.git("stash", "-q", cwd="proj")
    app.wait(lambda s: s.editor_lines == ["committed", ""], what="stashed away")
    ws.git("stash", "pop", "-q", cwd="proj")
    s = app.wait(lambda s: s.editor_lines[:2] == ["committed", "agent line"], what="popped back")
    assert not (s.tabs[0].dirty or s.tabs[0].changed)


def slow_git(ws, seconds):
    """A PATH whose git takes `seconds` before listing files: a folder search that runs that long."""
    d = os.path.join(ws.sys, "slowgit")  # with the other PATH tools, outside ltt's tree
    os.makedirs(d)
    for name in ("pbcopy", "pbpaste"):
        os.symlink(os.path.join(ws.bin, name), os.path.join(d, name))
    with open(os.path.join(d, "git"), "w") as f:
        f.write(f'#!/bin/sh\n[ "$1" = ls-files ] && /bin/sleep {seconds}\nexec "{ws.tools.git}" "$@"\n')
    os.chmod(os.path.join(d, "git"), 0o755)
    return d


def test_folder_search_keeps_going_through_an_outside_change(ltt, ws):
    # S2: any outside change cancelled a running folder search, leaving "found: 0" (t5.py)
    ws.write("proj/needle.txt", "the zqxneedle is here\n")
    app = ltt(ws.path("proj"), cols=W, path=slow_git(ws, 3))
    app.press("ctrl+f")
    app.type("zqxneedle")
    app.wait(lambda s: "searching…" in s.status_line, what="the search running")
    ws.write("proj/outside-change.txt", "x\n")
    app.wait(lambda s: s.tree_row("outside-change.txt") and "searching…" in s.status_line,
             what="the tree refreshed while the search still runs")
    s = app.wait(lambda s: "found: 1" in s.status_line, what="the search finished")
    assert [h.location for h in s.results] == ["needle.txt:1"]


def test_shown_results_stay_after_an_outside_change(ltt, ws):
    ws.write("proj/needle.txt", "the zqxneedle is here\n")
    app = ltt(ws.path("proj"), cols=W)
    app.press("ctrl+f")
    app.type("zqxneedle")
    app.wait(lambda s: "found: 1" in s.status_line, what="the search finished")
    app.press("esc")  # to the results
    ws.write("proj/new.txt", "x\n")
    s = app.wait_status("External change: created new.txt — left panel refreshed.")
    assert [h.location for h in s.results] == ["needle.txt:1"]
    s = app.press("enter")
    assert s.tab_names == ["needle.txt"] and s.editor_lines[0] == "the zqxneedle is here"


def test_git_commands_leave_the_status_and_the_results_alone(ltt, ws):
    # W2: git's own writes inside .git (index.lock, objects, refs) refreshed the tree and reset the search
    ws.repo("proj", {"needle.txt": "the zqxneedle is here\n", "b.txt": "b\n"})
    app = ltt(ws.path("proj"), cols=W)
    app.press("ctrl+f")
    app.type("zqxneedle")
    app.wait(lambda s: "found: 1" in s.status_line, what="the search finished")
    before = app.press("esc")
    assert before.status.startswith("Found: 1.")
    ws.git("status", cwd="proj")
    ws.git("commit", "-q", "--allow-empty", "-m", "agent commit", cwd="proj")
    ws.git("tag", "v1", cwd="proj")
    time.sleep(1.5)  # on purpose: the watcher's batch and a disk check come and go
    s = app.snap()
    assert s.status == before.status and s.tree_names == before.tree_names
    assert s.results == before.results and s.tree_selected.name == before.tree_selected.name
    ws.write("proj/marker.txt", "m\n")  # the watcher is alive, and git's batch went by on its own
    s = app.wait_status("External change: created marker.txt — left panel refreshed.")
    assert s.status == "External change: created marker.txt — left panel refreshed."


def test_writes_in_a_folded_folder_leave_the_status_alone(ltt, ws):
    # W2: writes inside a collapsed node_modules re-read the whole tree
    ws.write("proj/node_modules/pkg/index.js", "x" * 5000)
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj"), cols=W)
    before = app.press("down", "down")
    assert before.tree_selected.name == "a.txt"
    for i in range(30):
        ws.write(f"proj/node_modules/pkg{i}/index.js", "y\n")
    time.sleep(1.5)  # on purpose: the watcher's batch comes and goes
    s = app.snap()
    assert s.status == before.status and s.tree_names == before.tree_names
    assert s.tree_selected.name == "a.txt"
    ws.write("proj/marker.txt", "m\n")  # the watcher is alive, and those 30 writes went by on their own
    s = app.wait_status("External change: created marker.txt — left panel refreshed.")
    assert s.status == "External change: created marker.txt — left panel refreshed."
