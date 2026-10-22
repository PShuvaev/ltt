"""The folder tree: moving around, opening, order and sizes, git dimming, following the disk, the mouse."""
import os
import shutil
import stat
import sys
import time
import unicodedata

import pytest

from harness import keys as K
from tree_helpers import DOWN, burst_fixture, first_frame, names, select, size_fixture


def basic(ws):
    """proj: dir/ (4 bytes inside), big.txt (20), small.txt (1)."""
    ws.write("proj/dir/one.txt", "1")
    ws.write("proj/dir/three.txt", "333")
    ws.write("proj/big.txt", "b" * 20)
    ws.write("proj/small.txt", "s")
    return ws.path("proj")


def long_tree(ws, n=60):
    """proj with f00.txt … f59.txt, f00 the largest (listed first)."""
    for i in range(n):
        ws.write(f"proj/f{i:02d}.txt", "x" * (100 - i))
    return ws.path("proj")


# ---------------------------------------------------------------- moving around

def test_starts_with_the_root_selected_and_expanded(ltt, ws):
    s = ltt(basic(ws)).snap()
    assert s.mode == "TREE" and s.status == "Ready. Enter opens a file."
    assert names(s) == ["proj", "dir", "big.txt", "small.txt"]
    assert s.tree_selected.name == "proj" and s.tree[0].expanded and not s.tree_row("dir").expanded


def test_down_and_up_move_the_selection(ltt, ws):
    app = ltt(basic(ws))
    assert app.press("down").tree_selected.name == "dir"
    assert app.press("down").tree_selected.name == "big.txt"
    assert app.press("up").tree_selected.name == "dir"


def test_up_at_the_top_and_down_at_the_bottom_stay(ltt, ws):
    app = ltt(basic(ws))
    assert app.press("up").tree_selected.name == "proj"
    app.press("end")
    assert app.press("down").tree_selected.name == "small.txt"


def test_home_and_end_jump_to_the_first_and_last_row(ltt, ws):
    app = ltt(basic(ws))
    assert app.press("end").tree_selected.name == "small.txt"
    assert app.press("home").tree_selected.name == "proj"


def test_title_shows_the_selected_path(ltt, ws):
    app = ltt(basic(ws), cols=300)
    select(app, "big.txt")
    assert app.snap().title_path == ws.path("proj/big.txt")


def test_page_down_and_up_move_a_screen_at_a_time(ltt, ws):
    app = ltt(long_tree(ws))
    page = len(app.snap().tree)  # rows the tree shows: 27 in 30 lines
    s = app.press("pgdn")
    assert s.tree_selected.name == f"f{page - 1:02d}.txt"  # row 0 is the root
    s = app.press("pgdn")
    assert s.tree_selected.name == f"f{2 * page - 1:02d}.txt"
    assert s.tree_selected.y == s.tree[-1].y  # scrolled: the selection is on the last visible row
    s = app.press("pgdn")
    assert s.tree_selected.name == "f59.txt"  # no further than the end
    s = app.press("pgup")
    assert s.tree_selected.name == f"f{59 - page:02d}.txt" and s.tree_selected.y == 1


def test_moving_past_the_last_visible_row_scrolls_by_one(ltt, ws):
    app = ltt(long_tree(ws))
    page = len(app.snap().tree)
    app.write(DOWN * (page - 1))
    s = app.settle()
    assert s.tree[0].name == "proj" and s.tree_selected.y == s.tree[-1].y
    s = app.press("down")
    assert s.tree[0].name == "f00.txt" and s.tree_selected.name == f"f{page - 1:02d}.txt"
    assert s.tree_selected.y == s.tree[-1].y


def test_end_shows_the_last_rows_and_home_the_first(ltt, ws):
    app = ltt(long_tree(ws))
    s = app.press("end")
    assert s.tree[-1].name == "f59.txt" and s.tree_selected.name == "f59.txt" and s.tree_row("proj") is None
    s = app.press("home")
    assert s.tree[0].name == "proj" and s.tree_selected.y == 1


def test_right_expands_a_folder_then_moves_into_it(ltt, ws):
    app = ltt(basic(ws))
    select(app, "dir")
    s = app.press("right")
    assert names(s) == ["proj", "dir", "three.txt", "one.txt", "big.txt", "small.txt"]
    assert s.tree_row("dir").expanded and s.tree_selected.name == "dir"
    assert s.tree_row("three.txt").depth == 2
    assert app.press("right").tree_selected.name == "three.txt"


def test_right_on_an_expanded_empty_folder_stays(ltt, ws):
    ws.mkdir("proj/empty")
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    select(app, "empty")
    s = app.press("right")
    assert s.tree_row("empty").expanded and names(s) == ["proj", "empty", "a.txt"]
    assert app.press("right").tree_selected.name == "empty"


def test_left_collapses_a_folder_then_goes_to_its_parent(ltt, ws):
    app = ltt(basic(ws))
    select(app, "dir")
    app.press("right")
    select(app, "one.txt")
    s = app.press("left")
    assert s.tree_selected.name == "dir" and s.tree_row("dir").expanded
    s = app.press("left")
    assert s.tree_selected.name == "dir" and not s.tree_row("dir").expanded and s.tree_row("one.txt") is None
    assert app.press("left").tree_selected.name == "proj"


