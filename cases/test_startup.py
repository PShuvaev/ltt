"""Starting ltt: the command line, the first screen, the title line, the status and help lines."""
import os
import subprocess

import pytest

from startup_helpers import ENTER, fresh_termios, run_plain

TREE_HELP = ("↑↓ select · Enter opens · Ctrl+E next change · Ctrl+N new file · Ctrl+Shift+N folder · Ctrl+R rename · "
             "Ctrl+C/X/V copy/move · Ctrl+Del delete · Ctrl+F search here · Ctrl+F+F all folders · "
             "Ctrl+O add root · Ctrl+L go to")
EDITOR_HELP = ("Esc to tree · Ctrl+E next change · Ctrl+F find in file · Ctrl+F+F find everywhere · Ctrl+L go to line · "
               "Ctrl+R rename · Shift+arrows select · Ctrl+A all · Ctrl+K delete line · Ctrl+C/X/V clipboard · Ctrl+S save · "
               "Ctrl+W close tab · Ctrl+P pin tab")
RESULTS_HELP = "↑↓ PgUp/PgDn or wheel to pick · Enter/double-click opens above · Esc hides the panel"
WIDE = 280  # columns that fit the longest help line


def roots(s):
    return [r.name for r in s.tree if r.depth == 0 and r.is_dir]


def wide_for(path):
    """Columns that show this path in the title in full."""
    return max(100, len(path) + 40)


def at_home(ws, rel=""):
    return os.path.join(ws.home, rel)


# ---------------------------------------------------------------- command line

