"""Search: what Ctrl+F searches, the live input, fuzzy matching and ranking, and which files a search covers."""
import os
import sys
import time
import unicodedata

import pytest

from harness import keys as K
from search_helpers import (BIG_LINES, GitGate, big_log, final_results, found, locations, search, search_input,
                            select, wait_found)


@pytest.fixture
def git_gate(ws):
    """GitGate(ws, hold) for the test; a held listing is let go at the end whatever happened."""
    gates = []

    def make(hold="before"):
        gates.append(GitGate(ws, hold))
        return gates[-1]
    yield make
    for g in gates:
        g.open()


# ---------------------------------------------------------------- scopes


def test_ctrl_f_on_a_root_searches_its_folder(ltt, ws):
    ws.write("proj/a.txt", "needle one\n")
    ws.write("proj/sub/b.txt", "needle two\n")
    app = ltt(ws.path("proj"), cols=120)
    s = app.press("ctrl+f")
    assert s.prompt == "Search folder:    · search starts at 2 characters"
    assert s.help == ("Searching this folder live · Ctrl+F again widens to all folders · ↑↓ pick · Enter opens"
                      " · Esc to results")
    s = search(app, "needle", 2)
    assert s.results_header.startswith('Search results (folder proj) for "needle": 1/2 ·')
    assert sorted(locations(s)) == ["a.txt:1", "sub/b.txt:1"]


def test_ctrl_f_on_a_subfolder_searches_only_inside_it(ltt, ws):
    ws.write("proj/a.txt", "needle outside\n")
    ws.write("proj/sub/b.txt", "x\nneedle inside\n")
    app = ltt(ws.path("proj"))
    select(app, "sub")
    app.press("ctrl+f")
    s = search(app, "needle", 1)
    assert s.results_header.startswith('Search results (folder sub) for "needle": 1/1 ·')
    assert locations(s) == ["b.txt:2"]  # paths start at the searched folder


def test_ctrl_f_on_a_file_in_the_tree_searches_that_file_without_opening_it(ltt, ws):
    ws.write("proj/a.txt", "needle in a\n")
    ws.write("proj/b.txt", "needle in b\nand another needle\n")
    app = ltt(ws.path("proj"), cols=120)
    select(app, "b.txt")
    s = app.press("ctrl+f")
    assert s.prompt == "Search file:    · search starts at 2 characters"
    assert s.help == ("Searching this file live · Ctrl+F again widens to all folders · ↑↓ pick · Enter jumps"
                      " · Esc to results")
    s = search(app, "needle", 2)
    assert s.results_header.startswith('Search results (file b.txt) for "needle": 1/2 ·')
    assert locations(s) == ["b.txt:1", "b.txt:2"]
    assert s.tabs == []


def test_ctrl_f_in_the_editor_searches_the_open_file(ltt, ws):
    ws.write("proj/a.txt", "needle one\nnothing\nneedle two\n")
    ws.write("proj/b.txt", "needle elsewhere\n")
    app = ltt(ws.path("proj/a.txt"))
    s = app.press("ctrl+f")
    assert s.prompt == "Search file:    · search starts at 2 characters"
    s = search(app, "needle", 2)
    assert s.results_header.startswith('Search results (file a.txt) for "needle": 1/2 ·')
    assert locations(s) == ["a.txt:1", "a.txt:3"]


def test_second_ctrl_f_widens_to_all_folders(ltt, ws):
    ws.write("proj/a.txt", "needle one\n")
    ws.write("proj/sub/b.txt", "needle two\n")
    app = ltt(ws.path("proj/a.txt"), cols=120)
    app.press("ctrl+f")
    s = app.press("ctrl+f")
    assert s.prompt == "Search all folders:    · search starts at 2 characters"
    assert s.help == ("Searching all folders live · Ctrl+F again narrows to the selection · ↑↓ pick · Enter opens"
                      " · Esc to results")
    s = search(app, "needle", 2)
    assert s.results_header.startswith('Search results (all folders) for "needle": 1/2 ·')
    assert sorted(locations(s)) == ["a.txt:1", "sub/b.txt:1"]