def test_left_on_the_root_collapses_it_and_then_stays(ltt, ws):
    app = ltt(basic(ws))
    s = app.press("left")
    assert names(s) == ["proj"] and not s.tree[0].expanded
    s = app.press("left")
    assert names(s) == ["proj"] and s.tree_selected.name == "proj"
    s = app.press("right")
    assert names(s) == ["proj", "dir", "big.txt", "small.txt"]


def test_right_on_a_file_opens_it_in_the_editor(ltt, ws):
    app = ltt(basic(ws))
    select(app, "small.txt")
    s = app.press("right")
    assert s.mode == "EDITOR" and s.tab_names == ["small.txt"] and s.editor_lines == ["s"]


def test_enter_toggles_a_folder_and_keeps_the_focus(ltt, ws):
    app = ltt(basic(ws))
    select(app, "dir")
    s = app.press("enter")
    assert s.mode == "TREE" and s.tree_row("dir").expanded and s.tree_row("one.txt") is not None
    s = app.press("enter")
    assert s.mode == "TREE" and not s.tree_row("dir").expanded and s.tree_row("one.txt") is None


def test_enter_on_the_root_collapses_it(ltt, ws):
    app = ltt(basic(ws))
    assert names(app.press("enter")) == ["proj"]
    assert names(app.press("enter")) == ["proj", "dir", "big.txt", "small.txt"]


def test_enter_on_a_file_opens_it_and_focuses_the_editor(ltt, ws):
    app = ltt(basic(ws))
    select(app, "big.txt")
    s = app.press("enter")
    assert s.mode == "EDITOR" and s.tab_names == ["big.txt"] and s.editor_lines == ["b" * 20]
    assert s.status == "Opened big.txt (1 lines), tab 1/1. Ctrl+W closes."
    assert s.tree_selected.name == "big.txt" and s.tree_selected.bg == "DarkGray"


def test_enter_on_an_open_file_switches_to_its_tab(ltt, ws):
    app = ltt(basic(ws))
    select(app, "big.txt")
    app.press("enter", "esc")
    select(app, "small.txt")
    app.press("enter", "esc")
    select(app, "big.txt")
    s = app.press("enter")
    assert s.tab_names == ["big.txt", "small.txt"] and s.active_tab.name == "big.txt"
    assert s.mode == "EDITOR" and s.status == "Tab: big.txt"


def test_tab_without_an_open_file_asks_to_open_one(ltt, ws):
    app = ltt(basic(ws))
    s = app.press("tab")
    assert s.mode == "TREE" and s.status == "Open a file with Enter first."


def test_tab_goes_to_the_editor_and_esc_comes_back(ltt, ws):
    app = ltt(basic(ws))
    select(app, "small.txt")
    app.press("enter")
    s = app.press("esc")
    assert s.mode == "TREE"
    assert s.status == "File panel. Enter opens; Ctrl+F searches the selection, Ctrl+F+F all folders."
    s = app.press("tab")
    assert s.mode == "EDITOR" and s.status == "Editing. Esc returns to the tree."


def test_selection_colours_follow_the_focus(ltt, ws):
    app = ltt(basic(ws))
    s = select(app, "big.txt")
    size_x = s.left_w - 1
    sel, folder, other = s.tree_row("big.txt"), s.tree_row("dir"), s.tree_row("small.txt")
    assert (sel.bg, sel.fg, s.fg(size_x, sel.y)) == ("DarkCyan", "White", "Gray")
    assert (folder.bg, folder.fg, s.fg(size_x, folder.y)) == ("Black", "White", "DarkGray")
    assert (other.bg, other.fg) == ("Black", "Gray")
    s = app.press("enter")  # the editor takes the focus: the tree's selection dims
    sel = s.tree_row("big.txt")
    assert (sel.bg, sel.fg) == ("DarkGray", "White")
    s = app.press("esc")
    assert s.tree_row("big.txt").bg == "DarkCyan"


# ---------------------------------------------------------------- loading on demand, deep and hidden entries

def test_a_folder_is_read_when_it_is_expanded(ltt, ws):
    ws.write("proj/later/old.txt", "o")
    app = ltt(ws.path("proj"))
    ws.write("proj/later/new.txt", "nn")  # behind ltt's back, in a folder never opened
    select(app, "later")
    s = app.press("right")
    assert names(s) == ["proj", "later", "new.txt", "old.txt"]


@pytest.mark.perf
def test_a_folder_of_thousands_of_files_opens_at_once(ltt):
    app = ltt(os.path.join(size_fixture(), "bytes"))  # many: 3000 files
    app.wait(lambda s: s.tree_row("many").size == "2.9K", what="counted")
    app.press("down")
    t0 = time.monotonic()
    app.write(b"\x1b[C")
    s = app.wait(lambda s: s.tree_row("f0000") is not None, what="the files listed")
    assert s.at - t0 < 1.0, f"expanding took {s.at - t0:.2f} s"


def test_deep_folders_open_level_by_level(ltt, ws):
    ws.write("proj/a/b/c/d/e/f/deep.txt", "d")
    app = ltt(ws.path("proj"))
    app.write(b"\x1b[C" * 13)  # Right: into a, then expand and enter each level down to the file
    s = app.settle()
    assert [(r.name, r.depth) for r in s.tree] == [
        ("proj", 0), ("a", 1), ("b", 2), ("c", 3), ("d", 4), ("e", 5), ("f", 6), ("deep.txt", 7)]
    assert s.tree_selected.name == "deep.txt"
    assert s.lines[s.tree_selected.y].startswith(" " * 16 + "deep.txt")


