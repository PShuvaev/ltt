"""Change highlighting: the tree's colours and the editor's gutter marks — against the last commit
in a git repository, against what was there when ltt started elsewhere."""
import os
import shutil

import pytest

from harness import shared
from keys_helpers import expand, open_in_tree, select

ADDED, MODIFIED = ("▎", "Green"), ("▎", "Yellow")
GONE_BELOW, GONE_ABOVE = ("▁", "Red"), ("▔", "Red")


def row(app, name, fg, timeout=10):
    """Wait for the tree row `name`, not selected, drawn in this colour; returns the frame."""
    def ok(s):
        r = s.tree_row(name)
        return r is not None and not r.selected and r.fg == fg
    return app.wait(ok, timeout, f"tree row {name!r} in {fg}")


def marks(s):
    """The gutter marks of the visible lines: {line number: (mark, colour)}."""
    return {r.number: (r.mark, r.mark_color) for r in s.editor_rows if r.mark != " "}


def wait_marks(app, want, timeout=10):
    return app.wait(lambda s: marks(s) == want, timeout, f"gutter marks {want}")


def to_tree_root(app):
    """Esc to the tree and select its first row, so no other row is drawn as selected."""
    app.press("esc", "home")
    return app.wait(lambda s: s.mode == "TREE" and s.tree_selected is not None and s.tree_selected.y == 1)


# ---------------------------------------------------------------- the tree in a git repository

def test_git_modified_tracked_file_is_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/a.txt", "ONE\n")
    app = ltt(ws.path("r"))
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_untracked_file_is_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    ws.write("r/new.txt", "new\n")
    app = ltt(ws.path("r"))
    s = row(app, "new.txt", "Green")
    assert s.tree_row("a.txt").fg == "Gray"


def test_git_unchanged_entries_keep_their_colours(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "src/b.txt": "two\n"})
    ws.write("r/fresh.txt", "f\n")
    app = ltt(ws.path("r"))
    s = row(app, "fresh.txt", "Green")  # git has answered
    assert s.tree_row("a.txt").fg == "Gray" and s.tree_row("src").fg == "White"


