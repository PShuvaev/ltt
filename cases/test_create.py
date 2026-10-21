"""Ctrl+N and Ctrl+Shift+N: the prefilled folder, new files and folders, names, refusals, errors, help."""
import os

import pytest

from prompts_helpers import candidates, create, ghost, prompt_line, select_in_tree

FILE_HELP = "Enter creates the file and opens it · Tab/↑↓ completion · a trailing / creates a folder · Esc cancels"
FOLDER_HELP = "Enter creates the folder · Tab/↑↓ completion · Esc cancels"
NO_MATCHES = "No matches · Enter uses it as-is · Esc cancels · ~ and relative paths are supported"


# ---- the prefilled folder ----

def test_prefill_is_the_selected_folder_written_from_home(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"))
    select_in_tree(app, "sub")
    s = app.press("ctrl+n")
    assert s.mode == "PROMPT" and prompt_line(s) == " New file: ~/proj/sub/"
    assert s.cursor == (len(" New file: ~/proj/sub/"), s.status_y)


def test_prefill_outside_home_is_the_absolute_folder(ltt, ws):
    ws.write("proj/sub/c.txt", "c")
    want = ws.path("proj/sub") + "/"
    app = ltt(ws.path("proj"), cols=len(want) + 40)
    select_in_tree(app, "sub")
    s = app.press("ctrl+n")
    assert prompt_line(s) == " New file: " + want


def test_prefill_with_a_file_selected_is_its_folder(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"))
    select_in_tree(app, "sub")
    app.press("right")  # expand
    select_in_tree(app, "c.txt")
    assert prompt_line(app.press("ctrl+n")) == " New file: ~/proj/sub/"


def test_prefill_from_the_editor_is_the_open_files_folder(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"), ws.path("home/proj/sub/c.txt"))
    app.press("esc", "home", "tab")  # the tree selection moves to the root, the editor keeps focus
    assert app.snap().mode == "EDITOR"
    assert prompt_line(app.press("ctrl+n")) == " New file: ~/proj/sub/"


def test_prefilled_folder_lists_its_entries(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    ws.write("home/proj/sub/inner/d.txt", "d")
    app = ltt(ws.path("home/proj"))
    select_in_tree(app, "sub")
    s = app.press("ctrl+n")
    assert candidates(s) == ["inner/", "c.txt"] and ghost(s) == "inner/"


def test_enter_on_the_prefilled_folder_says_it_exists(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"))
    select_in_tree(app, "sub")
    app.press("ctrl+n")
    s = app.press("enter")
    assert s.status == "Folder already exists: ~/proj/sub" and ws.files("home/proj") == ["sub/c.txt"]


# ---- new files ----

def test_new_file_is_created_and_opened(ltt, ws):
    ws.write("home/proj/a.txt", "a")
    app = ltt(ws.path("home/proj"))
    app.press("ctrl+n")
    app.type("new.txt")
    s = app.press("enter")
    assert ws.read("home/proj/new.txt") == ""
    assert s.status == "File created and opened: ~/proj/new.txt"
    assert s.mode == "EDITOR" and s.tab_names == ["new.txt"] and s.editor_lines == [""]
    assert s.tree_selected.name == "new.txt" and s.title_path == "~/proj/new.txt"


def test_new_file_takes_text_and_saves(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    create(app, "new.txt")
    app.type("hello")
    assert app.press("ctrl+s").status == "Saved: new.txt"
    assert ws.read("proj/new.txt") == "hello"


def test_new_file_from_the_editor_goes_next_to_the_open_file(ltt, ws):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"), ws.path("home/proj/sub/c.txt"))
    app.press("ctrl+n")
    app.type("d.txt")  # after the prefilled folder
    s = app.press("enter")
    assert ws.exists("home/proj/sub/d.txt") and s.status == "File created and opened: ~/proj/sub/d.txt"
    assert s.tab_names == ["c.txt", "d.txt"] and s.active_tab.name == "d.txt" and s.mode == "EDITOR"


def test_relative_name_goes_into_the_selected_folder(ltt, ws):
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"))  # ltt runs in ws.root
    select_in_tree(app, "sub")
    create(app, "rel.txt")
    assert ws.exists("proj/sub/rel.txt") and not ws.exists("proj/rel.txt") and not ws.exists("rel.txt")


def test_missing_parent_folders_are_created(ltt, ws):
    ws.mkdir("home/proj")
    app = ltt(ws.path("home/proj"))
    s = create(app, "x/y/z.txt")
    assert ws.files("home/proj") == ["x/y/z.txt"]
    assert s.status == "File created and opened: ~/proj/x/y/z.txt"
    assert s.tree_row("x").expanded and s.tree_row("y").expanded
    assert (s.tree_selected.name, s.tree_selected.depth) == ("z.txt", 3)


def test_new_file_in_a_collapsed_folder_shows_it_in_the_tree(ltt, ws):
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"))
    assert not app.snap().tree_row("sub").expanded
    s = create(app, "sub/n.txt")
    assert s.tree_row("sub").expanded and s.tree_row("c.txt") is not None
    assert (s.tree_selected.name, s.tree_selected.depth) == ("n.txt", 2)


def test_existing_file_is_opened_not_overwritten(ltt, ws):
    ws.write("home/proj/a.txt", "keep me")
    app = ltt(ws.path("home/proj"))
    s = create(app, "a.txt")
    assert s.status == "The file already existed — opened it: ~/proj/a.txt"
    assert s.editor_lines == ["keep me"] and ws.read("home/proj/a.txt") == "keep me"


def test_existing_open_file_activates_its_tab(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj"), ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.press("alt+2")
    s = create(app, "a.txt")
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "a.txt"


def test_tilde_path_creates_in_home(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    s = create(app, "~/fromhome.txt")
    assert ws.exists("home/fromhome.txt") and s.status == "File created and opened: ~/fromhome.txt"


def test_absolute_path_creates_anywhere(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    s = create(app, ws.path("elsewhere/deep/x.txt"))
    assert ws.exists("elsewhere/deep/x.txt") and s.tab_names == ["x.txt"] and s.mode == "EDITOR"


def test_dot_dot_goes_up_from_the_selected_folder(ltt, ws):
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "sub")
    create(app, "../up.txt")
    assert ws.exists("proj/up.txt")


@pytest.mark.parametrize("name", ["my notes.txt", "заметка.md", "📝 todo.txt", ".env", "a#b%c&(d)'e.txt",
                                  "with:colon.txt"])
def test_odd_names_are_created_as_typed(ltt, ws, name):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    s = create(app, name)
    assert os.listdir(ws.path("proj")) == [name]
    assert s.active_tab.name == name and s.mode == "EDITOR"


def test_spaces_around_the_name_are_dropped(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    create(app, "  spaced.txt  ")
    assert os.listdir(ws.path("proj")) == ["spaced.txt"]


def test_pasted_name_with_a_line_break_creates_the_file(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    app.press("ctrl+n", "ctrl+u")
    s = app.paste("pasted.txt\n")
    assert s.mode == "PROMPT" and prompt_line(s) == " New file: pasted.txt"
    app.press("enter")
    assert os.listdir(ws.path("proj")) == ["pasted.txt"]


# ---- folders ----

def test_trailing_slash_makes_a_folder(ltt, ws):
    ws.write("home/proj/a.txt", "a")
    app = ltt(ws.path("home/proj"))
    s = create(app, "newdir/")
    assert os.path.isdir(ws.path("home/proj/newdir"))
    assert s.status == "Folder created: ~/proj/newdir" and s.mode == "TREE"
    assert s.tree_selected.name == "newdir" and s.tabs == []


def test_ctrl_shift_n_makes_a_folder(ltt, ws):
    ws.write("home/proj/a.txt", "a")
    app = ltt(ws.path("home/proj"))
    s = app.press("ctrl+shift+n")
    assert s.mode == "PROMPT" and prompt_line(s) == " New folder: ~/proj/"
    app.type("made")
    s = app.press("enter")
    assert os.path.isdir(ws.path("home/proj/made"))
    assert s.status == "Folder created: ~/proj/made" and s.mode == "TREE" and s.tree_selected.name == "made"


def test_ctrl_shift_n_makes_nested_folders(ltt, ws):
    ws.mkdir("home/proj")
    app = ltt(ws.path("home/proj"))
    s = create(app, "p/q/r", folder=True)
    assert os.path.isdir(ws.path("home/proj/p/q/r")) and s.status == "Folder created: ~/proj/p/q/r"


def test_ctrl_shift_n_in_kitty_keyboard_mode(ltt, ws):
    ws.mkdir("home/proj")
    app = ltt(ws.path("home/proj"), keyboard="kitty")
    s = app.press("ctrl+shift+n")
    assert prompt_line(s) == " New folder: ~/proj/"


@pytest.mark.parametrize("folder", [False, True], ids=["trailing-slash", "ctrl-shift-n"])
def test_existing_folder_is_reported(ltt, ws, folder):
    ws.write("home/proj/sub/c.txt", "c")
    app = ltt(ws.path("home/proj"))
    s = create(app, "sub" if folder else "sub/", folder=folder)
    assert s.status == "Folder already exists: ~/proj/sub" and ws.files("home/proj") == ["sub/c.txt"]


# ---- refusals and errors ----

def test_folder_name_for_a_new_file_is_refused(ltt, ws):
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"), cols=300)
    s = create(app, "sub")
    assert s.status == "That is a folder, not a file: " + ws.path("proj/sub")
    assert s.mode == "TREE" and s.tabs == []


@pytest.mark.parametrize("folder", [False, True], ids=["trailing-slash", "ctrl-shift-n"])
def test_folder_over_an_existing_file_is_refused(ltt, ws, folder):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"), cols=300)
    s = create(app, "a.txt" if folder else "a.txt/", folder=folder)
    assert s.status == "A file with this name already exists: " + ws.path("proj/a.txt")
    assert ws.read("proj/a.txt") == "a"


def test_file_inside_a_file_is_an_error(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    s = create(app, "a.txt/b.txt")
    assert s.status.startswith("Creation error: ") and s.mode == "TREE" and ws.files("proj") == ["a.txt"]


def test_name_too_long_is_an_error(ltt, ws):
    ws.mkdir("proj")
    app = ltt(ws.path("proj"))
    s = create(app, "n" * 300 + ".txt")
    assert s.status.startswith("Creation error: ") and ws.files("proj") == []


def test_read_only_folder_is_an_error(ltt, ws):
    ws.mkdir("proj/locked")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "locked")
    os.chmod(ws.path("proj/locked"), 0o555)
    try:
        s = create(app, "x.txt")
        assert s.status.startswith("Creation error: ") and s.tabs == []
        assert not ws.exists("proj/locked/x.txt")
    finally:
        os.chmod(ws.path("proj/locked"), 0o755)


# ---- leaving the prompt ----

@pytest.mark.parametrize("typed", ["", "   ", "/"], ids=["empty", "spaces", "slash"])
def test_nothing_to_create_cancels(ltt, ws, typed):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press("ctrl+n", "ctrl+u")
    if typed:
        app.type(typed)
    s = app.press("enter")
    assert s.status == "Creation cancelled." and ws.files("proj") == ["a.txt"]


def test_esc_cancels(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press("ctrl+n")
    app.type("new.txt")
    s = app.press("esc")
    assert (s.mode, s.status) == ("TREE", "Cancelled.") and ws.files("proj") == ["a.txt"]


def test_esc_returns_to_the_editor_when_started_there(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+n")
    s = app.press("esc")
    assert (s.mode, s.status) == ("EDITOR", "Cancelled.")


def test_ctrl_c_cancels(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press("ctrl+n")
    app.type("new.txt")
    s = app.press("ctrl+c")
    assert s.status == "Cancelled (Ctrl+C)." and ws.files("proj") == ["a.txt"]


# ---- completion, help, long input ----

def test_tab_completes_a_folder_in_the_new_file_prompt(ltt, ws):
    ws.write("home/proj/subfolder/c.txt", "c")
    app = ltt(ws.path("home/proj"))
    app.press("ctrl+n")
    app.type("s")
    s = app.press("tab")
    assert prompt_line(s) == " New file: ~/proj/subfolder/"
    app.type("new.txt")
    app.press("enter")
    assert ws.exists("home/proj/subfolder/new.txt")


def test_help_line_of_the_new_file_prompt(ltt, ws):
    ws.mkdir("proj/empty")
    app = ltt(ws.path("proj/empty"), cols=120)
    s = app.press("ctrl+n", "ctrl+u")  # an empty folder: no candidates to list
    assert prompt_line(s) == " New file:" and s.help == FILE_HELP


def test_help_line_of_the_new_folder_prompt(ltt, ws):
    ws.mkdir("proj/empty")
    app = ltt(ws.path("proj/empty"))
    s = app.press("ctrl+shift+n", "ctrl+u")
    assert prompt_line(s) == " New folder:" and s.help == FOLDER_HELP


def test_help_line_without_matches(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj"))
    app.press("ctrl+n")
    s = app.type("zzz")
    assert s.help == NO_MATCHES


def test_long_prefill_keeps_the_typed_name_in_view(ltt, ws):
    # U5: the prefilled folder pushed the typed name out of an 80-column prompt
    deep = "proj/src/components/forms/very/deep/folder/structure"
    ws.write(deep + "/a.txt", "a")
    app = ltt(ws.path(deep + "/a.txt"), cols=80, rows=16)
    app.press("ctrl+n")
    s = app.type("LoginForm.test.tsx")
    line = prompt_line(s)
    assert line.startswith(" New file: …") and line.endswith("/folder/structure/LoginForm.test.tsx")
    assert s.cursor == (79, s.status_y)
    app.press("enter")
    assert ws.exists(deep + "/LoginForm.test.tsx")