def test_dot_files_and_hidden_files_are_listed(ltt, ws):
    ws.write("proj/.env", "X=1\n")
    ws.write("proj/.config/settings.json", "{}\n")
    ws.write("proj/hidden-flag.txt", "h")
    if hasattr(os, "chflags"):
        os.chflags(ws.path("proj/hidden-flag.txt"), stat.UF_HIDDEN)  # hidden in Finder
    s = ltt(ws.path("proj")).snap()
    assert names(s) == ["proj", ".config", ".env", "hidden-flag.txt"]
    assert s.tree_row(".env").fg == "Gray"


def test_gitignore_file_is_dimmed_and_last_even_outside_git(ltt, ws):
    ws.write("proj/.gitignore", "*.log\n" * 100)
    ws.write("proj/a.txt", "a")
    s = ltt(ws.path("proj")).snap()
    assert names(s) == ["proj", "a.txt", ".gitignore"]
    assert s.tree_row(".gitignore").fg == "DarkGray" and s.tree_row("a.txt").fg == "Gray"


def test_an_unreadable_folder_opens_empty(ltt, ws):
    ws.write("proj/locked/secret.txt", "s")
    ws.write("proj/a.txt", "a")
    os.chmod(ws.path("proj/locked"), 0)
    try:
        app = ltt(ws.path("proj"))
        select(app, "locked")
        s = app.press("right")
        assert s.tree_row("locked").expanded and names(s) == ["proj", "locked", "a.txt"]
        assert s.status == "Ready. Enter opens a file."
    finally:
        os.chmod(ws.path("proj/locked"), 0o755)


def test_a_dangling_symlink_is_listed_and_does_not_open(ltt, ws):
    ws.write("proj/a.txt", "a")
    os.symlink(ws.path("proj/nowhere.txt"), ws.path("proj/dangling.txt"))
    app = ltt(ws.path("proj"), cols=300)
    select(app, "dangling.txt")
    s = app.press("enter")
    assert s.mode == "TREE" and s.tabs == []
    assert s.status.startswith("Error: ") and "dangling.txt" in s.status


def test_a_symlink_loop_neither_hangs_nor_counts(ltt, ws):
    ws.write("proj/a.txt", "a")
    os.symlink(ws.path("proj"), ws.path("proj/loop"))  # a link to its own folder
    app = ltt(ws.path("proj"))
    s = select(app, "loop")
    assert s.tree_row("loop").size == "0"  # links are not followed when counting
    s = app.press("right")
    assert [(r.name, r.depth) for r in s.tree] == [
        ("proj", 0), ("loop", 1), ("loop", 2), ("a.txt", 2), ("a.txt", 1)]


def test_control_characters_in_names_are_shown_harmlessly(ltt, ws):
    # D12: a file name could drive the terminal, here set the window title
    ws.write("proj/evil\x1b]0;PWNED\x07.txt", "e")
    app = ltt(ws.path("proj"))
    s = app.snap()
    assert s.tree[1].name == "evil␛]0;PWNED␇.txt" and not s.window_title
    assert b"\x1b]0;PWNED" not in app.output


def test_wide_names_keep_the_size_column_in_place(ltt, ws):
    ws.write("proj/日本語のファイル名です.txt", "x" * 3000)
    ws.write("proj/emoji👀.txt", "x" * 30)
    s = ltt(ws.path("proj")).snap()
    wide, emoji = s.tree[1], s.tree[2]
    assert wide.name == "日本語のファイル名で" and emoji.name == "emoji👀.txt"  # cut on a whole character
    assert (wide.size, emoji.size) == ("2.9K", "30")
    assert s.text(wide.y, s.left_w - 4, s.left_w + 1) == "2.9K│"


def test_opening_a_fifo_does_not_hang(ltt, ws):
    # D11: reading a FIFO would block forever
    ws.write("proj/a.txt", "a")
    os.mkfifo(ws.path("proj/pipe"))
    app = ltt(ws.path("proj"), cols=300)
    s = select(app, "pipe")
    assert s.tree_row("pipe").size == "0"
    s = app.press("enter")
    assert s.status == "Error: Not a regular file: " + ws.path("proj/pipe")
    assert s.mode == "TREE" and s.tabs == []
    assert app.press("up").tree_selected.name == "a.txt"  # still answering


@pytest.mark.skipif(sys.platform != "darwin", reason="macOS file names: both Unicode forms are one file")
def test_a_decomposed_file_name_opens_as_one_tab(ltt, ws):
    # V27: macOS treats both Unicode forms of a name as one file
    nfd = unicodedata.normalize("NFD", "café.txt")
    ws.write("proj/" + nfd, "coffee\n")
    nfc_path = ws.path("proj/" + unicodedata.normalize("NFC", "café.txt"))
    app = ltt(ws.path("proj"), nfc_path)  # the tab: the composed spelling; the tree: the name as stored
    app.wait_mode("EDITOR")
    app.press("esc")
    select(app, "café.txt")
    s = app.press("enter")
    assert s.tab_names == ["café.txt"] and s.status == "Tab: café.txt"
    app.press("esc", "ctrl+o")
    app.type(ws.path("proj/" + nfd))
    s = app.press("enter")
    assert s.tab_names == ["café.txt"]


# ---------------------------------------------------------------- order and sizes