def test_git_folder_holding_a_modified_file_is_yellow(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n", "doc/c.txt": "three\n"})
    ws.write("r/src/a.txt", "ONE\n")
    app = ltt(ws.path("r"))
    s = row(app, "src", "Yellow")
    assert not s.tree_row("src").expanded and s.tree_row("doc").fg == "White"


def test_git_folder_holding_only_new_files_is_green(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n"})
    ws.write("r/src/new.txt", "new\n")
    app = ltt(ws.path("r"))
    row(app, "src", "Green")


def test_git_folder_holding_modified_and_new_files_is_yellow(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n"})
    ws.write("r/src/a.txt", "ONE\n")
    ws.write("r/src/new.txt", "new\n")
    app = ltt(ws.path("r"))
    row(app, "src", "Yellow")


def test_git_untracked_folder_and_everything_in_it_are_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    ws.write("r/fresh/x.txt", "x\n")
    ws.write("r/fresh/deep/y.txt", "y\n")
    app = ltt(ws.path("r"))
    row(app, "fresh", "Green")
    expand(app, "fresh", "deep")
    app.press("home")
    s = row(app, "x.txt", "Green")
    assert s.tree_row("deep").fg == "Green" and s.tree_row("y.txt").fg == "Green"


def test_git_deleted_file_makes_its_folder_yellow(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n", "doc/c.txt": "three\n"})
    ws.remove("r/src/a.txt")
    app = ltt(ws.path("r"))
    row(app, "src", "Yellow")
    expand(app, "src")
    app.press("home")
    s = app.snap()
    assert s.tree_row("a.txt") is None and s.tree_row("b.txt").fg == "Gray" and s.tree_row("doc").fg == "White"


def test_git_staged_deletion_makes_its_folder_yellow(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n"})
    ws.git("rm", "-q", "src/a.txt", cwd="r")
    app = ltt(ws.path("r"))
    row(app, "src", "Yellow")


def test_git_staged_rename_shows_the_new_name_yellow(ltt, ws):
    ws.repo("r", {"src/old.txt": "one\ntwo\nthree\n", "src/keep.txt": "k\n"})
    ws.git("mv", "src/old.txt", "src/new.txt", cwd="r")
    app = ltt(ws.path("r"))
    expand(app, "src")
    app.press("home")
    s = row(app, "new.txt", "Yellow")
    assert s.tree_row("src").fg == "Yellow" and s.tree_row("keep.txt").fg == "Gray"


def test_git_unstaged_move_shows_the_new_name_green_and_its_folder_yellow(ltt, ws):
    ws.repo("r", {"src/old.txt": "one\n", "src/keep.txt": "k\n"})
    ws.rename("r/src/old.txt", "r/src/new.txt")
    app = ltt(ws.path("r"))
    row(app, "src", "Yellow")
    expand(app, "src")
    app.press("home")
    s = row(app, "new.txt", "Green")
    assert s.tree_row("keep.txt").fg == "Gray"


def test_git_staged_new_file_is_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    ws.write("r/b.txt", "two\n")
    ws.git("add", "b.txt", cwd="r")
    app = ltt(ws.path("r"))
    row(app, "b.txt", "Green")


def test_git_staged_modification_is_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/a.txt", "ONE\n")
    ws.git("add", "a.txt", cwd="r")
    app = ltt(ws.path("r"))
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_file_with_a_merge_conflict_is_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "base\nline\n", "b.txt": "b\n"})
    ws.git("checkout", "-q", "-b", "other", cwd="r")
    ws.write("r/a.txt", "OTHER\nline\n")
    ws.git("commit", "-q", "-am", "other", cwd="r")
    ws.git("checkout", "-q", "main", cwd="r")
    ws.write("r/a.txt", "MAIN\nline\n")
    ws.git("commit", "-q", "-am", "main", cwd="r")
    ws.git("merge", "-q", "other", cwd="r", check=False)  # a conflict: "u UU" in git status
    app = ltt(ws.path("r"))
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_ignored_entries_are_never_marked(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"}, ignore="*.log\nbuild/\n")
    ws.write("r/debug.log", "x\n")
    ws.write("r/build/out.txt", "o\n")
    ws.write("r/new.txt", "n\n")
    app = ltt(ws.path("r"))
    s = row(app, "new.txt", "Green")  # git has answered
    assert s.tree_row("debug.log").fg == "DarkGray" and s.tree_row("build").fg == "DarkGray"


def test_git_ignored_file_changed_after_start_stays_unmarked(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"}, ignore="*.log\n")
    ws.write("r/debug.log", "x\n")
    app = ltt(ws.path("r"))
    ws.write("r/debug.log", "a longer log line\n")
    ws.write("r/new.txt", "n\n")
    s = row(app, "new.txt", "Green")  # the watcher and git have caught up with both writes
    app.wait(lambda s: s.tree_row("debug.log").size == "18", what="the log's new size")
    assert app.snap().tree_row("debug.log").fg == "DarkGray"


def test_git_root_is_never_marked(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    ws.write("r/a.txt", "ONE\n")
    ws.write("r/new.txt", "n\n")
    app = ltt(ws.path("r"))
    app.press("end")  # .git, the last row, takes the selection off the root
    s = row(app, "new.txt", "Green")
    assert s.tree_row("r").fg == "White" and s.tree_row("a.txt").fg == "Yellow"


def test_git_marks_clear_after_an_outside_commit(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/src/a.txt", "ONE\n")
    ws.write("r/new.txt", "n\n")
    app = ltt(ws.path("r"))
    row(app, "src", "Yellow")
    row(app, "new.txt", "Green")
    ws.git("add", "-A", cwd="r")
    ws.git("commit", "-q", "-m", "agent", cwd="r")
    s = row(app, "src", "White")
    assert s.tree_row("new.txt").fg == "Gray"


def test_git_marks_go_when_the_repository_is_removed(ltt, ws):
    ws.repo("p", {"a.txt": "one\n"})
    ws.write("p/a.txt", "ONE\n")  # changed against the commit, before ltt started
    app = ltt(ws.path("p"))
    row(app, "a.txt", "Yellow")
    ws.remove("p/.git")
    row(app, "a.txt", "Gray")  # outside git: a change made before the start is not marked


def test_git_commit_of_one_file_clears_only_its_mark(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/a.txt", "ONE\n")
    ws.write("r/b.txt", "TWO\n")
    app = ltt(ws.path("r"))
    row(app, "a.txt", "Yellow")
    row(app, "b.txt", "Yellow")
    ws.git("commit", "-q", "-m", "a only", "a.txt", cwd="r")
    s = row(app, "a.txt", "Gray")
    assert s.tree_row("b.txt").fg == "Yellow"


def test_git_marks_clear_after_checkout_of_the_file(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/a.txt", "ONE\n")
    app = ltt(ws.path("r"))
    row(app, "a.txt", "Yellow")
    ws.git("checkout", "--", "a.txt", cwd="r")
    row(app, "a.txt", "Gray")


def test_git_edit_saved_back_to_the_committed_text_clears_the_marks(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    app = ltt(ws.path("r"))
    open_in_tree(app, "a.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    to_tree_root(app)
    row(app, "a.txt", "Yellow")
    app.press("tab", "ctrl+z", "ctrl+s")
    s = app.wait_status("Saved: a.txt")
    assert marks(s) == {}
    to_tree_root(app)
    row(app, "a.txt", "Gray")


def test_git_file_changed_outside_after_start_turns_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    app = ltt(ws.path("r"))
    ws.write("r/a.txt", "ONE\n")
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_file_created_outside_after_start_turns_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    app = ltt(ws.path("r"))
    ws.write("r/src/agent.py", "print()\n")
    row(app, "src", "Green")


def test_git_file_saved_by_ltt_turns_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    app = ltt(ws.path("r"))
    open_in_tree(app, "a.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    to_tree_root(app)
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_file_made_with_ctrl_n_turns_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    app = ltt(ws.path("r"))
    app.press("ctrl+n")
    app.type("made.txt")
    app.press("enter")
    app.wait(lambda s: s.tab_names == ["made.txt"], what="made.txt open")
    to_tree_root(app)
    row(app, "made.txt", "Green")


def test_git_file_renamed_by_ltt_shows_the_new_name_green(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n"})
    app = ltt(ws.path("r"))
    expand(app, "src")
    select(app, "a.txt")
    app.press("ctrl+r")
    app.wait(lambda s: s.prompt == "Rename: a.txt", what="the rename prompt")
    app.press("ctrl+u")
    app.type("c.txt")
    app.press("enter")
    app.wait_status("Renamed: a.txt → c.txt")
    app.press("home")
    s = row(app, "c.txt", "Green")  # git sees a new file and a deleted one
    assert s.tree_row("src").fg == "Yellow" and s.tree_row("b.txt").fg == "Gray"


def test_git_root_inside_a_repository_marks_its_files(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n"})
    ws.write("r/src/a.txt", "ONE\n")
    ws.write("r/src/new.txt", "n\n")
    app = ltt(ws.path("r/src"))
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("new.txt").fg == "Green" and s.tree_row("b.txt").fg == "Gray"
    assert s.tree_row("src").fg == "White"  # the root itself, though git sees a changed folder


def test_git_root_inside_a_repository_clears_marks_after_an_outside_commit(ltt, ws):
    ws.repo("r", {"src/a.txt": "one\n", "src/b.txt": "two\n"})
    ws.write("r/src/a.txt", "ONE\n")
    app = ltt(ws.path("r/src"))
    row(app, "a.txt", "Yellow")
    ws.git("commit", "-q", "-am", "agent", cwd="r")
    row(app, "a.txt", "Gray", timeout=3)  # with the repository as the root it takes under a second


def test_git_repository_inside_a_plain_folder_marks_its_files(ltt, ws):
    ws.write("plain/notes.txt", "n\n")
    ws.repo("plain/repo", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("plain/repo/a.txt", "ONE\n")
    ws.write("plain/repo/new.txt", "n\n")
    app = ltt(ws.path("plain"))
    expand(app, "repo")
    app.press("home")
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("new.txt").fg == "Green" and s.tree_row("b.txt").fg == "Gray"
    assert s.tree_row("notes.txt").fg == "Gray"


def test_git_repository_folder_inside_a_plain_folder_is_marked_by_what_it_holds(ltt, ws):
    ws.write("plain/notes.txt", "n\n")
    ws.repo("plain/repo", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("plain/repo/a.txt", "ONE\n")
    app = ltt(ws.path("plain"))
    expand(app, "repo")
    app.press("home")
    row(app, "a.txt", "Yellow")  # git has answered
    row(app, "repo", "Yellow", timeout=2)


def test_git_init_in_a_subfolder_while_running_marks_against_its_commit(ltt, ws):
    ws.write("p/notes.txt", "n\n")
    app = ltt(ws.path("p"))
    ws.write("p/proj/a.txt", "one\n")  # outside git it would be yellow: written after the start
    ws.write("p/proj/b.txt", "two\n")
    for args in (("init", "-q", "-b", "main"), ("add", "-A"), ("commit", "-q", "-m", "first")):
        ws.git(*args, cwd="p/proj")
    app.wait_tree("proj")
    expand(app, "proj")
    app.press("home")
    row(app, "a.txt", "Gray")  # committed, unchanged
    ws.write("p/proj/a.txt", "ONE\n")
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_init_in_a_shown_root_switches_its_marks_to_git(ltt, ws):
    ws.write("p/a.txt", "one\n")
    app = ltt(ws.path("p"))
    ws.write("p/new.txt", "n\n")
    row(app, "new.txt", "Green")  # outside git: appeared since the start
    for args in (("init", "-q", "-b", "main"), ("add", "-A"), ("commit", "-q", "-m", "first")):
        ws.git(*args, cwd="p")
    app.wait_tree(".git")
    row(app, "new.txt", "Gray", timeout=3)  # in git: committed, unchanged (a restarted ltt shows it so)


def test_git_and_plain_roots_side_by_side(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "b.txt": "two\n"})
    ws.write("r/a.txt", "ONE\n")
    ws.write("p/sub/c.txt", "three\n")
    ws.write("p/d.txt", "four\n")
    app = ltt(ws.path("r"), ws.path("p"))
    row(app, "a.txt", "Yellow")
    ws.write("p/sub/c.txt", "THREE\n")  # outside git: changed since the start
    s = row(app, "sub", "Yellow")
    assert s.tree_row("r").fg == "White" and s.tree_row("p").fg == "White" and s.tree_row("d.txt").fg == "Gray"
    assert s.tree_row("b.txt").fg == "Gray"


def test_git_repository_without_commits_marks_every_file_new(ltt, ws):
    ws.repo("r", {"a.txt": "one\n", "src/b.txt": "two\n"}, commit=False)
    app = ltt(ws.path("r"))
    s = row(app, "a.txt", "Green")
    assert s.tree_row("src").fg == "Green"


def test_git_names_with_spaces_and_non_ascii_letters_are_marked(ltt, ws):
    ws.repo("r", {"my notes.txt": "one\n", "приветы/файл.txt": "two\n", "keep.txt": "k\n"})
    ws.write("r/my notes.txt", "ONE\n")
    ws.write("r/приветы/файл.txt", "TWO\n")
    ws.write("r/новый файл.txt", "n\n")
    app = ltt(ws.path("r"))
    s = row(app, "my notes.txt", "Yellow")
    assert s.tree_row("приветы").fg == "Yellow" and s.tree_row("новый файл.txt").fg == "Green"
    assert s.tree_row("keep.txt").fg == "Gray"


# ---------------------------------------------------------------- the gutter in a git repository

def test_gutter_marks_a_modified_line_yellow(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\nthree\n"})
    ws.write("r/a.txt", "one\nTWO\nthree\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {2: MODIFIED})


def test_gutter_marks_added_lines_green(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n"})
    ws.write("r/a.txt", "one\nnew\ntwo\nmore\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {2: ADDED, 4: ADDED})


def test_gutter_marks_removed_lines_under_the_line_before_them(ltt, ws):
    ws.repo("r", {"a.txt": "a\nb\nc\nd\n"})
    ws.write("r/a.txt", "a\nd\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {1: GONE_BELOW})


def test_gutter_marks_a_removed_first_line_over_line_one(ltt, ws):
    ws.repo("r", {"a.txt": "a\nb\nc\n"})
    ws.write("r/a.txt", "b\nc\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {1: GONE_ABOVE})


def test_gutter_marks_a_mix_of_changes(ltt, ws):
    ws.repo("r", {"a.txt": "a\nb\nc\nd\ne\n"})
    ws.write("r/a.txt", "a\nX\nc\ne\nf\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {2: MODIFIED, 3: GONE_BELOW, 5: ADDED})


def test_gutter_marks_every_open_tab_against_its_own_file(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n", "b.txt": "three\nfour\n"})
    ws.write("r/a.txt", "one\nTWO\n")
    ws.write("r/b.txt", "THREE\nfour\n")
    app = ltt(ws.path("r/a.txt"), ws.path("r/b.txt"))
    wait_marks(app, {2: MODIFIED})
    app.press("alt+2")
    s = app.wait_status("Tab 2/2: b.txt")
    assert marks(s) == {1: MODIFIED}


def test_gutter_shows_no_marks_on_an_unchanged_file(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n", "b.txt": "b\n"})
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/a.txt"))
    s = row(app, "b.txt", "Yellow")  # git has answered: the open file has its baseline
    assert marks(s) == {} and s.editor_lines[:2] == ["one", "two"]


def test_gutter_marks_every_line_of_an_untracked_file_green(ltt, ws):
    ws.repo("r", {"a.txt": "a\n"})
    ws.write("r/new.txt", "one\ntwo\n")
    app = ltt(ws.path("r/new.txt"))
    wait_marks(app, {1: ADDED, 2: ADDED, 3: ADDED})


def test_gutter_follows_edits_live(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\nthree\n", "b.txt": "b\n"})
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/a.txt"))
    row(app, "b.txt", "Yellow")  # git has answered: the open file has its baseline
    app.type("X")
    assert marks(app.snap()) == {1: MODIFIED}
    app.press("down", "end", "enter")
    app.type("new")
    assert marks(app.snap()) == {1: MODIFIED, 3: ADDED}
    app.press("ctrl+home", "delete")
    s = app.snap()
    assert s.editor_lines[:3] == ["one", "two", "new"] and marks(s) == {3: ADDED}


def test_gutter_marks_a_line_deleted_in_the_editor(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\nthree\n", "b.txt": "b\n"})
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/a.txt"))
    row(app, "b.txt", "Yellow")
    app.press("down", "shift+down", "delete")
    s = app.snap()
    assert s.editor_lines[:2] == ["one", "three"] and marks(s) == {1: GONE_BELOW}


def test_gutter_marks_disappear_when_an_edit_is_undone(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n", "b.txt": "b\n"})
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/a.txt"))
    row(app, "b.txt", "Yellow")
    app.type("X")
    assert marks(app.snap()) == {1: MODIFIED}
    app.press("ctrl+z")
    s = app.snap()
    assert s.editor_lines[0] == "one" and marks(s) == {}


def test_gutter_marks_stay_after_saving(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n", "b.txt": "b\n"})
    app = ltt(ws.path("r"))
    open_in_tree(app, "a.txt")
    app.press("down")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    to_tree_root(app)
    s = row(app, "a.txt", "Yellow")  # git has looked again after the save
    assert marks(s) == {2: MODIFIED}


def test_gutter_clears_after_an_outside_commit(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n"})
    app = ltt(ws.path("r/a.txt"))
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert marks(app.snap()) == {1: MODIFIED}
    ws.git("commit", "-q", "-am", "agent", cwd="r")
    s = wait_marks(app, {})
    assert s.editor_lines[0] == "Xone"


def test_gutter_clears_when_the_file_is_checked_out(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n"})
    ws.write("r/a.txt", "one\nTWO\n")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {2: MODIFIED})
    ws.git("checkout", "--", "a.txt", cwd="r")
    app.wait_editor(["one", "two"])  # the clean tab reloads
    s = wait_marks(app, {})
    assert not s.title_dirty


def test_gutter_marks_what_the_agent_rewrote(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\nthree\n", "b.txt": "b\n"})
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/a.txt"))
    row(app, "b.txt", "Yellow")
    ws.write("r/a.txt", "one\nTWO\nthree\nfour\n")
    app.wait_editor(["one", "TWO", "three", "four"])  # a clean tab reloads
    wait_marks(app, {2: MODIFIED, 4: ADDED})


def test_gutter_of_a_new_file_clears_once_it_is_committed(ltt, ws):
    ws.repo("r", {"a.txt": "a\n"})
    ws.write("r/new.txt", "one\ntwo\n")
    app = ltt(ws.path("r/new.txt"))
    wait_marks(app, {1: ADDED, 2: ADDED, 3: ADDED})
    ws.git("add", "new.txt", cwd="r")
    ws.git("commit", "-q", "-m", "agent", cwd="r")
    wait_marks(app, {})


def test_gutter_compares_with_the_last_commit_not_the_index(ltt, ws):
    ws.repo("r", {"a.txt": "one\ntwo\n", "b.txt": "b\n"})
    ws.write("r/a.txt", "one\nTWO\n")
    ws.git("add", "a.txt", cwd="r")
    app = ltt(ws.path("r/a.txt"))
    wait_marks(app, {2: MODIFIED})


def test_gutter_ignores_crlf_line_endings(ltt, ws):
    ws.repo("r", {"a.txt": "one\r\ntwo\r\nthree\r\n", "b.txt": "b\r\n"})
    ws.write("r/a.txt", "one\r\nTWO\r\nthree\r\n")
    ws.write("r/b.txt", "B\r\n")
    app = ltt(ws.path("r/a.txt"))
    s = wait_marks(app, {2: MODIFIED})
    assert s.editor_lines[:3] == ["one", "TWO", "three"]


def test_gutter_of_a_file_made_with_ctrl_n_is_green_as_you_type(ltt, ws):
    ws.repo("r", {"a.txt": "one\n"})
    app = ltt(ws.path("r"))
    app.press("ctrl+n")
    app.type("made.txt")
    app.press("enter")
    app.wait(lambda s: s.tab_names == ["made.txt"] and s.mode == "EDITOR", what="made.txt open")
    app.type("hello")
    app.press("enter")
    app.type("world")
    wait_marks(app, {1: ADDED, 2: ADDED})


def test_gutter_of_a_file_with_spaces_and_non_ascii_letters_in_its_path(ltt, ws):
    ws.repo("r", {"папка с пробелом/my file.txt": "one\ntwo\n"})
    ws.write("r/папка с пробелом/my file.txt", "one\nTWO\n")
    app = ltt(ws.path("r/папка с пробелом/my file.txt"))
    wait_marks(app, {2: MODIFIED})


def big_text():
    """A text file over the 8 MB limit for comparing with the committed version."""
    with shared("keys-big-text") as (path, ready):
        f = os.path.join(path, "big.txt")
        if not ready:
            with open(f, "w") as out:
                for i in range(150_000):
                    out.write(f"line {i:06d} " + "x" * 48 + "\n")
    return f


def test_gutter_of_a_file_over_8mb_compares_with_its_text_as_first_opened(ltt, ws):
    ws.repo("r", {"b.txt": "b\n"})
    shutil.copyfile(big_text(), ws.path("r/big.txt"))
    assert os.path.getsize(ws.path("r/big.txt")) > 8 << 20
    ws.git("add", "big.txt", cwd="r")
    ws.git("commit", "-q", "-m", "big", cwd="r")
    with open(ws.path("r/big.txt"), "r+b") as f:  # line 1 differs from the commit
        f.write(b"LINE")
    ws.write("r/b.txt", "B\n")
    app = ltt(ws.path("r/big.txt"))
    s = row(app, "b.txt", "Yellow", timeout=20)  # git has answered
    assert s.editor_lines[0].startswith("LINE 000000") and marks(s) == {}
    app.press("down")
    app.type("X")
    assert marks(app.snap()) == {2: MODIFIED}


# ---------------------------------------------------------------- outside git: what changed since ltt started

def test_plain_file_changed_after_start_is_yellow(ltt, ws):
    ws.write("p/sub/a.txt", "one\n")
    ws.write("p/sub/b.txt", "two\n")
    app = ltt(ws.path("p"))
    expand(app, "sub")
    app.press("home")
    ws.write("p/sub/a.txt", "ONE\n")
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_plain_file_in_a_root_changed_after_start_is_redrawn_yellow(ltt, ws):
    ws.write("p/a.txt", "one\n")
    ws.write("p/b.txt", "two\n")
    app = ltt(ws.path("p"))
    ws.write("p/a.txt", "changed\n")
    row(app, "a.txt", "Yellow", timeout=3)


def test_plain_file_replaced_atomically_is_yellow_not_green(ltt, ws):  # how agents and editors save
    ws.write("p/sub/a.txt", "one\n")
    ws.write("p/sub/b.txt", "two\n")
    app = ltt(ws.path("p"))
    expand(app, "sub")
    app.press("home")
    ws.write("p/sub/.a.txt.tmp", "ONE, rewritten\n")
    ws.rename("p/sub/.a.txt.tmp", "p/sub/a.txt")
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row(".a.txt.tmp") is None and s.tree_row("b.txt").fg == "Gray"


def test_plain_file_created_after_start_is_green(ltt, ws):
    ws.write("p/a.txt", "one\n")
    app = ltt(ws.path("p"))
    ws.write("p/new.txt", "new\n")
    s = row(app, "new.txt", "Green")
    assert s.tree_row("a.txt").fg == "Gray"


def test_plain_entries_from_before_the_start_are_not_marked(ltt, ws):
    ws.write("p/a.txt", "one\n")
    ws.write("p/sub/b.txt", "two\n")
    app = ltt(ws.path("p"))
    ws.write("p/marker.txt", "m\n")
    s = row(app, "marker.txt", "Green")
    assert s.tree_row("a.txt").fg == "Gray" and s.tree_row("sub").fg == "White"


def test_plain_folders_above_a_change_are_yellow(ltt, ws):
    ws.write("p/sub/deep/c.txt", "cee\n")
    ws.write("p/sub/b.txt", "bee\n")
    ws.write("p/other/d.txt", "dee\n")
    app = ltt(ws.path("p"))
    expand(app, "sub", "deep")
    app.press("home")
    ws.write("p/sub/deep/c.txt", "CEE\n")
    s = row(app, "c.txt", "Yellow")
    assert s.tree_row("deep").fg == "Yellow" and s.tree_row("sub").fg == "Yellow"
    assert s.tree_row("b.txt").fg == "Gray" and s.tree_row("other").fg == "White"


def test_plain_collapsed_folder_above_a_new_file_is_yellow(ltt, ws):
    ws.write("p/sub/b.txt", "bee\n")
    ws.write("p/other/d.txt", "dee\n")
    app = ltt(ws.path("p"))
    ws.write("p/sub/new.txt", "new\n")
    s = row(app, "sub", "Yellow")
    assert not s.tree_row("sub").expanded and s.tree_row("other").fg == "White"


def test_plain_root_is_never_marked(ltt, ws):
    ws.write("p/a.txt", "one\n")
    ws.write("p/b.txt", "a bigger file\n")
    app = ltt(ws.path("p"))
    app.press("end")
    ws.write("p/a.txt", "ONE\n")
    ws.write("p/new.txt", "n\n")
    s = row(app, "new.txt", "Green")
    assert s.tree_row("p").fg == "White"


def test_plain_file_saved_by_ltt_turns_yellow(ltt, ws):
    ws.write("p/sub/a.txt", "one\n")
    ws.write("p/sub/b.txt", "two\n")
    app = ltt(ws.path("p"))
    expand(app, "sub")
    open_in_tree(app, "a.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    to_tree_root(app)
    s = row(app, "a.txt", "Yellow")
    assert s.tree_row("b.txt").fg == "Gray"


def test_plain_file_made_with_ctrl_n_is_green_and_its_folder_yellow(ltt, ws):
    ws.write("p/sub/a.txt", "one\n")
    ws.write("p/other/b.txt", "two\n")
    app = ltt(ws.path("p"))
    select(app, "sub")
    app.press("ctrl+n")
    app.type("made.txt")
    app.press("enter")
    app.wait(lambda s: s.tab_names == ["made.txt"], what="made.txt open")
    to_tree_root(app)
    s = row(app, "made.txt", "Green")
    s = row(app, "sub", "Yellow")
    assert s.tree_row("other").fg == "White" and s.tree_row("a.txt").fg == "Gray"


def test_plain_folder_made_with_ctrl_shift_n_is_green(ltt, ws):
    ws.write("p/a.txt", "one\n")
    app = ltt(ws.path("p"))
    app.press("ctrl+shift+n")
    app.wait(lambda s: s.prompt is not None and s.prompt.startswith("New folder:"), what="the new-folder prompt")
    app.type("made")
    app.press("enter")
    app.wait_status("Folder created")
    app.press("home")
    row(app, "made", "Green")


def test_plain_gutter_compares_with_the_text_as_first_opened(ltt, ws):
    ws.write("p/a.txt", "one\ntwo\nthree\n")
    app = ltt(ws.path("p/a.txt"))
    assert marks(app.snap()) == {}
    app.type("X")
    app.press("down", "end", "enter")
    app.type("added")
    assert marks(app.snap()) == {1: MODIFIED, 3: ADDED}


def test_plain_gutter_marks_stay_after_saving(ltt, ws):
    ws.write("p/sub/a.txt", "one\ntwo\n")
    ws.write("p/sub/b.txt", "two\n")
    app = ltt(ws.path("p"))
    expand(app, "sub")
    open_in_tree(app, "a.txt")
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    to_tree_root(app)
    s = row(app, "a.txt", "Yellow")  # the watcher has seen the save
    assert marks(s) == {1: MODIFIED}


def test_plain_gutter_marks_what_changed_outside_since_first_opened(ltt, ws):
    ws.write("p/a.txt", "one\ntwo\nthree\n")
    app = ltt(ws.path("p/a.txt"))
    ws.write("p/a.txt", "one\nthree\nfour\n")
    app.wait_editor(["one", "three", "four"])
    wait_marks(app, {1: GONE_BELOW, 3: ADDED})


def test_plain_gutter_of_a_file_created_after_start_is_all_green(ltt, ws):
    ws.write("p/a.txt", "one\n")
    app = ltt(ws.path("p"))
    ws.write("p/new.txt", "fresh\nlines\n")
    row(app, "new.txt", "Green")
    open_in_tree(app, "new.txt")
    wait_marks(app, {1: ADDED, 2: ADDED, 3: ADDED})


def test_plain_gutter_of_a_file_made_with_ctrl_n_is_all_green(ltt, ws):
    ws.write("p/a.txt", "one\n")
    app = ltt(ws.path("p"))
    app.press("ctrl+n")
    app.type("made.txt")
    app.press("enter")
    app.wait(lambda s: s.tab_names == ["made.txt"] and s.mode == "EDITOR", what="made.txt open")
    assert marks(app.snap()) == {1: ADDED}
    app.type("hello")
    app.press("enter")
    app.type("world")
    assert marks(app.snap()) == {1: ADDED, 2: ADDED}


def test_a_symlink_in_a_repository_marks_only_the_lines_that_changed(ltt, ws):  # Helix's git/test.rs 'symlink'
    root = ws.repo("r", {"file.txt": "a\nb\nc\n"})
    os.symlink("file.txt", os.path.join(root, "link.txt"))
    ws.git("add", "link.txt", cwd="r")
    ws.git("commit", "-q", "-m", "link", cwd="r")
    ws.write("r/file.txt", "a\nB\nc\n")
    app = ltt(ws.path("r/link.txt"))
    wait_marks(app, {2: MODIFIED}, timeout=5)