def test_ctrl_f_in_the_input_toggles_the_scope_of_the_typed_query(ltt, ws):
    ws.write("proj/a.txt", "needle one\nneedle two\n")
    ws.write("proj/b.txt", "needle three\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    search(app, "needle", 2, scope="file")
    app.press("ctrl+f")
    s = wait_found(app, "needle", 3, scope="all folders")
    assert sorted(locations(s)) == ["a.txt:1", "a.txt:2", "b.txt:1"]
    app.press("ctrl+f")
    s = wait_found(app, "needle", 2, scope="file")
    assert locations(s) == ["a.txt:1", "a.txt:2"]


def test_all_folders_search_covers_every_root(ltt, ws):
    ws.write("projA/a.txt", "needle in A\n")
    ws.write("projB/b.txt", "needle in B\n")
    app = ltt(ws.path("projA"), ws.path("projB"))
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["projA/a.txt:1", "projB/b.txt:1"]  # with several roots, paths start at the root


def test_ctrl_f_works_on_a_cyrillic_layout(ltt, ws):
    # kitty sends Cyrillic "а" with the Latin key under it (F) as the base: still Ctrl+F
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    s = app.press(K.kitty_key("ctrl+а", base="f"))
    assert search_input(s) == ("folder", "", "search starts at 2 characters")


# ---------------------------------------------------------------- the live input


def test_search_starts_at_two_characters(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = app.type("n")
    assert search_input(s) == ("folder", "n", "search starts at 2 characters")
    assert s.results_top is None
    app.type("e")
    s = wait_found(app, "ne", 1)
    assert locations(s) == ["a.txt:1"]


def test_spaces_around_the_query_do_not_count(ltt, ws):
    ws.write("proj/a.txt", "a b\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = app.type(" a ")
    assert search_input(s) == ("folder", " a ", "search starts at 2 characters")
    assert s.results_top is None


def test_searching_is_shown_until_the_scan_ends(ltt, ws, git_gate):
    ws.repo("proj", files={"a.txt": "needle\n"})
    gate = git_gate()
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    s = app.paste("needle")
    assert search_input(s) == ("folder", "needle", "searching…")
    app.wait(lambda s: gate.waiting, what="the file listing held")
    s = app.snap()
    assert search_input(s).suffix == "searching…" and s.results_top is None
    gate.open()
    s = wait_found(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]


def test_no_match_shows_found_0_and_no_panel(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "zzqqxx", 0)
    assert s.results_top is None


def test_backspace_edits_the_query_and_searches_again(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needlex", 0)
    app.press("backspace")
    s = wait_found(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]


def test_backspace_below_two_characters_hides_the_results(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "ne", 1)
    s = app.press("backspace")
    assert search_input(s) == ("folder", "n", "search starts at 2 characters")
    assert s.results_top is None


def test_backspace_deletes_a_whole_character(ltt, ws):
    ws.write("proj/a.txt", "x\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    app.type("ab👀")
    s = app.press("backspace")
    assert search_input(s).query == "ab"
    app.type("e\u0301")  # e + a combining acute accent
    s = app.press("backspace")
    assert search_input(s).query == "ab"


def test_ctrl_u_clears_the_query(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "needle", 1)
    s = app.press("ctrl+u")
    assert search_input(s) == ("folder", "", "search starts at 2 characters")
    assert s.results_top is None


def test_a_paste_into_the_input_becomes_one_line(ltt, ws):
    ws.write("proj/a.txt", "alpha beta gamma\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    app.paste("alpha\nbeta\tgamma\n")
    s = wait_found(app, "alpha beta gamma", 1)
    assert locations(s) == ["a.txt:1"]


def test_the_cursor_follows_a_query_of_wide_characters(ltt, ws):
    ws.write("proj/a.txt", "日本語\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "日本", 1)
    assert s.cursor == (len(" Search folder: ") + 4, s.status_y)


def test_a_long_query_shows_its_end_and_the_count(ltt, ws):
    ws.write("proj/a.txt", "x\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    query = "".join(f"{i:03d}" for i in range(50))  # 150 characters
    app.type(query)
    s = app.wait(lambda s: s.prompt is not None and s.prompt.endswith("   · found: 0"), what="the count")
    shown = s.prompt[len("Search file: …"):-len("   · found: 0")]
    assert s.prompt.startswith("Search file: …") and len(shown) > 50 and query.endswith(shown)
    assert s.cursor == (len(" Search file: …") + len(shown), s.status_y)


# ---------------------------------------------------------------- matching and ranking


def test_letters_in_order_find_a_word(ltt, ws):
    ws.write("proj/a.txt", "nothing here\nthe manager is in\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "mngr", 1)
    assert locations(s) == ["a.txt:2"]


def test_search_ignores_case(ltt, ws):
    ws.write("proj/a.txt", "class TaskManager\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "taskMANAGER", 1)
    assert locations(s) == ["a.txt:1"]


def test_cyrillic_letters_match_in_order_and_in_any_case(ltt, ws):
    ws.write("proj/ru.txt", "ничего\nПривет, мир\n")
    app = ltt(ws.path("proj/ru.txt"))
    app.press("ctrl+f")
    s = search(app, "ПРВТ", 1)
    assert locations(s) == ["ru.txt:2"]


def test_every_word_must_match_in_any_order(ltt, ws):
    ws.write("proj/a.txt", "File.ReadAllLines(path)\nFile.Open(path)\nreader of files\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "read file", 2)
    assert locations(s) == ["a.txt:1", "a.txt:3"]


def test_letters_spread_far_apart_are_not_a_match(ltt, ws):
    # S7: a match may skip at most 10 characters — not "a" here and "nchor" 30 cells later
    ws.write("proj/a.txt", "a" + " " * 30 + "nchor\nan anchor\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "anchor", 1)
    assert locations(s) == ["a.txt:2"]


def test_the_jump_lands_on_the_compact_match_not_on_the_first_letters(ltt, ws):
    # S7: "anhr" is the word anchor (column 12), not a…n…h…r picked up from the start of the line
    ws.write("proj/a.txt", "a big nice anchor\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    search(app, "anhr", 1)
    s = app.press("enter")
    assert s.pos == (1, 2, 12, 0)


def test_an_exact_match_ranks_above_fuzzy_ones(ltt, ws):
    ws.write("proj/a.txt", "m-a-n-a-g-e-r\n")
    ws.write("proj/z.txt", "manager\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "manager", 2)
    assert locations(s) == ["z.txt:1", "a.txt:1"]


def test_a_match_at_a_word_start_ranks_above_one_inside_a_word(ltt, ws):
    ws.write("proj/a.txt", "xmanager\n")
    ws.write("proj/b.txt", "the manager\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "manager", 2)
    assert locations(s) == ["b.txt:1", "a.txt:1"]


def test_an_earlier_match_in_the_line_ranks_higher(ltt, ws):
    ws.write("proj/a.txt", "some words before the manager\n")
    ws.write("proj/b.txt", "manager first\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "manager", 2)
    assert locations(s) == ["b.txt:1", "a.txt:1"]


def test_equal_matches_are_ordered_by_path_then_line(ltt, ws):
    ws.write("proj/b.txt", "needle\nx\nneedle\n")
    ws.write("proj/a.txt", "x\nneedle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 3)
    assert locations(s) == ["a.txt:2", "b.txt:1", "b.txt:3"]


def test_hits_in_one_file_keep_their_order_in_the_file(ltt, ws):
    ws.write("proj/a.txt", "m-a-n-a-g-e-r\nmanager\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "manager", 2)
    assert locations(s) == ["a.txt:1", "a.txt:2"]


def test_more_than_500_hits_are_marked_with_a_plus(ltt, ws):
    # S4: the panel keeps 500 hits and says there were more
    ws.write("proj/a.txt", "needle\n" * 600)
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    s = search(app, "needle", "500+")
    assert s.results_header.startswith('Search results (file a.txt) for "needle": 1/500+ ·')
    s = app.press("esc")
    assert s.status == "Found: 500+. Enter opens above, Esc hides the panel."


def test_an_exact_match_after_thousands_of_fuzzy_ones_ranks_first(ltt, ws):
    # S4: every hit is ranked, not only the first 5000 found
    ws.write("proj/fuzzy.txt", "m.a.n.a.g.e.r\n" * 6000)
    ws.write("proj/zz.txt", "manager\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "manager", "500+")
    assert locations(s)[0] == "zz.txt:1"


# ---------------------------------------------------------------- refining while typing


def test_refining_after_a_capped_result_finds_every_match(ltt, ws):
    # S1: "co" hits more than 500 lines; "config" must not be filtered out of those 500
    ws.write("proj/a.txt", "co co\n" * 600)
    pad = "x" * 120  # the config lines rank below every "co co" for "co"
    ws.write("proj/z.txt", f"{pad} config one\n{pad} config two\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "co", "500+")
    for q in ("con", "conf", "confi", "config"):
        app.type(q[-1])
        s = wait_found(app, q, 2)
    assert locations(s) == ["z.txt:1", "z.txt:2"]


def test_a_query_typed_letter_by_letter_finds_what_a_pasted_one_finds(ltt, ws):
    # V23: refining "ab" into "abc" keeps exactly the lines a fresh "abc" finds
    ws.write("proj/a.txt", "a" + "x" * 10 + "bc\n" + "a" + "x" * 11 + "bc\n" + "abc\n" + "a-b-c\n" + "cab\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "ab", 4)
    app.type("c")
    typed = locations(wait_found(app, "abc", 3))
    app.press("ctrl+u")
    app.paste("abc")
    fresh = locations(wait_found(app, "abc", 3))
    assert typed == fresh == ["a.txt:3", "a.txt:4", "a.txt:1"]


def test_an_outside_edit_makes_the_next_letter_search_the_files_again(ltt, ws):
    # V23: results from before a change are not refined; the files are read again
    ws.write("proj/a.txt", "abq one\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    search(app, "ab", 1)
    ws.append("proj/a.txt", "abc new\nabc two\n")
    ws.write("proj/marker.txt", "zz\n")
    app.wait_tree("marker.txt")  # ltt has taken in the change
    app.type("c")
    s = wait_found(app, "abc", 2)
    assert locations(s) == ["a.txt:2", "a.txt:3"]


# ---------------------------------------------------------------- what a search covers


def test_packages_bin_and_obj_folders_are_searched(ltt, ws):
    # S3: these are ordinary names (a JS monorepo's packages/*), not build output to skip
    for d in ("packages/ui", "bin", "obj", "src/bin"):
        ws.write(f"proj/{d}/x.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 4)
    assert sorted(locations(s)) == ["bin/x.txt:1", "obj/x.txt:1", "packages/ui/x.txt:1", "src/bin/x.txt:1"]


def test_a_plain_folder_search_skips_tool_and_dependency_folders(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    for d in ("node_modules/lib", ".venv/lib", "__pycache__", ".tox", ".idea", ".vs", ".hg", ".svn"):
        ws.write(f"proj/{d}/x.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]


@pytest.mark.parametrize("repo", [False, True], ids=["plain", "git"])
def test_symlinked_folders_are_not_followed(ltt, ws, repo):
    # S5: links back up the tree gave hundreds of duplicates, and a query without hits never ended
    if repo:
        ws.repo("proj", files={"a.txt": "needle\n"})
    else:
        ws.write("proj/a.txt", "needle\n")
    ws.mkdir("proj/sub")
    os.symlink(ws.path("proj"), ws.path("proj/loop"))
    os.symlink("..", ws.path("proj/sub/up"))
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]
    app.press("ctrl+u")
    search(app, "zzqqxx", 0)


def test_a_repository_search_follows_gitignore(ltt, ws):
    # S6: untracked files are searched, ignored ones are not
    ws.repo("proj", files={"src/a.txt": "needle\n", "build/out.txt": "needle\n", "debug.log": "needle\n"},
            ignore="build/\n*.log\n")
    ws.write("proj/new.txt", "needle\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["new.txt:1", "src/a.txt:1"]


def test_a_repository_inside_a_plain_folder_keeps_its_gitignore(ltt, ws):
    # V29
    ws.write("top/loose.txt", "needle\n")
    ws.repo("top/repo", files={"kept.txt": "needle\n", "secret.txt": "needle\n"}, ignore="secret.txt\n")
    app = ltt(ws.path("top"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["loose.txt:1", "repo/kept.txt:1"]


def test_a_nested_repository_is_searched_with_its_own_gitignore(ltt, ws):
    ws.repo("outer", files={"a.txt": "needle\n"})
    ws.repo("outer/inner", files={"b.txt": "needle\n", "hidden.txt": "needle\n"}, ignore="hidden.txt\n")
    app = ltt(ws.path("outer"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["a.txt:1", "inner/b.txt:1"]


def test_a_root_inside_another_root_is_searched_where_git_leaves_it_out(ltt, ws):
    # V22: the outer repository ignores vendor/, but vendor/ is a root of its own
    ws.repo("outer", files={"a.txt": "needle\n"}, ignore="vendor/\n")
    ws.write("outer/vendor/lib.txt", "needle\n")
    app = ltt(ws.path("outer"), ws.path("outer/vendor"))
    app.press("ctrl+f")  # the root "outer" alone
    s = search(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]
    app.press("ctrl+f")
    s = wait_found(app, "needle", 2, scope="all folders")
    assert sorted(locations(s)) == ["a.txt:1", "vendor/lib.txt:1"]


def test_an_ignored_folder_selected_in_the_tree_is_searched(ltt, ws):
    ws.repo("proj", files={"a.txt": "needle\n"}, ignore="build/\n")
    ws.write("proj/build/out.txt", "needle\n")
    app = ltt(ws.path("proj"))
    select(app, "build")
    app.press("ctrl+f")
    s = search(app, "needle", 1)
    assert locations(s) == ["out.txt:1"]


def test_overlapping_roots_list_each_hit_once(ltt, ws):
    # S11
    ws.write("proj/a.txt", "needle\n")
    ws.write("proj/src/b.txt", "needle\n")
    app = ltt(ws.path("proj"), ws.path("proj/src"))
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["a.txt:1", "src/b.txt:1"]


def test_a_fifo_in_a_searched_folder_does_not_hang_the_search(ltt, ws):
    # D11: reading a FIFO blocks until someone writes to it; the search leaves it alone
    ws.write("proj/a.txt", "needle\n")
    os.mkfifo(ws.path("proj/pipe"))
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 1)
    assert locations(s) == ["a.txt:1"]


def test_a_fifo_selected_in_the_tree_is_searched_as_empty(ltt, ws):
    ws.write("proj/a.txt", "needle\n")
    os.mkfifo(ws.path("proj/pipe"))
    app = ltt(ws.path("proj"))
    select(app, "pipe")
    app.press("ctrl+f")
    s = search(app, "needle", 0)
    assert search_input(s).scope == "file"


def test_binary_files_are_not_searched(ltt, ws):
    ws.write("proj/text.txt", "needle in text\n")
    ws.write("proj/image.png", "needle in a png\n")
    ws.write("proj/data.txt", b"needle before\n\x00\x01\x02 binary\nneedle after\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert sorted(locations(s)) == ["data.txt:1", "text.txt:1"]  # a file is read up to its first binary line


def test_a_folder_search_reads_unsaved_edits(ltt, ws):
    # S11: open tabs are searched as they are on screen, not as on disk
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj/a.txt"))
    app.type("zebra ")
    app.wait(lambda s: s.title_dirty)
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "zebra", 1)
    assert [(h.location, h.snippet) for h in s.results] == [("a.txt:1", "zebra alpha")]


def test_a_folder_search_does_not_find_text_removed_by_unsaved_edits(ltt, ws):
    ws.write("proj/a.txt", "zebra\nalpha\n")
    ws.write("proj/b.txt", "zebra too\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("shift+down", "backspace")
    app.wait(lambda s: s.title_dirty and s.editor_lines[0] == "alpha")
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "zebra", 1)
    assert locations(s) == ["b.txt:1"]


def test_a_file_search_reads_unsaved_edits(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj/a.txt"))
    app.type("zebra ")
    app.press("ctrl+f")
    s = search(app, "zebra", 1)
    assert [(h.location, h.snippet) for h in s.results] == [("a.txt:1", "zebra alpha")]


def test_a_folder_search_works_after_a_file_was_moved_onto_another_open_file(ltt, ws):
    # V19: both tabs had unsaved edits; the move left two tabs on one path and every key of a folder search threw
    ws.write("proj/a.txt", "alpha line\n")
    ws.write("proj/b.txt", "beta line\n")
    app = ltt(ws.path("proj/a.txt"), ws.path("proj/b.txt"))
    app.type("X")
    app.press("alt+2")
    app.type("Y")
    ws.rename("proj/a.txt", "proj/b.txt")
    app.wait(lambda s: s.tabs[0].missing and s.tabs[1].changed, what="a.txt gone, b.txt changed on disk")
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "line", 1)
    assert [(h.location, h.snippet) for h in s.results] == [("b.txt:1", "Ybeta line")]


def test_ctrl_f_on_a_file_in_the_tree_searches_its_unsaved_edits(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj/a.txt"))
    app.type("zebra ")
    s = app.press("esc")  # to the tree, where a.txt is selected
    assert s.tree_selected.name == "a.txt"
    app.press("ctrl+f")
    s = search(app, "zebra", 1)
    assert [(h.location, h.snippet) for h in s.results] == [("a.txt:1", "zebra alpha")]


@pytest.mark.skipif(sys.platform != "darwin", reason="macOS takes both Unicode forms of a name for one file")
def test_an_open_file_with_a_decomposed_name_is_searched_and_opened_in_its_tab(ltt, ws):
    # V27: git lists "café.txt" composed; the tab holds the name as typed (decomposed)
    name = unicodedata.normalize("NFD", "café.txt")
    ws.repo("proj", files={name: "needle here\n"})
    assert unicodedata.is_normalized("NFC", ws.git("ls-files", "-z", cwd="proj"))
    app = ltt(ws.path("proj"), ws.path(os.path.join("proj", name)))
    app.type("Z")
    app.wait(lambda s: s.title_dirty)
    app.press("ctrl+f", "ctrl+f")
    s = search(app, "needle", 1)
    assert [(unicodedata.normalize("NFC", h.location), h.snippet) for h in s.results] == [("café.txt:1", "Zneedle here")]
    s = app.press("enter")
    assert len(s.tabs) == 1 and s.pos[:3] == (1, 2, 2)


def test_control_characters_in_hits_never_reach_the_terminal(ltt, ws):
    # D12: a line or a file name that would set the window title or write the clipboard is shown inert
    ws.write("proj/a.txt", "needle \x1b]0;PWNED\x07 and \x1b]52;c;aGk=\x07 end\n")
    ws.write("proj/evil\x1b]0;NAME\x07.txt", "needle too\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "needle", 2)
    assert len(s.results) == 2 and not s.window_title
    assert b"\x1b]0;" not in app.output and b"\x1b]52;" not in app.output


# ---------------------------------------------------------------- encodings

SUMMER = "summer été here\n"


def test_utf16_files_with_a_bom_are_searched_as_text(ltt, ws):
    ws.write("proj/le.txt", b"\xff\xfe" + SUMMER.encode("utf-16-le"))
    ws.write("proj/be.txt", b"\xfe\xff" + SUMMER.encode("utf-16-be"))
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "été", 2)
    assert [(h.location, h.snippet) for h in s.results] == [("be.txt:1", "summer été here"),
                                                            ("le.txt:1", "summer été here")]


def test_a_utf8_bom_is_not_part_of_the_first_line(ltt, ws):
    ws.write("proj/bom.txt", b"\xef\xbb\xbf" + "été first\n".encode())
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "été", 1)
    assert s.results[0].snippet == "été first"
    s = app.press("enter")
    assert s.pos[:3] == (1, 2, 1)


def test_an_open_latin1_file_is_searched_as_the_editor_shows_it(ltt, ws):
    ws.write("proj/latin1.txt", SUMMER.encode("latin-1"))
    app = ltt(ws.path("proj/latin1.txt"))
    assert app.snap().editor_lines[0] == "summer été here"
    app.press("ctrl+f")
    s = search(app, "été", 1)
    assert [(h.location, h.snippet) for h in s.results] == [("latin1.txt:1", "summer été here")]


def test_a_folder_search_reads_a_latin1_file_as_the_editor_does(ltt, ws):
    ws.write("proj/latin1.txt", SUMMER.encode("latin-1"))
    ws.write("proj/utf8.txt", "plain été too\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    app.type("été")
    s = final_results(app, "été")
    assert sorted(locations(s)) == ["latin1.txt:1", "utf8.txt:1"]


def test_a_latin1_file_selected_in_the_tree_is_searched_as_the_editor_reads_it(ltt, ws):
    ws.write("proj/latin1.txt", SUMMER.encode("latin-1"))
    app = ltt(ws.path("proj"))
    select(app, "latin1.txt")
    app.press("ctrl+f")
    app.type("été")
    s = final_results(app, "été")
    assert found(s) == "1" and locations(s) == ["latin1.txt:1"]


def test_a_hit_in_a_latin1_file_shows_its_line_as_the_editor_does(ltt, ws):
    ws.write("proj/latin1.txt", SUMMER.encode("latin-1"))
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "summer", 1)
    assert s.results[0].snippet == "summer été here"


# ---------------------------------------------------------------- big files


def test_a_folder_search_skips_files_over_8_mb_and_says_so(ltt, ws):
    # S8
    big_log(ws, "proj/big.log")
    ws.write("proj/small.txt", "zqxneedle here\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+f")
    s = search(app, "zqxneedle", 1)
    assert locations(s) == ["small.txt:1"]
    assert s.results_header.startswith(
        'Search results (folder proj) for "zqxneedle": 1/1 (1 files over 8 MB skipped) ·')


def test_a_big_file_selected_in_the_tree_is_searched_whole(ltt, ws):
    # S8: the size limit is for folder searches only
    big_log(ws, "proj/big.log")
    app = ltt(ws.path("proj"))
    select(app, "big.log")
    app.press("ctrl+f")
    s = search(app, "zqxneedle", 1, timeout=30)
    assert locations(s) == [f"big.log:{BIG_LINES + 1}"]


def test_searching_a_big_unopened_file_keeps_the_input_live(ltt, ws):
    # V25: the file is read in the background; keys are answered meanwhile
    big_log(ws, "proj/big.log")
    app = ltt(ws.path("proj"))
    select(app, "big.log")
    app.press("ctrl+f")
    s = app.type("zqxneedl")
    assert search_input(s) == ("file", "zqxneedl", "searching…")
    s = app.type("e")
    assert search_input(s) == ("file", "zqxneedle", "searching…")
    wait_found(app, "zqxneedle", 1, timeout=30)


# ---------------------------------------------------------------- scans in the background


def test_an_outside_change_does_not_cancel_a_running_search(ltt, ws, git_gate):
    # S2: an agent writing a file while the scan ran used to leave "found: 0"
    ws.repo("proj", files={"needle.txt": "the zqxneedle is here\n"})
    gate = git_gate()
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    app.paste("zqxneedle")
    app.wait(lambda s: gate.waiting, what="the file listing held")
    ws.write("proj/outside.txt", "an agent wrote this\n")
    s = app.wait_tree("outside.txt")  # ltt took in the change while the scan ran
    assert search_input(s).suffix == "searching…"
    gate.open()
    s = wait_found(app, "zqxneedle", 1)
    assert locations(s) == ["needle.txt:1"]


def test_a_file_list_taken_before_an_outside_change_is_not_reused(ltt, ws, git_gate):
    # V24: the listing that was running when b.txt appeared must not be cached for the next scan
    ws.repo("proj", files={"a.txt": "needle one\n"})
    gate = git_gate(hold="after")
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    app.paste("needle")
    app.wait(lambda s: gate.waiting, what="git listed the files and is held")
    ws.write("proj/b.txt", "needle two\n")
    app.wait_tree("b.txt")
    gate.open()
    s = wait_found(app, "needle", 1)  # that scan worked from the listing without b.txt
    assert locations(s) == ["a.txt:1"]
    app.press("backspace")
    s = wait_found(app, "needl", 2)
    assert sorted(locations(s)) == ["a.txt:1", "b.txt:1"]


@pytest.mark.slow
def test_a_hung_git_listing_gives_way_to_walking_the_folder(ltt, ws, git_gate):
    # git gets 15 s to list the files; then the folder is walked instead
    ws.repo("proj", files={"a.txt": "needle\n"})
    gate = git_gate()  # never opened while ltt runs
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    app.paste("needle")
    app.wait(lambda s: gate.waiting, what="the file listing held")
    s = wait_found(app, "needle", 1, timeout=40)
    assert locations(s) == ["a.txt:1"]


def test_esc_stops_a_running_scan(ltt, ws, git_gate):
    # S9: the panel of a cancelled scan must never pop up later
    ws.repo("proj", files={"a.txt": "zqxneedle\n"})
    gate = git_gate()
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    app.paste("zqxneedle")
    app.wait(lambda s: gate.waiting, what="the file listing held")
    s = app.press("esc")
    assert (s.mode, s.status, s.results_top) == ("TREE", "Search cancelled.", None)
    gate.open()
    time.sleep(1.0)  # a scan left running finishes within a few ticks from here
    s = app.snap()
    assert (s.mode, s.status, s.results_top) == ("TREE", "Search cancelled.", None)


def test_ctrl_c_stops_a_running_scan(ltt, ws, git_gate):
    ws.repo("proj", files={"a.txt": "zqxneedle\n"})
    gate = git_gate()
    app = ltt(ws.path("proj"), path=gate.path)
    app.press("ctrl+f")
    app.paste("zqxneedle")
    app.wait(lambda s: gate.waiting, what="the file listing held")
    s = app.press("ctrl+c")
    assert (s.mode, s.status, s.results_top) == ("TREE", "Cancelled (Ctrl+C).", None)
    gate.open()
    time.sleep(1.0)  # a scan left running finishes within a few ticks from here
    s = app.snap()
    assert (s.mode, s.status, s.results_top) == ("TREE", "Cancelled (Ctrl+C).", None)


def test_the_count_is_never_stale_after_a_fast_scan(ltt, ws):
    # The input reads "searching…" only while the scan task runs; one that has just ended but
    # is not applied yet shows the old count. A big frame (wide editor full of code) makes the
    # gap between taking the results and drawing them wide enough to hit almost every time.
    ws.write("proj/a.txt", "manager here\n")
    ws.write("proj/view.cs", "".join(
        f"    public static int Method{i}(string value, int count) {{ return value.Length + count * {i}; }}"
        f" /* comment {i} */ // trailing words\n" for i in range(300)))
    app = ltt(ws.path("proj"), ws.path("proj/view.cs"), cols=250, rows=80)
    app.press("esc")
    select(app, "a.txt")
    for q in ("manager", "here", "mngr", "anager", "ere") * 4:
        app.press("ctrl+f")
        app.write(q)
        shown = set()
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:  # watch every state the input goes through
            i = search_input(app.snap())
            if i and i.query == q:
                shown.add(i.suffix)
                if i.suffix == "found: 1":
                    break
            time.sleep(0.002)
        assert "found: 1" in shown
        assert "found: 0" not in shown, f"{q!r}: the input showed found: 0 before found: 1"
        app.press("esc", "esc")


@pytest.mark.xfail(strict=True, reason="BUG: a fuzzy match is looked for from the first 32 places of the query's "
                                       "first letter only, so a long line (a log, minified code) misses it")
def test_a_match_after_many_of_the_first_letter_is_found(ltt, ws):  # Kakoune's ranked_match tries every start
    p = ws.write("proj/f.txt", "see " * 20 + "error\nnothing\n")
    app = ltt(p)
    app.press("ctrl+f")
    app.type("eror")
    s = final_results(app, "eror")
    assert found(s) == "1" and locations(s) == ["f.txt:1"]