def test_folders_come_first_then_files_largest_first(ltt, ws):
    ws.write("proj/small_dir/f", "x" * 10)
    ws.write("proj/big_dir/f", "x" * 5000)
    ws.write("proj/huge.txt", "x" * 9000)  # larger than every folder, still after them
    ws.write("proj/mid.txt", "x" * 100)
    ws.write("proj/tiny.txt", "x")
    s = ltt(ws.path("proj")).snap()
    assert names(s) == ["proj", "big_dir", "small_dir", "huge.txt", "mid.txt", "tiny.txt"]


def test_equal_sizes_are_ordered_by_name_ignoring_case(ltt, ws):
    for name in ("b.txt", "D.txt", "a.txt", "C.txt", "_.txt"):
        ws.write("proj/" + name, "x")
    s = ltt(ws.path("proj")).snap()
    assert names(s) == ["proj", "a.txt", "b.txt", "C.txt", "D.txt", "_.txt"]  # compared upper-cased: _ after Z


def test_file_sizes_have_two_significant_digits(ltt, ws):
    sizes = {"zero": 0, "seven": 7, "b99": 99, "b512": 512, "b999": 999, "k1.5": 1536, "k10": 10240,
             "k980": 999_000, "m1": 1 << 20, "g1.5": 1_610_612_736}
    for name, n in sizes.items():
        ws.write("proj/" + name, "")
        os.truncate(ws.path("proj/" + name), n)  # sparse: no space taken
    s = ltt(ws.path("proj")).snap()
    shown = {name: s.tree_row(name).size for name in sizes}
    assert shown == {"zero": "0", "seven": "7", "b99": "99", "b512": "510", "b999": "1K", "k1.5": "1.5K",
                     "k10": "10K", "k980": "980K", "m1": "1M", "g1.5": "1.5G"}


def test_a_folder_shows_the_total_of_everything_inside(ltt, ws):
    ws.write("proj/dir/a.bin", "x" * 1000)
    ws.write("proj/dir/sub/deeper/b.bin", "x" * 536)
    ws.mkdir("proj/empty")
    s = ltt(ws.path("proj")).snap()
    assert s.tree_row("dir").size == "1.5K" and s.tree_row("empty").size == "0"
    assert s.tree_row("proj").size == ""  # a root shows no size of its own


def test_a_big_folder_shows_a_lower_bound_until_counted(ltt):
    app = ltt(os.path.join(size_fixture(), "bytes"))  # many: 3000 files of one byte
    assert first_frame(app).tree_row("many").size.startswith("≥")
    app.wait(lambda s: s.tree_row("many").size == "2.9K", what="the counted size")


def test_a_folder_not_counted_yet_shows_an_ellipsis(ltt):
    app = ltt(os.path.join(size_fixture(), "empty"))  # many: 3000 empty files
    assert first_frame(app).tree_row("many").size == "…"
    app.wait(lambda s: s.tree_row("many").size == "0", what="the counted size")


def test_folder_size_follows_a_change_deep_inside(ltt, ws):
    # V21: a change deep inside counts the folder again (it was shown stale, as exact)
    ws.write("proj/aaa/b/c/small.txt", "0123456789")
    ws.write("proj/zzz/f.txt", "z" * 100)
    app = ltt(ws.path("proj"))
    s = app.snap()
    assert names(s) == ["proj", "zzz", "aaa"] and s.tree_row("aaa").size == "10"
    ws.write("proj/aaa/b/c/big.bin", b"\0" * 5000)
    s = app.wait(lambda s: s.tree_row("aaa").size == "4.9K", what="aaa counted again")
    assert names(s) == ["proj", "aaa", "zzz"]  # re-sorted: the larger folder first
    ws.remove("proj/aaa/b/c/big.bin")
    s = app.wait(lambda s: s.tree_row("aaa").size == "10", what="aaa counted again")
    assert names(s) == ["proj", "zzz", "aaa"]


def test_file_size_follows_its_contents(ltt, ws):
    ws.write("proj/grow.txt", "g")
    ws.write("proj/other.txt", "oo")
    app = ltt(ws.path("proj"))
    assert names(app.snap()) == ["proj", "other.txt", "grow.txt"]
    ws.append("proj/grow.txt", "g" * 2047)
    s = app.wait(lambda s: s.tree_row("grow.txt").size == "2K", what="the new size")
    assert names(s) == ["proj", "grow.txt", "other.txt"]


def test_size_column_stays_in_a_narrow_panel(ltt, ws):
    ws.write("proj/a-very-long-file-name-indeed.txt", "x" * 2000)
    app = ltt(ws.path("proj"), cols=42, rows=10)  # the smallest terminal ltt draws in
    s = app.snap()
    row = s.tree[1]
    assert s.left_w == 22 and s.lines[row.y][s.left_w] == "│"
    assert row.name == "a-very-lon" and row.size == "2K"  # the name is cut, the size stays


def test_shrinking_the_terminal_keeps_the_selection_in_view(ltt, ws):
    app = ltt(long_tree(ws))
    select(app, "f20.txt")
    app.resize(100, 12)
    s = app.wait(lambda s: s.tree_selected is not None, what="the selection in view")
    assert s.tree_selected.name == "f20.txt" and s.tree_selected.y == s.tree[-1].y


# ---------------------------------------------------------------- git