def test_no_arguments_make_the_working_folder_the_root(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/sub/b.txt", "beta\n")
    app = ltt(cwd=ws.path("proj"))
    s = app.snap()
    assert s.tree_names == ["proj", "sub", "a.txt"]
    assert s.mode == "TREE" and s.tabs == []


def test_folders_become_roots_in_argument_order(ltt, ws):
    ws.write("zeta/z.txt", "z\n")
    ws.write("alpha/a.txt", "a\n")
    app = ltt(ws.path("zeta"), ws.path("alpha"))
    s = app.snap()
    assert roots(s) == ["zeta", "alpha"]
    assert s.tree_selected.name == "zeta" and s.mode == "TREE"


def test_files_open_as_tabs_and_the_first_stays_active(ltt, ws):
    ws.write("p/a.txt", "alpha\n")
    ws.write("p/b.txt", "beta\n")
    ws.write("p/c.txt", "gamma\n")
    app = ltt(ws.path("p/a.txt"), ws.path("p/b.txt"), ws.path("p/c.txt"))
    s = app.snap()
    assert s.tab_names == ["a.txt", "b.txt", "c.txt"]
    assert s.active_tab.name == "a.txt" and s.editor_lines[0] == "alpha"
    assert s.mode == "EDITOR" and s.pos[:3] == (1, 2, 1)
    assert s.tree_selected.name == "a.txt"


def test_folder_and_file_arguments_together(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    ws.write("notes/n.txt", "note\n")
    app = ltt(ws.path("notes/n.txt"), ws.path("proj"))
    s = app.snap()
    assert roots(s) == ["proj", "notes"]  # folders first, then the folders of files
    assert s.tab_names == ["n.txt"] and s.mode == "EDITOR"


def test_the_same_file_twice_opens_one_tab(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), ws.path("a.txt"))
    assert app.snap().tab_names == ["a.txt"]


def test_file_line_argument_puts_the_cursor_on_that_line(ltt, ws):
    ws.write("big.txt", "".join(f"line {i}\n" for i in range(1, 201)))
    app = ltt(ws.path("big.txt") + ":150")
    s = app.snap()
    assert s.pos[:3] == (150, 201, 1)
    row = s.editor_row(150)
    assert row is not None and row.current and row.text == "line 150"


def test_file_line_col_argument_opens_at_the_line(ltt, ws):
    # As compilers print it; the command line takes the line (its usage reads file[:line]).
    ws.write("b.txt", "one\ntwo\nthree\n")
    app = ltt(ws.path("b.txt") + ":2:3")
    s = app.snap()
    assert s.tab_names == ["b.txt"] and s.pos[0] == 2


@pytest.mark.parametrize("suffix, line", [(":999", 4), (":0", 1)])
def test_file_line_out_of_range_is_clamped(ltt, ws, suffix, line):
    ws.write("b.txt", "one\ntwo\nthree\n")  # four lines: the last one is empty
    app = ltt(ws.path("b.txt") + suffix)
    assert app.snap().pos[:2] == (line, 4)


def test_existing_name_with_a_colon_is_a_file_not_a_location(ltt, ws):
    ws.write("notes:2", "first\nsecond\n")
    app = ltt(ws.path("notes:2"))
    s = app.snap()
    assert s.tab_names == ["notes:2"] and s.pos[0] == 1


def test_file_named_like_an_option_opens_by_its_path(ltt, ws):
    ws.write("-dash.txt", "dash\n")
    app = ltt("./-dash.txt")
    s = app.snap()
    assert s.tab_names == ["-dash.txt"] and s.editor_lines[0] == "dash"


@pytest.mark.parametrize("order", ["folder first", "file first"])
def test_file_inside_a_root_folder_adds_no_root(ltt, ws, order):  # W5
    ws.write("proj/a.txt", "alpha\n")
    ws.write("proj/sub/b.txt", "beta\n")
    args = [ws.path("proj"), ws.path("proj/sub/b.txt")]
    app = ltt(*(args if order == "folder first" else args[::-1]))
    s = app.snap()
    assert roots(s) == ["proj"]
    assert s.tab_names == ["b.txt"]
    sel = s.tree_selected
    assert (sel.name, sel.depth) == ("b.txt", 2)  # sub/ expanded down to the file


def test_file_outside_the_roots_adds_its_folder(ltt, ws):  # W5
    ws.write("proj/a.txt", "alpha\n")
    ws.write("other/c.txt", "gamma\n")
    app = ltt(ws.path("proj"), ws.path("other/c.txt"))
    assert roots(app.snap()) == ["proj", "other"]


def test_two_files_of_one_folder_add_one_root(ltt, ws):
    ws.write("x/a.txt", "alpha\n")
    ws.write("x/b.txt", "beta\n")
    app = ltt(ws.path("x/a.txt"), ws.path("x/b.txt"))
    s = app.snap()
    assert roots(s) == ["x"] and s.tab_names == ["a.txt", "b.txt"]


@pytest.mark.parametrize("second", ["proj", "proj/", "proj/.", "proj/../proj"])
def test_the_same_root_counts_once(ltt, ws, second):  # W5
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"), ws.path(second))
    s = app.snap()
    assert roots(s) == ["proj"] and s.tree_names == ["proj", "a.txt"]


def test_root_with_a_trailing_slash_shows_its_name(ltt, ws):  # W5
    ws.write("proj/a.txt", "alpha\n")
    path = ws.path("proj")
    app = ltt(path + "/", cols=wide_for(path))
    s = app.snap()
    assert roots(s) == ["proj"]
    assert s.title_path == path  # no trailing slash


def test_relative_arguments_resolve_against_the_working_folder(ltt, ws):
    ws.write("work/sub/s.txt", "s\n")
    ws.write("notes/n.txt", "one\ntwo\n")
    app = ltt("sub", "../notes/n.txt:2", cwd=ws.path("work"))
    s = app.snap()
    assert roots(s) == ["sub", "notes"]
    assert s.tab_names == ["n.txt"] and s.pos[0] == 2


# ---------------------------------------------------------------- refusals (exit code 2)

@pytest.mark.parametrize("arg", ["nope.txt", "nope.txt:3", "nope/"])
def test_missing_path_is_refused(ltt, ws, arg):
    app = ltt(arg, ready=False)
    assert app.wait_exit() == 2
    assert app.output_text.strip().endswith(f"Path not found: {arg}")
    assert ENTER not in app.output and app.frames == 0  # the terminal was never taken over
    assert app.tty_settings() == fresh_termios()


@pytest.mark.parametrize("args", [["-x"], ["--help"], ["-"], ["a.txt", "-v"]])
def test_unknown_option_prints_the_usage(ltt, ws, args):
    ws.write("a.txt", "alpha\n")
    app = ltt(*args, ready=False)
    assert app.wait_exit() == 2
    bad = [a for a in args if a.startswith("-")][0]
    assert app.output_text.strip().endswith(f"Unknown option: {bad}. Usage: ltt [folder…] [file[:line]…]")
    assert ENTER not in app.output and app.frames == 0


@pytest.mark.parametrize("arg", ["nope.txt", "-x"])
def test_argument_error_leaves_cursor_keys_and_keypad_normal(ltt, ws, arg):
    app = ltt(arg, ready=False)
    assert app.wait_exit() == 2
    out = app.output
    if b"\x1b[?1h" in out:
        assert out.rfind(b"\x1b[?1l") > out.rfind(b"\x1b[?1h")
    if b"\x1b=" in out:
        assert out.rfind(b"\x1b>") > out.rfind(b"\x1b=")


def test_redirected_input_needs_a_terminal(tools, ws):
    ws.write("a.txt", "alpha\n")
    master, slave = os.openpty()
    try:
        r = run_plain(tools, ws, ["a.txt"], stdin=subprocess.DEVNULL, stdout=slave)
    finally:
        os.close(master)
        os.close(slave)
    assert r.returncode == 2
    assert r.stderr == b"The editor needs an interactive terminal.\n"


def test_redirected_output_needs_a_terminal(tools, ws):
    ws.write("a.txt", "alpha\n")
    master, slave = os.openpty()
    try:
        r = run_plain(tools, ws, ["a.txt"], stdin=slave, stdout=subprocess.PIPE)
    finally:
        os.close(master)
        os.close(slave)
    assert r.returncode == 2 and r.stdout == b""
    assert r.stderr == b"The editor needs an interactive terminal.\n"


def test_arguments_are_checked_before_the_terminal(tools, ws):
    r = run_plain(tools, ws, ["nope.txt"], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE)
    assert r.returncode == 2 and r.stderr == b"Path not found: nope.txt\n"


# ---------------------------------------------------------------- the first screen

def test_folder_start_shows_ready_and_the_tree_help(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"), cols=WIDE)
    s = app.snap()
    assert s.status_line.startswith(" [TREE] Ready. Enter opens a file.")
    assert s.pos is None  # no position without an open file
    assert s.help == TREE_HELP


def test_no_file_shows_the_editor_hint(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"))
    s = app.snap()
    assert s.text(1, s.editor_x).strip() == "(no file)"
    assert [s.text(y, s.editor_x).strip() for y in range(2, 7)] == [
        "Pick a file on the left and press Enter.",
        "Ctrl+F — search here; Ctrl+F+F — search all folders.",
        "",
        "←/→ collapse and expand folders,",
        "Tab switches panels, Ctrl+Q quits.",
    ]


def test_file_start_names_the_tab_and_shows_the_editor_help(ltt, ws):
    ws.write("a.txt", "alpha\nbeta\n")
    app = ltt(ws.path("a.txt"), cols=WIDE)
    s = app.snap()
    assert s.mode == "EDITOR" and s.status == "Tab: a.txt"
    assert s.status_line.rstrip().endswith("Ln 1/3, Col 1")
    assert s.help == EDITOR_HELP


def test_status_help_and_title_colours(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    s = app.snap()
    assert (s.fg(1, 0), s.bg(1, 0)) == ("White", "DarkBlue")
    assert (s.fg(2, s.status_y), s.bg(2, s.status_y)) == ("Black", "Gray")
    assert (s.fg(2, s.help_y), s.bg(2, s.help_y)) == ("DarkGray", "Black")


# ---------------------------------------------------------------- status and help per mode

def test_tab_without_an_open_file_asks_to_open_one(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"))
    s = app.press("tab")
    assert s.mode == "TREE" and s.status == "Open a file with Enter first."


def test_tab_and_esc_switch_panels_with_their_status(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=WIDE)
    s = app.press("esc")
    assert s.mode == "TREE"
    assert s.status == "File panel. Enter opens; Ctrl+F searches the selection, Ctrl+F+F all folders."
    assert s.help == TREE_HELP
    assert s.pos[:3] == (1, 2, 1)  # the open file's position stays in the status line
    s = app.press("tab")
    assert s.mode == "EDITOR" and s.status == "Editing. Esc returns to the tree."
    assert s.help == EDITOR_HELP


def test_opening_from_the_tree_reports_lines_and_tab(ltt, ws):
    ws.write("proj/b.txt", "one\ntwo\n")
    app = ltt(ws.path("proj"))
    s = app.press("down", "enter")
    assert s.mode == "EDITOR"
    assert s.status == "Opened b.txt (3 lines), tab 1/1. Ctrl+W closes."


def test_position_stays_whole_when_the_status_message_is_cut(ltt, ws):
    ws.write("proj/b.txt", "one\ntwo\n")
    app = ltt(ws.path("proj"), cols=42, rows=10)
    s = app.press("down", "enter")
    message, position = " [EDITOR] Opened b.txt (3 lines), tab 1/1. Ctrl+W closes.", " Ln 1/3, Col 1 "
    assert s.status_line == message[:42 - len(position)] + position


def test_selection_size_shows_in_the_status_line(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"))
    s = app.press("shift+right", "shift+right", "shift+right")
    assert s.status_line.rstrip().endswith("Ln 1/2, Col 4 · selected: 3")
    assert s.pos == (1, 2, 4, 3)


@pytest.mark.parametrize("from_tree, scope_help", [
    (False, "Searching this file live · Ctrl+F again widens to all folders · ↑↓ pick · Enter jumps · "
            "Esc to results"),
    (True, "Searching this folder live · Ctrl+F again widens to all folders · ↑↓ pick · Enter opens · "
           "Esc to results"),
], ids=["from the editor", "from the tree"])
def test_search_input_help_names_the_scope(ltt, ws, from_tree, scope_help):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), cols=WIDE)
    if from_tree:
        app.press("esc", "home")  # the root folder selected
    s = app.press("ctrl+f")
    assert s.mode == "PROMPT" and s.help == scope_help
    s = app.press("ctrl+f")
    assert s.help == ("Searching all folders live · Ctrl+F again narrows to the selection · ↑↓ pick · "
                      "Enter opens · Esc to results")


def test_results_mode_status_and_help(ltt, ws):
    ws.write("proj/a.txt", "needle one\nneedle two\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), cols=WIDE)
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: len(s.results) == 2, what="two hits")
    s = app.press("esc")
    assert s.mode == "SEARCH"
    assert s.status == "Found: 2. Enter opens above, Esc hides the panel."
    assert s.help == RESULTS_HELP


def test_results_panel_left_open_adds_the_next_hit_hint(ltt, ws):
    ws.write("proj/a.txt", "needle one\nneedle two\n")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), cols=WIDE)
    app.press("ctrl+f")
    app.type("needle")
    app.wait(lambda s: len(s.results) == 2, what="two hits")
    s = app.press("enter")  # opens the hit, the panel stays
    assert s.mode == "EDITOR" and s.results
    assert s.help == EDITOR_HELP + " · Ctrl+G — next hit"


def test_question_clears_the_help_line(ltt, ws):
    ws.write("a.txt", "alpha\n")
    app = ltt(ws.path("a.txt"), cols=WIDE)
    app.type("X")
    s = app.press("ctrl+q")
    assert s.mode == "CONFIRM"
    assert s.question == "Unsaved tabs: 1!  S saves all · D quits without saving · Esc cancels"
    assert (s.fg(1, s.status_y), s.bg(1, s.status_y)) == ("White", "DarkRed")
    assert s.help == ""
    s = app.press("esc")
    assert s.status == "Cancelled." and s.help == EDITOR_HELP


# ---------------------------------------------------------------- the title line

def test_title_shows_paths_under_home_with_a_tilde(ltt, ws):
    ws.write(at_home(ws, "proj/a.txt"), "alpha\n")
    app = ltt(at_home(ws, "proj/a.txt"))
    assert app.snap().title == "ltt · ~/proj/a.txt"


def test_title_of_the_home_folder_itself_is_a_tilde(ltt, ws):
    ws.write(at_home(ws, "a.txt"), "alpha\n")
    app = ltt(ws.home)
    assert app.snap().title == "ltt · ~"


def test_title_keeps_a_home_lookalike_folder_whole(ltt, ws):
    path = ws.write(ws.home + "work/c.txt", "gamma\n")  # starts like HOME, but is not inside it
    app = ltt(path, cols=wide_for(path))
    assert app.snap().title == "ltt · " + path


def test_long_title_loses_its_beginning_not_the_file_name(ltt, ws):  # U4
    rel = "/".join(f"level{i:02d}" for i in range(12)) + "/deep-file-name.txt"
    ws.write(at_home(ws, rel), "deep\n")
    ws.write(at_home(ws, "b.txt"), "b\n")
    app = ltt(at_home(ws, rel), at_home(ws, "b.txt"), cols=60)
    s = app.snap()
    assert s.lines[0].startswith(" ltt · …")
    assert s.title.endswith("/level11/deep-file-name.txt [tab 1/2]")
    app.type("X")
    s = app.wait(lambda s: s.title_dirty, what="dirty title")
    assert s.title.endswith("/deep-file-name.txt ● [tab 1/2]")


def test_title_marks_unsaved_edits(ltt, ws):
    ws.write(at_home(ws, "a.txt"), "alpha\n")
    app = ltt(at_home(ws, "a.txt"))
    app.type("X")
    s = app.snap()
    assert s.title == "ltt · ~/a.txt ●"
    app.press("ctrl+s")
    s = app.wait_status("Saved: a.txt")
    assert s.title == "ltt · ~/a.txt"


def test_title_counts_tabs_only_when_there_are_several(ltt, ws):
    ws.write(at_home(ws, "a.txt"), "alpha\n")
    ws.write(at_home(ws, "b.txt"), "beta\n")
    app = ltt(at_home(ws, "a.txt"), at_home(ws, "b.txt"))
    assert app.snap().title == "ltt · ~/a.txt [tab 1/2]"
    assert app.press("ctrl+pgdn").title == "ltt · ~/b.txt [tab 2/2]"
    assert app.press("ctrl+w").title == "ltt · ~/a.txt"


def test_title_in_the_tree_shows_the_selected_entry(ltt, ws):
    ws.write(at_home(ws, "proj/a.txt"), "alpha\n")
    app = ltt(at_home(ws, "proj"), at_home(ws, "proj/a.txt"))
    app.type("X")  # an unsaved tab: the tree's title shows neither ● nor [tab]
    s = app.press("esc")
    assert s.title == "ltt · ~/proj/a.txt"
    assert app.press("up").title == "ltt · ~/proj"


def test_title_without_an_open_file_shows_the_tree_selection(ltt, ws):
    ws.write(at_home(ws, "proj/a.txt"), "alpha\n")
    app = ltt(at_home(ws, "proj"))
    assert app.snap().title == "ltt · ~/proj"
    assert app.press("down").title == "ltt · ~/proj/a.txt"  # selected, not open


def test_title_in_search_results_shows_the_hit_file(ltt, ws):
    ws.write(at_home(ws, "proj/x.txt"), "needle in x\n")
    ws.write(at_home(ws, "proj/y.txt"), "needle in y\n")
    app = ltt(at_home(ws, "proj"))
    app.press("ctrl+f", "ctrl+f")
    app.type("needle")
    app.wait(lambda s: len(s.results) == 2, what="two hits")
    s = app.press("esc")
    first = s.results[0].path
    assert s.mode == "SEARCH" and s.title == f"ltt · ~/proj/{first}"
    second = s.results[1].path
    assert app.press("down").title == f"ltt · ~/proj/{second}"



def test_the_error_of_a_later_file_on_the_command_line_is_shown(ltt, ws):  # Textadept's args_test.lua
    a = ws.write("a.txt", "alpha\n")
    b = ws.write("bin.dat", b"\x00\x01binary\n")
    app = ltt(a, b)
    s = app.snap()
    assert s.tab_names == ["a.txt"] and "The file looks binary" in s.status
