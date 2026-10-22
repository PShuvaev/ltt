"""Ctrl+L: a line number, or file[:line[:col]] as compilers and agents print it — paths, completion, help, keys."""
import pytest

from harness import keys as K
from prompts_helpers import candidates, chosen, ghost, goto, prompt_line, select_in_tree

FIFTY = "\n".join(f"line {i}" for i in range(1, 51))  # 50 lines, no final newline
GOTO_HELP = "Enter jumps: 120 — a line, file.cs:120 — a file at a line · Esc cancels"
NO_MATCHES = "No matches · Enter uses it as-is · Esc cancels · ~ and relative paths are supported"


# ---- a line number in the open file ----

def test_line_number_jumps_to_the_line(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "30")
    assert (s.mode, s.status, s.pos[:3]) == ("EDITOR", "Line 30 of 50.", (30, 50, 1))
    assert s.editor_row(30).current


@pytest.mark.parametrize("typed, line", [("999", 50), ("0", 1), ("-5", 1), ("+7", 7), (" 7 ", 7)])
def test_line_number_is_clamped_and_trimmed(ltt, ws, typed, line):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    goto(app, "20")
    s = goto(app, typed)
    assert s.status == f"Line {line} of 50." and s.pos[0] == line


def test_line_number_from_the_tree_moves_to_the_editor(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    assert app.press("esc").mode == "TREE"
    s = goto(app, "12")
    assert s.mode == "EDITOR" and s.pos[0] == 12


def test_line_number_without_an_open_file(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj"))
    s = goto(app, "5")
    assert s.status == "Open a file first, or type a path." and s.mode == "TREE" and s.tabs == []


def test_huge_line_number_goes_to_the_last_line(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "99999999999")
    assert s.pos[0] == 50


# ---- leaving the prompt ----

def test_enter_on_empty_input_cancels(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.press("enter")
    assert (s.mode, s.status, s.pos[0]) == ("EDITOR", "Go to cancelled.", 1)


def test_esc_cancels_and_the_cursor_stays(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("30")
    s = app.press("esc")
    assert (s.mode, s.status, s.pos[0]) == ("EDITOR", "Cancelled.", 1)


def test_ctrl_c_cancels(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("30")
    s = app.press("ctrl+c")
    assert (s.mode, s.status, s.pos[0]) == ("EDITOR", "Cancelled (Ctrl+C).", 1)


# ---- file, file:line, file:line:col ----

def test_file_colon_line_opens_the_file_there(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2\nb3\nb4")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt:3")
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "b.txt"
    assert s.status == "Opened b.txt (4 lines), tab 2/2. Ctrl+W closes."
    assert s.mode == "EDITOR" and s.pos[:3] == (3, 4, 1)
    assert s.title_path.endswith("/proj/b.txt") and s.title.endswith("[tab 2/2]")


def test_file_line_and_column(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2 0123456789\nb3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt:2:5")
    assert s.active_tab.name == "b.txt" and s.pos[:3] == (2, 3, 5)


def test_bare_file_name_opens_at_the_first_line(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2\nb3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt")
    assert s.active_tab.name == "b.txt" and s.pos[:3] == (1, 3, 1)


def test_line_past_the_end_of_a_file_goes_to_its_last_line(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2\nb3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt:99")
    assert s.active_tab.name == "b.txt" and s.pos[:2] == (3, 3)


def test_column_past_the_line_end_stops_at_its_end(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2 0123456789\nb3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt:2:99")
    assert s.pos[:3] == (2, 3, len("b2 0123456789") + 1)


def test_line_zero_in_a_location_means_the_first_line(ltt, ws):
    ws.write("proj/b.txt", "b1\nb2\nb3")
    app = ltt(ws.path("proj/b.txt"))
    goto(app, "3")
    s = goto(app, "b.txt:0")
    assert s.pos[0] == 1 and s.status == "Line 1."


def test_location_in_an_open_file_activates_its_tab(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.press("alt+2")
    s = goto(app, "a.txt:40")
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "a.txt"
    assert s.status == "Line 40." and s.pos[0] == 40


def test_location_past_the_end_of_an_open_file_reports_the_line_reached(ltt, ws):
    ws.write("proj/b.txt", "b1\nb2\nb3")
    app = ltt(ws.path("proj/b.txt"))
    s = goto(app, "b.txt:99")
    assert s.pos[0] == 3
    assert "99" not in s.status


def test_opened_file_is_selected_in_the_tree(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/c.txt", "c1\nc2")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "sub/c.txt:2")
    sel = s.tree_selected
    assert (sel.name, sel.depth, sel.bg) == ("c.txt", 2, "DarkGray")  # dim: the editor has focus
    assert s.tree_row("sub").expanded


# ---- where a typed path starts ----

def test_absolute_path(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("elsewhere/c.txt", "c1\nc2")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, ws.path("elsewhere/c.txt") + ":2")
    assert s.active_tab.name == "c.txt" and s.pos[0] == 2


def test_tilde_path_starts_from_home(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("home/notes/n.txt", "n1\nn2\nn3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "~/notes/n.txt:3")
    assert s.active_tab.name == "n.txt" and s.pos[0] == 3 and s.title_path == "~/notes/n.txt"


def test_relative_path_starts_from_the_open_files_folder(ltt, ws):
    ws.write("proj/b.txt", "root b")
    ws.write("proj/sub/b.txt", "sub b")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"), ws.path("proj/sub/c.txt"))
    s = goto(app, "b.txt")
    assert s.editor_lines == ["sub b"] and s.title_path.endswith("/proj/sub/b.txt")


def test_relative_path_starts_from_the_folder_selected_in_the_tree(ltt, ws):
    ws.write("proj/b.txt", "root b")
    ws.write("proj/sub/b.txt", "sub b")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "sub")
    s = goto(app, "b.txt")
    assert s.editor_lines == ["sub b"]


def test_relative_path_starts_from_the_folder_of_the_file_selected_in_the_tree(ltt, ws):
    ws.write("proj/b.txt", "root b")
    ws.write("proj/sub/b.txt", "sub b")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"))
    select_in_tree(app, "sub")
    app.press("right")  # expand
    select_in_tree(app, "c.txt")
    s = goto(app, "b.txt")
    assert s.editor_lines == ["sub b"]


def test_path_relative_to_the_root_is_found(ltt, ws):
    # agents print paths relative to the project
    ws.write("proj/sub/deep/x.txt", "x")
    ws.write("proj/other/z.txt", "z1\nz2\nz3")
    app = ltt(ws.path("proj"), ws.path("proj/sub/deep/x.txt"))
    s = goto(app, "other/z.txt:2")
    assert s.active_tab.name == "z.txt" and s.pos[0] == 2


def test_path_relative_to_another_root_is_found(ltt, ws):
    ws.write("app/main.txt", "m")
    ws.write("lib/src/util.txt", "u1\nu2")
    app = ltt(ws.path("app"), ws.path("lib"), ws.path("app/main.txt"))
    s = goto(app, "src/util.txt:2")
    assert s.active_tab.name == "util.txt" and s.pos[0] == 2


def test_relative_path_never_starts_from_the_working_directory(ltt, ws):
    ws.write("cwd.txt", "in the working directory")  # ltt runs in ws.root
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "cwd.txt")
    assert s.status == "File not found: cwd.txt" and s.tab_names == ["a.txt"]


def test_dot_dot_and_spaces_in_a_path(ltt, ws):
    ws.write("proj/my notes.txt", "m1\nm2")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"), ws.path("proj/sub/c.txt"))
    s = goto(app, "../my notes.txt:2")
    assert s.active_tab.name == "my notes.txt" and s.pos[0] == 2


# ---- what is not opened ----

def test_missing_file_is_reported(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "nope.txt:3")
    assert s.status == "File not found: nope.txt" and s.tab_names == ["a.txt"] and s.mode == "EDITOR"


def test_folder_is_not_opened(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "sub")
    assert s.status == "File not found: sub" and s.tab_names == ["a.txt"]


def test_text_after_a_colon_is_part_of_the_name(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "b.txt:abc")
    assert s.status == "File not found: b.txt:abc" and s.tab_names == ["a.txt"]


def test_name_with_a_colon_opens_by_its_full_path(ltt, ws):
    # a path that exists as typed wins over reading `:5` as a line
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/odd:5", "odd")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, ws.path("proj/sub/odd:5"))
    assert s.active_tab.name == "odd:5" and s.editor_lines == ["odd"]


def test_colon_inside_a_folder_name_stays_in_the_path(ltt, ws):
    # only trailing numbers are a line and a column
    ws.write("proj/a.txt", "a")
    ws.write("proj/v1:beta/notes.txt", "n1\nn2\nn3")
    app = ltt(ws.path("proj/a.txt"))
    s = goto(app, "v1:beta/notes.txt:3")
    assert s.active_tab.name == "notes.txt" and s.pos[0] == 3


def test_relative_name_with_a_colon_opens_that_file(ltt, ws):
    ws.write("proj/sub/odd:5", "odd")
    ws.write("proj/sub/c.txt", "c")
    app = ltt(ws.path("proj"), ws.path("proj/sub/c.txt"))
    s = goto(app, "odd:5")
    assert s.active_tab.name == "odd:5"


# ---- completion ----

def test_typing_shows_a_grey_suggestion_after_the_cursor(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("be")
    assert prompt_line(s) == " Go to: be" and ghost(s) == "ta.txt"
    assert s.cursor == (len(" Go to: be"), s.status_y) and not s.cursor_hidden
    x = s.cursor[0]
    assert (s.fg(x, s.status_y), s.bg(x, s.status_y)) == ("DarkGray", "DarkBlue")


def test_tab_cycles_through_the_candidates(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    ws.write("proj/bravo.txt", "v")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("b")
    assert candidates(s) == ["beta.txt", "bravo.txt"] and chosen(s) == ""
    s = app.press("tab")
    assert prompt_line(s) == " Go to: beta.txt" and chosen(s) == "beta.txt" and ghost(s) == ""
    s = app.press("tab")
    assert prompt_line(s) == " Go to: bravo.txt" and chosen(s) == "bravo.txt"
    s = app.press("tab")
    assert prompt_line(s) == " Go to: beta.txt" and chosen(s) == "beta.txt"


def test_chosen_candidate_is_highlighted(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    ws.write("proj/bravo.txt", "v")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("b")
    s = app.press("tab")
    x, y = s.find("beta.txt", rows=[s.help_y])
    assert (s.fg(x, y), s.bg(x, y)) == ("White", "DarkBlue")
    x, y = s.find("bravo.txt", rows=[s.help_y])
    assert (s.fg(x, y), s.bg(x, y)) == ("Gray", "Black")


def test_tab_completes_the_common_prefix_first(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/note-alpha.txt", "1")
    ws.write("proj/note-beta.txt", "2")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("n")
    s = app.press("tab")
    assert prompt_line(s) == " Go to: note-" and chosen(s) == ""
    s = app.press("tab")
    assert prompt_line(s) == " Go to: note-alpha.txt" and chosen(s) == "note-alpha.txt"


def test_tab_with_one_candidate_completes_it(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/unique.txt", "u")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("u")
    s = app.press("tab")
    assert prompt_line(s) == " Go to: unique.txt" and ghost(s) == ""
    assert app.press("enter").active_tab.name == "unique.txt"


def test_tab_into_a_folder_lists_its_entries(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/sub/c.txt", "c")
    ws.write("proj/sub/d.txt", "d")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("s")
    s = app.press("tab")
    assert prompt_line(s) == " Go to: sub/" and candidates(s) == ["c.txt", "d.txt"] and ghost(s) == "c.txt"


def test_down_and_up_cycle_the_candidates(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    ws.write("proj/bravo.txt", "v")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("b")
    assert prompt_line(app.press("down")) == " Go to: beta.txt"
    assert prompt_line(app.press("down")) == " Go to: bravo.txt"
    assert prompt_line(app.press("up")) == " Go to: beta.txt"
    s = app.press("up")  # wraps to the last
    assert prompt_line(s) == " Go to: bravo.txt" and chosen(s) == "bravo.txt"


def test_right_accepts_the_suggestion_spelled_as_on_disk(ltt, ws):
    # P2: → used to keep the typed case (main.cs for Main.cs)
    ws.write("proj/a.txt", "a")
    ws.write("proj/Main.cs", "class Main {}")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("ma")
    assert prompt_line(s) == " Go to: ma" and ghost(s) == "in.cs"
    s = app.press("right")
    assert prompt_line(s) == " Go to: Main.cs" and ghost(s) == ""
    assert app.press("enter").active_tab.name == "Main.cs"


def test_right_without_a_suggestion_does_nothing(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("zz")
    s = app.press("right")
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to: zz"


def test_candidates_list_folders_first(ltt, ws):
    for name in ("dir/b.txt", "dir/A.md", "dir/Zeta/z.txt", "dir/alpha/x.txt"):
        ws.write("proj/" + name, "x")
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("dir/")
    assert candidates(s) == ["alpha/", "Zeta/", "A.md", "b.txt"]


def test_completion_ignores_case(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/readme.md", "r")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("READ")
    assert candidates(s) == ["readme.md"]
    assert prompt_line(app.press("tab")) == " Go to: readme.md"


def test_completion_after_a_tilde(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("home/hdir/x.txt", "x")
    ws.write("home/hnote.txt", "h")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("~/h")
    assert candidates(s) == ["hdir/", "hnote.txt"]
    assert prompt_line(app.press("down")) == " Go to: ~/hdir/"


def test_line_numbers_get_no_completion(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/2024.txt", "a file named like a number")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("20")
    assert ghost(s) == "" and candidates(s) == [] and s.help == NO_MATCHES


# ---- editing the input ----

def test_ctrl_u_clears_the_input(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("b.txt:3")
    s = app.press("ctrl+u")
    assert prompt_line(s) == " Go to:" and s.help == GOTO_HELP


def test_backspace_deletes_the_last_character(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/beta.txt", "b")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("betx")
    s = app.press("backspace")
    assert prompt_line(s) == " Go to: bet" and ghost(s) == "a.txt"


def test_backspace_deletes_a_whole_emoji_or_accented_letter(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    app.type("q📝é")  # e + a combining acute accent
    assert prompt_line(app.press("backspace")) == " Go to: q📝"
    assert prompt_line(app.press("backspace")) == " Go to: q"


def test_paste_ending_in_a_line_break_fills_the_prompt(ltt, ws):
    # T1: a pasted line break used to fire the prompt
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b1\nb2\nb3")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.paste("b.txt:3\n")  # as copied from an agent's output, line break included
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to: b.txt:3"
    s = app.press("enter")
    assert s.active_tab.name == "b.txt" and s.pos[0] == 3


def test_paste_of_several_lines_joins_them(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.paste("one\ntwo\n")
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to: one two"


def test_long_input_keeps_its_end_in_view(ltt, ws):
    # U5: prompts did not scroll sideways
    deep = "proj/" + "/".join(f"folder-{i}" for i in range(8))
    ws.write(deep + "/target-file.txt", "t1\nt2")
    app = ltt(ws.path(deep + "/target-file.txt"), cols=80, rows=16)
    app.press("ctrl+l")
    s = app.paste(ws.path(deep) + "/target-file.txt:2")
    line = prompt_line(s)
    assert line.startswith(" Go to: …") and line.endswith("/folder-7/target-file.txt:2")
    assert s.cursor == (79, s.status_y)  # right after the last character
    assert app.press("enter").pos[0] == 2


# ---- help, screen, other keys ----

def test_help_line_for_an_empty_input(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    s = app.press("ctrl+l")
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to:" and s.help == GOTO_HELP
    assert s.cursor == (len(" Go to: "), s.status_y)


def test_help_line_without_matches(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+l")
    s = app.type("zzz")
    assert s.help == NO_MATCHES


def test_ctrl_l_repaints_the_whole_screen(ltt, ws):
    ws.write("proj/a.txt", "a")
    app = ltt(ws.path("proj/a.txt"))
    mark = app.output_mark()
    app.press("ctrl+l")
    out = app.output_since(mark)
    assert b"\x1b[2J" in out and "ltt · ".encode() in out and b"proj/" in out


def test_mouse_and_shortcuts_wait_while_the_prompt_is_open(ltt, ws):
    ws.write("proj/a.txt", "a")
    ws.write("proj/b.txt", "b")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.press("ctrl+l")
    app.type("b")
    tab_b = app.snap().tabs[1]
    for key in ("ctrl+w", "alt+2", "ctrl+pgdn", "ctrl+s", "ctrl+r", "ctrl+n"):
        app.press(key)
    s = app.click(tab_b.x0 + 2, 1)
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to: b"
    assert s.tab_names == ["a.txt", "b.txt"] and s.active_tab.name == "a.txt"


def test_kitty_keyboard_ctrl_l(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"), keyboard="kitty")
    app.press("ctrl+l")
    app.type("25", each=True)
    s = app.press("enter")
    assert s.status == "Line 25 of 50." and s.pos[0] == 25


def test_ctrl_l_by_the_physical_key_on_a_cyrillic_layout(ltt, ws):
    ws.write("proj/a.txt", FIFTY)
    app = ltt(ws.path("proj/a.txt"))
    s = app.press(K.kitty_key("ctrl+д", base="l"))  # д sits on the L key
    assert s.mode == "PROMPT" and prompt_line(s) == " Go to:"


@pytest.mark.parametrize("suffix, pos", [(":2:5:", (2, 4, 5)), (":2:", (2, 4, 1))], ids=["line_col", "line"])
def test_a_location_with_a_colon_after_it_opens_there(ltt, ws, suffix, pos):  # dte's test/util.c
    p = ws.write("proj/b.txt", "one\ntwo two\nthree\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+l")
    app.type(p + suffix)
    s = app.press("enter")
    assert s.tab_names == ["b.txt"] and s.pos[:3] == pos