def test_git_ignored_entries_are_dimmed_and_listed_last(ltt, ws):
    ws.repo("repo", files={"src/main.c": "int main;\n", "a.txt": "aaaa\n"}, ignore="*.log\nbuild/\n")
    ws.write("repo/debug.log", "l" * 500)
    ws.write("repo/build/out.o", "o" * 9)
    s = ltt(ws.path("repo")).snap()
    assert names(s) == ["repo", "src", "a.txt", ".git", "build", "debug.log", ".gitignore"]
    dim = {r.name for r in s.tree if r.fg == "DarkGray"}
    assert dim == {".git", "build", "debug.log", ".gitignore"}
    assert s.tree_row("src").fg == "White" and s.tree_row("a.txt").fg == "Gray"


def test_entries_inside_an_ignored_folder_are_dimmed(ltt, ws):
    ws.repo("repo", files={"a.txt": "a\n"}, ignore="build/\n")
    ws.write("repo/build/out.o", "o")
    ws.write("repo/build/sub/deep.o", "d")
    app = ltt(ws.path("repo"))
    select(app, "build")
    s = app.press("right")
    assert s.tree_row("out.o").fg == "DarkGray" and s.tree_row("sub").fg == "DarkGray"


def test_a_root_inside_a_repository_follows_its_gitignore(ltt, ws):
    ws.repo("repo", files={"src/main.c": "x\n"}, ignore="*.log\n")
    ws.write("repo/src/debug.log", "l")
    app = ltt(ws.path("repo/src"))
    assert app.snap().tree_row("debug.log").fg == "DarkGray"
    ws.write("repo/src/trace.log", "t")  # and after a refresh
    s = app.wait_tree("trace.log")
    assert s.tree_row("trace.log").fg == "DarkGray" and s.tree_row("debug.log").fg == "DarkGray"


def test_a_symlinked_folder_does_not_break_dimming(ltt, ws):
    # W7: git refused the whole batch when one path went through a symlink
    ws.repo("repo", files={"src/main.c": "x\n"}, ignore="*.log\n")
    ws.write("repo/debug.log", "l")
    os.symlink(ws.path("repo/src"), ws.path("repo/linkdir"))
    app = ltt(ws.path("repo"))
    s = app.snap()
    assert s.tree_row("linkdir").is_dir and s.tree_row("debug.log").fg == "DarkGray"
    ws.write("repo/trace.log", "t")  # a refresh asks git about the folder again
    s = app.wait_tree("trace.log")
    assert s.tree_row("trace.log").fg == "DarkGray" and s.tree_row("debug.log").fg == "DarkGray"


def nested(ws):
    """outer repo ignoring *.out; inner/ a repository of its own (not part of outer) ignoring *.tmp."""
    ws.repo("outer", files={"o.txt": "o\n"}, ignore="*.out\n")
    ws.repo("outer/inner", files={"i.txt": "i\n"}, ignore="*.tmp\n")
    ws.write("outer/inner/x.tmp", "t")
    ws.write("outer/inner/y.out", "t")
    return ws.path("outer")


def test_a_nested_repository_dims_by_its_own_gitignore(ltt, ws):
    app = ltt(nested(ws))
    select(app, "inner")
    s = app.press("right")
    assert s.tree_row("x.tmp").fg == "DarkGray"
    assert s.tree_row("y.out").fg != "DarkGray"  # the outer repository's rules stop at the inner one
    assert [r.fg for r in s.tree if r.name == ".git"] == ["DarkGray", "DarkGray"]


def test_a_nested_repository_keeps_its_dimming_after_a_refresh(ltt, ws):
    app = ltt(nested(ws))
    select(app, "inner")
    app.press("right")
    assert app.snap().tree_row("y.out").fg != "DarkGray"
    ws.write("outer/inner/new.tmp", "n")  # the watcher re-reads inner/
    s = app.wait_tree("new.tmp")
    assert s.tree_row("new.tmp").fg == "DarkGray"
    assert s.tree_row("y.out").fg != "DarkGray"


@pytest.mark.skipif(sys.platform == "win32", reason="a backslash is a separator there")
def test_a_backslash_in_a_folder_name_keeps_its_dimming_after_a_refresh(ltt, ws):
    ws.repo("repo", files={"a\\b/x.txt": "x\n"}, ignore="a/b/*.txt\n")  # one folder named a\b; there is no a/b
    app = ltt(ws.path("repo"))
    select(app, "a\\b")
    s = app.press("right")
    assert s.tree_row("x.txt").fg == "Gray"
    ws.write("repo/a\\b/y.txt", "y")  # the watcher re-reads a\b: one git question for its repository
    app.wait_tree("y.txt")
    s = app.wait(lambda s: s.tree_row("y.txt").fg != "Gray", what="y.txt marked new (or dimmed)")
    assert (s.tree_row("x.txt").fg, s.tree_row("y.txt").fg) == ("Gray", "Green")  # committed and new: no dimming


def test_a_repository_inside_a_plain_folder_dims_its_ignored_files(ltt, ws):
    ws.write("plain/top.txt", "t")
    ws.repo("plain/repo", files={"a.txt": "a\n"}, ignore="*.log\n")
    ws.write("plain/repo/x.log", "l")
    app = ltt(ws.path("plain"))
    select(app, "repo")
    s = app.press("right")
    assert s.tree_row("x.log").fg == "DarkGray" and s.tree_row("a.txt").fg == "Gray"


def test_a_repository_inside_a_plain_folder_keeps_its_dimming_after_a_refresh(ltt, ws):
    ws.write("plain/top.txt", "t")
    ws.repo("plain/repo", files={"a.txt": "a\n"}, ignore="*.log\n")
    ws.write("plain/repo/x.log", "l")
    app = ltt(ws.path("plain"))
    select(app, "repo")
    assert app.press("right").tree_row("x.log").fg == "DarkGray"
    ws.write("plain/repo/new.txt", "n")  # the watcher re-reads repo/
    s = app.wait_tree("new.txt")
    assert s.tree_row("x.log").fg == "DarkGray"


def test_a_dot_git_file_is_dimmed_like_a_dot_git_folder(ltt, ws):
    ws.repo("main", files={"a.txt": "a\n"})
    ws.git("worktree", "add", "-q", ws.path("wt"), "-b", "side", cwd="main")  # wt/.git is a file
    s = ltt(ws.path("wt")).snap()
    assert s.tree_row(".git").fg == "DarkGray" and names(s)[-1] == ".git"


def test_git_init_shows_dot_git_right_away(ltt, ws):
    # V26: .git appeared only after some other change
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    ws.git("init", "-q", cwd="proj")
    s = app.wait_tree(".git")
    assert s.tree_row(".git").fg == "DarkGray" and names(s)[-1] == ".git"


def test_a_root_inside_dot_git_follows_changes(ltt, ws):
    # V26: changes under .git were never applied, even to a root there
    ws.repo("repo", files={"a.txt": "a\n"})
    app = ltt(ws.path("repo/.git"))
    ws.write("repo/.git/newthing", "x")
    app.wait_tree("newthing")


def test_without_git_only_dot_git_and_gitignore_are_dimmed(ltt, ws):
    ws.repo("repo", files={"a.txt": "a\n"}, ignore="*.log\n")
    ws.write("repo/x.log", "l")
    s = ltt(ws.path("repo"), path=ws.bin_without("git")).snap()
    assert {r.name: r.fg for r in s.tree if r.depth == 1} == {
        "a.txt": "Gray", "x.log": "Gray", ".git": "DarkGray", ".gitignore": "DarkGray"}
    assert s.status == "Ready. Enter opens a file."


# ---------------------------------------------------------------- following changes made outside

def test_a_file_created_outside_appears(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    ws.write("proj/new.txt", "n")
    s = app.wait_tree("new.txt")
    assert s.status.startswith("External change: created new.txt")


def test_a_file_deleted_outside_disappears(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/gone.txt", "g")
    app = ltt(ws.path("proj"))
    ws.remove("proj/gone.txt")
    s = app.wait_tree("gone.txt", present=False)
    assert names(s) == ["proj", "a.txt"] and s.status.startswith("External change: ")


def test_a_file_renamed_outside_is_renamed_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/old.txt", "o")
    app = ltt(ws.path("proj"))
    ws.rename("proj/old.txt", "proj/new.txt")
    s = app.wait(lambda s: s.tree_row("new.txt") and not s.tree_row("old.txt"), what="the new name")
    assert names(s) == ["proj", "a.txt", "new.txt"]


def test_a_folder_created_outside_appears(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    ws.write("proj/newdir/inside.txt", "i")
    s = app.wait_tree("newdir")
    assert s.tree_row("newdir").is_dir and not s.tree_row("newdir").expanded


def test_a_folder_deleted_outside_disappears(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/doomed/inside.txt", "i")
    app = ltt(ws.path("proj"))
    select(app, "doomed")
    app.press("right")
    ws.remove("proj/doomed")
    s = app.wait(lambda s: not s.tree_row("doomed") and not s.tree_row("inside.txt"), what="doomed gone")
    assert names(s) == ["proj", "a.txt"]


def test_a_folder_renamed_outside_is_renamed_in_the_tree(ltt, ws):
    ws.write("proj/old/inside.txt", "i")
    app = ltt(ws.path("proj"))
    ws.rename("proj/old", "proj/new")
    s = app.wait(lambda s: s.tree_row("new") and not s.tree_row("old"), what="the new name")
    select(app, "new")
    assert names(app.press("right")) == ["proj", "new", "inside.txt"]


def test_a_file_created_in_an_expanded_folder_appears(ltt, ws):
    ws.write("proj/sub/inner.txt", "i")
    app = ltt(ws.path("proj"))
    select(app, "sub")
    app.press("right")
    ws.write("proj/sub/added.txt", "added")
    s = app.wait_tree("added.txt")
    assert s.tree_row("added.txt").depth == 2 and names(s) == ["proj", "sub", "added.txt", "inner.txt"]


def test_changes_in_a_collapsed_folder_show_when_it_is_expanded(ltt, ws):
    ws.write("proj/sub/old.txt", "old")
    ws.write("proj/sub/renamed-me.txt", "r")
    app = ltt(ws.path("proj"))
    select(app, "sub")
    app.press("right", "left")  # read once, then collapsed
    ws.remove("proj/sub/old.txt")
    ws.rename("proj/sub/renamed-me.txt", "proj/sub/renamed.txt")
    ws.write("proj/sub/new.txt", "n" * 2000)
    app.wait(lambda s: s.tree_row("sub").size == "2K", what="sub counted again")
    assert names(app.press("right")) == ["proj", "sub", "new.txt", "renamed.txt"]


def test_the_selection_stays_on_its_entry_when_rows_move(ltt, ws):
    ws.write("proj/a.txt", "aa")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"))
    select(app, "b.txt")
    ws.write("proj/aaa/x.txt", "x")  # a folder: listed above the files
    s = app.wait_tree("aaa")
    assert s.tree_selected.name == "b.txt" and names(s) == ["proj", "aaa", "a.txt", "b.txt"]


def test_a_root_deleted_outside_empties_and_fills_again(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("other/o.txt", "o")
    app = ltt(ws.path("proj"), ws.path("other"))
    ws.remove("proj")
    s = app.wait_tree("a.txt", present=False)
    assert names(s) == ["proj", "other", "o.txt"]  # the root stays in the panel
    ws.write("proj/back.txt", "b")
    s = app.wait_tree("back.txt")
    assert names(s) == ["proj", "back.txt", "other", "o.txt"]
    ws.write("proj/later.txt", "l")  # and is watched again
    app.wait_tree("later.txt")


def test_a_change_in_a_nested_root_shows_in_both_places(ltt, ws):
    ws.write("proj/sub/s.txt", "s")
    ws.write("proj/a.txt", "aa")
    app = ltt(ws.path("proj"), ws.path("proj/sub"))  # sub is a root of its own as well
    select(app, "sub")
    s = app.press("right")
    assert [(r.name, r.depth) for r in s.tree] == [
        ("proj", 0), ("sub", 1), ("s.txt", 2), ("a.txt", 1), ("sub", 0), ("s.txt", 1)]
    ws.write("proj/sub/new.txt", "n")
    app.wait(lambda s: [r.depth for r in s.tree if r.name == "new.txt"] == [2, 1], what="new.txt in both")


def test_expansion_inside_a_collapsed_folder_survives_a_refresh(ltt, ws):
    # W8: a re-read of a lost the expansion of b inside it
    ws.write("proj/a/b/c.txt", "c")
    ws.write("proj/z.txt", "zz")
    app = ltt(ws.path("proj"))
    select(app, "a")
    app.press("right", "right", "right")  # a expanded, b selected and expanded
    assert app.snap().tree_row("c.txt") is not None
    select(app, "a")
    app.press("left")
    ws.write("proj/a/new.txt", "n" * 50)  # a is read again while collapsed
    app.wait(lambda s: s.tree_row("a").size == "51", what="a counted again")
    s = app.press("right")
    assert names(s) == ["proj", "a", "b", "c.txt", "new.txt", "z.txt"] and s.tree_row("b").expanded


def test_writes_in_git_and_a_collapsed_node_modules_keep_the_tree(ltt, ws):
    # W2: git's own files and a busy node_modules reset the tree and the search
    ws.repo("repo", files={"src/main.c": "int needle;\n", "src/util.c": "void needle2(void);\n",
                           "node_modules/pkg/index.js": "x\n", "z.txt": "z\n"})
    app = ltt(ws.path("repo"))
    select(app, "src")
    app.press("right")
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: len(s.results) == 2, what="two hits")
    app.press("esc")  # to the results; a click puts the focus back in the tree, the results stay
    s = app.click(6, app.snap().tree_row("main.c").y)
    assert s.mode == "TREE" and s.tree_selected.name == "main.c" and len(s.results) == 2
    before = names(s)
    ws.write("repo/node_modules/pkg/new.js", "y\n")
    ws.write("repo/node_modules/pkg/index.js", "changed\n")
    ws.git("status", cwd="repo")
    ws.git("commit", "-q", "--allow-empty", "-m", "empty", cwd="repo")
    ws.write("repo/fence.txt", "f")  # seen after everything above
    s = app.wait_tree("fence.txt")
    assert [n for n in names(s) if n != "fence.txt"] == before
    assert s.tree_selected.name == "main.c" and s.tree_row("src").expanded
    assert not s.tree_row("node_modules").expanded and len(s.results) == 2


def test_a_burst_of_thousands_of_folders(ltt, ws):
    # V26: a burst re-read the whole tree and restarted every watcher
    ws.write("proj/src/a.txt", "a")
    app = ltt(ws.path("proj"))
    select(app, "src")
    app.press("right")
    shutil.copytree(burst_fixture(), ws.path("proj/burst"))  # 6000 folders at once
    s = app.wait_tree("burst", timeout=20)
    assert s.tree_row("src").expanded and s.tree_selected.name == "src"
    app.wait(lambda s: s.tree_row("burst").size == "0", timeout=20, what="burst counted")
    ws.write("proj/after.txt", "x")
    app.wait_tree("after.txt")  # the watchers still work


# ---------------------------------------------------------------- the mouse

def test_a_click_selects_without_opening(ltt, ws):
    app = ltt(basic(ws))
    s = app.click(6, app.snap().tree_row("dir").y)
    assert s.tree_selected.name == "dir" and not s.tree_row("dir").expanded
    s = app.click(6, s.tree_row("small.txt").y)
    assert s.tree_selected.name == "small.txt" and s.tabs == [] and s.mode == "TREE"


def test_a_click_in_the_tree_takes_the_focus_from_the_editor(ltt, ws):
    app = ltt(basic(ws))
    select(app, "small.txt")
    app.press("enter")
    s = app.click(6, app.snap().tree_row("big.txt").y)
    assert s.mode == "TREE" and s.tree_selected.name == "big.txt" and s.tab_names == ["small.txt"]


def test_a_double_click_toggles_a_folder(ltt, ws):
    app = ltt(basic(ws))
    y = app.snap().tree_row("dir").y
    s = app.double_click(6, y)
    assert s.tree_row("dir").expanded and s.mode == "TREE"
    s = app.double_click(6, y)
    assert not s.tree_row("dir").expanded


def test_a_double_click_opens_a_file(ltt, ws):
    app = ltt(basic(ws))
    s = app.double_click(6, app.snap().tree_row("big.txt").y)
    assert s.mode == "EDITOR" and s.tab_names == ["big.txt"]


def test_a_double_click_needs_both_clicks_on_the_same_entry(ltt, ws):
    # W4: rows that moved between the two clicks (sizes settling) opened another entry
    ws.write("proj/dir/a1.txt", "1")
    ws.write("proj/dir/a2.txt", "22")
    for i, name in enumerate("bcdef"):
        ws.write(f"proj/{name}.txt", "x" * (10 - i))
    app = ltt(ws.path("proj"))
    select(app, "dir")
    s = app.press("right")
    y = s.tree_row("c.txt").y
    click = K.mouse(K.LEFT, 6, y, True) + K.mouse(K.LEFT, 6, y, False)
    # one burst: click c.txt, up to dir and collapse it (the rows move up), click the same cell again
    app.write(click + b"\x1b[A" * (y - s.tree_row("dir").y) + b"\x1b[D" + click)
    s = app.settle()
    assert s.tree_selected.name == "e.txt" and s.tree_selected.y == y
    assert s.tabs == [] and s.mode == "TREE"


def test_clicks_on_two_rows_are_not_a_double_click(ltt, ws):
    app = ltt(basic(ws))
    s = app.snap()
    a, b = s.tree_row("big.txt").y, s.tree_row("small.txt").y
    app.write(K.mouse(K.LEFT, 6, a, True) + K.mouse(K.LEFT, 6, a, False)
              + K.mouse(K.LEFT, 6, b, True) + K.mouse(K.LEFT, 6, b, False))
    s = app.settle()
    assert s.tree_selected.name == "small.txt" and s.tabs == []


def test_a_click_below_the_last_entry_changes_nothing(ltt, ws):
    app = ltt(basic(ws))
    select(app, "dir")
    s = app.click(6, 20)
    assert s.tree_selected.name == "dir" and s.mode == "TREE"


def test_right_click_on_a_file_changes_nothing(ltt, ws):
    app = ltt(basic(ws))
    s = app.click(6, app.snap().tree_row("big.txt").y, button=K.RIGHT)
    assert s.tree_selected.name == "proj" and s.status == "Ready. Enter opens a file." and names(s)[0] == "proj"


def test_the_wheel_moves_the_selection_three_rows_per_five_notches(ltt, ws):
    app = ltt(long_tree(ws))
    seen = [app.wheel(6, 5).tree_selected.name for _ in range(5)]
    assert seen == ["proj", "f00.txt", "f00.txt", "f01.txt", "f02.txt"]
    s = app.wheel(6, 5, down=False, notches=5)
    assert s.tree_selected.name == "proj" and s.mode == "TREE"


def test_the_wheel_scrolls_a_long_tree(ltt, ws):
    app = ltt(long_tree(ws))
    s = app.wheel(6, 5, notches=50)  # 30 rows down
    assert s.tree_selected.name == "f29.txt" and s.tree[0].name != "proj"
    assert s.tree_selected.y == s.tree[-1].y
    s = app.wheel(6, 5, down=False, notches=50)
    assert s.tree_selected.name == "proj" and s.tree[0].name == "proj"


def test_the_wheel_over_the_tree_leaves_the_editor_focused(ltt, ws):
    app = ltt(long_tree(ws))
    select(app, "f00.txt")
    app.press("enter")
    s = app.wheel(6, 5, notches=5)
    assert s.mode == "EDITOR" and s.tree_selected.name == "f03.txt"



def test_a_name_like_git_pathspec_magic_leaves_the_dimming_alone(ltt, ws):  # Emacs's vc-git tests: odd names
    ws.repo("repo", files={"a.txt": "a\n"}, ignore="*.log\n")
    ws.write("repo/debug.log", "l")
    ws.write("repo/:!odd.txt", "o")
    ws.write("repo/:!odd.log", "o")  # and such a name is dimmed when it is ignored
    app = ltt(ws.path("repo"))
    s = app.snap()
    assert s.tree_row(":!odd.txt").fg == "Gray" and s.tree_row("debug.log").fg == "DarkGray"
    assert s.tree_row(":!odd.log").fg == "DarkGray"
    ws.write("repo/trace.log", "t")  # a refresh asks git about the whole folder again
    s = app.wait_tree("trace.log")
    assert s.tree_row("trace.log").fg == "DarkGray" and s.tree_row(":!odd.log").fg == "DarkGray"


def test_an_expanded_symlinked_folder_shows_new_files(ltt, ws):  # Emacs's dired tests: links to folders
    ws.write("outside/dir/one.txt", "1")
    ws.mkdir("proj")
    os.symlink(ws.path("outside/dir"), ws.path("proj/linked"))
    app = ltt(ws.path("proj"))
    select(app, "linked")
    assert app.press("right").tree_row("one.txt") is not None
    ws.write("outside/dir/two.txt", "2")
    app.wait_tree("two.txt", timeout=5)
    ws.remove("outside/dir/one.txt")  # and later changes too
    s = app.wait_tree("one.txt", present=False, timeout=5)
    assert s.tree_row("two.txt") is not None and s.tree_row("linked").expanded
