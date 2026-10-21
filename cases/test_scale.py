"""Big data, right answers: 20,000 files, a million lines, a 3 MB line, a big repository, a big paste."""
import os

import pytest

from perf_helpers import (MILLION, MINIFIED_LEN, NEEDLES, PLAIN, SHORT_LINE, ZEBRAS, answer, big_repo, million_js,
                          million_line, minified_js, tree)

pytestmark = pytest.mark.slow


# ---------------------------------------------------------------- 20,000 files in one folder

def test_folder_of_20000_files_lists_them_all(ltt):
    app = ltt(tree() + "/flat", env=PLAIN)
    s = app.snap()
    assert s.tree_names[:4] == ["flat", "f00000.txt", "f00001.txt", "f00002.txt"]  # one size: by name
    app.press("end")
    s = app.snap()
    assert s.tree_selected.name == "f19999.txt" and s.tree[-1].name == "f19999.txt"
    assert s.tree_names[-2] == "f19998.txt"
    app.press("home")
    assert app.snap().tree_selected.name == "flat"


def test_page_down_walks_a_folder_of_20000_files(ltt):
    app = ltt(tree() + "/flat", env=PLAIN)
    page = len(app.snap().tree)  # the tree's rows: a page
    app.press("pgdn", "pgdn", "pgdn")
    assert app.snap().tree_selected.name == f"f{3 * page - 1:05d}.txt"
    app.press("pgup")
    assert app.snap().tree_selected.name == f"f{2 * page - 1:05d}.txt"


def test_last_file_of_20000_opens(ltt):
    app = ltt(tree() + "/flat", env=PLAIN)
    app.press("end", "enter")
    s = app.wait(lambda s: s.tab_names == ["f19999.txt"], what="the last file open")
    assert s.editor_lines[:2] == ["file 19999", "the brown cat sits on a warm mat"]


# ---------------------------------------------------------------- a million lines

def test_ctrl_end_shows_the_last_lines_of_a_million(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+end")
    s = app.snap()
    assert s.pos == (MILLION, MILLION, len(million_line(MILLION)) + 1, 0)
    rows = [(r.number, r.text) for r in s.editor_rows]
    first = rows[0][0]
    assert rows == [(n, million_line(n)) for n in range(first, MILLION + 1)]
    assert s.editor_row(MILLION).current and s.editor_row(MILLION).y == s.content_bottom - 1


def test_ctrl_home_after_ctrl_end_in_a_million_lines(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+end", "ctrl+home")
    s = app.snap()
    assert s.pos == (1, MILLION, 1, 0)
    assert s.editor_rows[0].number == 1 and s.editor_lines[0] == million_line(1)


def test_go_to_a_line_of_a_million(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+l")
    app.type("777777")
    app.press("enter")
    s = app.snap()
    assert s.status == "Line 777777 of 1000000."
    row = s.editor_row(777_777)
    assert row.current and row.text == million_line(777_777)


def test_typing_and_undo_at_the_end_of_a_million_lines(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+end")
    app.type("XY")
    s = app.snap()
    assert s.editor_lines[-1] == million_line(MILLION) + "XY" and s.title_dirty
    assert s.pos[:2] == (MILLION, MILLION)
    app.press("ctrl+z")
    s = app.snap()
    assert s.editor_lines[-1] == million_line(MILLION) and not s.title_dirty


def test_enter_at_the_end_of_a_million_lines_adds_a_line(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+end", "enter")
    s = app.snap()
    assert s.pos[:2] == (MILLION + 1, MILLION + 1)
    assert [r.number for r in s.editor_rows][-2:] == [MILLION, MILLION + 1] and s.editor_lines[-1] == ""
    assert s.editor_lines[-2] == million_line(MILLION) and s.title_dirty


def test_search_in_a_million_lines_finds_every_hit(ltt):
    app = ltt(million_js(), env=PLAIN)
    app.press("ctrl+f")
    app.type("zebra")
    s = app.wait_status("found: 4")
    assert [h.location for h in s.results] == [f"million.js:{n}" for n in ZEBRAS]
    app.press("down", "down", "enter")
    s = app.snap()
    assert s.pos[0] == 750_000 and s.editor_row(750_000).text == million_line(750_000)


def test_search_of_an_unopened_47mb_file_reads_it_whole(ltt):
    # S8: a file over the folder-search limit (8 MB) is still searched whole on its own
    app = ltt(os.path.dirname(million_js()), env=PLAIN)
    app.press("down", "ctrl+f")  # million.js selected in the tree, not open
    app.type("zebra")
    s = app.wait(lambda s: "found: 4" in s.status_line, timeout=30, what="four hits")
    assert [h.line for h in s.results] == list(ZEBRAS)
    assert s.tabs == []


def test_folder_search_skips_files_over_8mb_and_says_so(ltt, ws):
    # S8: folder searches leave out files over 8 MB and count them
    ws.write("notes/todo.txt", "feed the zebra\n")
    app = ltt(ws.path("notes"), os.path.dirname(million_js()), env=PLAIN)
    app.press("ctrl+f", "ctrl+f")  # all folders
    app.type("zebra")
    s = app.wait(lambda s: "found: 1" in s.status_line, timeout=30, what="the one hit")
    assert [h.location for h in s.results] == ["notes/todo.txt:1"]
    assert "(1 files over 8 MB skipped)" in s.results_header


# ---------------------------------------------------------------- 20,000 files in folders

def test_folder_search_over_20000_files_finds_the_rare_lines(ltt):
    app = ltt(tree() + "/nested", env=PLAIN)
    app.press("ctrl+f")
    app.type("zq")
    s = app.wait(lambda s: "found: 3" in s.status_line, timeout=60, what="three hits")
    assert sorted(h.location for h in s.results) == sorted(rel[len("nested/"):] + ":22" for rel in NEEDLES)


def test_folder_search_over_20000_files_truncates_with_plus(ltt):
    app = ltt(tree() + "/nested", env=PLAIN)
    app.press("ctrl+f")
    app.type("brown cat")
    s = app.wait(lambda s: "found: 500+" in s.status_line, timeout=60, what="a truncated count")
    assert "1/500+" in s.results_header and len(s.results) > 0
    app.press("esc")  # to the results
    assert app.snap().status == "Found: 500+. Enter opens above, Esc hides the panel."


def test_sizes_of_a_big_tree_are_counted_and_sorted(ltt):
    app = ltt(tree(), env=PLAIN)
    s = app.wait(lambda s: all(r.size[:1].isdigit() for r in s.tree[1:]), timeout=60, what="sizes counted")
    assert [(r.name, r.size) for r in s.tree] == [("perf-tree", ""), ("nested", "20M"), ("flat", "860K")]


def test_tree_answers_keys_while_sizes_are_counted(ltt):
    app = ltt(tree(), env=PLAIN)
    # at once after the first frame: the counting starts in the same turn as the answer to this key
    _, s = answer(app, "down", lambda s: s.tree_selected.name != "perf-tree")
    picked = s.tree_selected.name  # flat: some of it is counted before the first frame, so it comes first
    assert any(r.size == "…" or r.size.startswith("≥") for r in s.tree[1:]), s.dump()
    s = app.wait(lambda s: all(r.size[:1].isdigit() for r in s.tree[1:]), timeout=60, what="sizes counted")
    assert s.tree_names == ["perf-tree", "nested", "flat"] and s.tree_selected.name == picked


# ---------------------------------------------------------------- a 3 MB line

def test_line_over_20000_characters_is_drawn_without_colours(ltt):
    # V15: a 3 MB line of code is drawn plain; the short line after it keeps its colours
    app = ltt(minified_js(), env=PLAIN)
    s = app.snap()
    long_row, short_row = s.editor_rows[0], s.editor_rows[1]
    assert {s.fg(x, long_row.y) for x in range(s.text_x, s.cols - 1)} == {"Gray"}
    assert short_row.text == SHORT_LINE
    x = s.text_x
    assert (s.fg(x, short_row.y), s.fg(x + SHORT_LINE.index('"'), short_row.y),
            s.fg(x + SHORT_LINE.index("//"), short_row.y)) == ("Cyan", "Yellow", "DarkGreen")


def test_line_of_20000_characters_keeps_its_colours(ltt, ws):
    # V15: the limit is 20,000 characters: one more and the line is drawn plain
    line = ("var x = 1; " * 2000)[:20_000]
    ws.write("edge.js", line + "\n" + line + "x\n")
    app = ltt(ws.path("edge.js"))
    s = app.snap()
    assert [s.fg(s.text_x, r.y) for r in s.editor_rows[:2]] == ["Cyan", "Gray"]


def test_typing_at_the_end_of_a_3mb_line_keeps_the_line(ltt):
    app = ltt(minified_js(), env=PLAIN)
    app.press("end")
    app.type("XY")
    s = app.snap()
    assert s.pos == (1, 3, MINIFIED_LEN + 3, 0) and s.title_dirty
    assert s.editor_lines[0].endswith("var c=1;XY")  # the view follows the cursor to the end
    app.press("home")
    s = app.snap()
    assert s.editor_lines[0].startswith("function a(b){return b*2/*x*/") and s.pos[2] == 1


# ---------------------------------------------------------------- a big paste

def test_pasting_5000_lines_keeps_every_line(ltt, ws):
    ws.write("notes.txt", "start\n")
    app = ltt(ws.path("notes.txt"))
    app.paste("".join(f"line {i} of the pasted text, some words here\n" for i in range(5000)))
    s = app.wait(lambda s: s.status.startswith("Pasted"), what="the paste")
    assert s.status == "Pasted: 228890 chars" and s.pos == (5001, 5002, 1, 0)
    assert s.editor_lines[-1] == "start"
    app.press("ctrl+l")
    app.type("2500")
    app.press("enter")
    assert app.snap().editor_row(2500).text == "line 2499 of the pasted text, some words here"
    app.press("ctrl+z")  # one undo takes the whole paste back
    s = app.snap()
    assert s.pos[:2] == (1, 2) and s.editor_lines[:2] == ["start", ""] and not s.title_dirty


# ---------------------------------------------------------------- a big repository

def test_git_status_of_a_big_repository_colours_the_tree(ltt, ws):
    app = ltt(big_repo(ws))
    s = app.wait(lambda s: s.tree_row("src").fg == "Yellow", timeout=30, what="git status")
    colours = {r.name: r.fg for r in s.tree[1:] if not r.selected}
    assert colours == {"src": "Yellow", "README.md": "Gray", ".git": "DarkGray", "node_modules": "DarkGray",
                       ".gitignore": "DarkGray"}
    app.press("down", "right")  # src/
    app.wait(lambda s: s.tree_row("module00") and all(r.size[:1].isdigit() for r in s.tree[2:]), timeout=30,
             what="the folders of src/ counted")
    app.press("down", "right")  # the biggest of them: module00, the only one with changes
    s = app.wait(lambda s: s.tree_row("Untracked.cs") is not None, what="module00 expanded")
    rows = {r.name: r for r in s.tree}
    assert rows["module00"].selected and rows["module00"].expanded
    assert s.tree_names[3:6] == ["Untracked.cs", "Item000.cs", "Item099.cs"]  # biggest first
    assert (rows["Untracked.cs"].fg, rows["Item000.cs"].fg, rows["Item099.cs"].fg) == ("Green", "Yellow", "Gray")


def test_search_in_a_big_repository_leaves_out_ignored_files(ltt, ws):
    # 2,000 files in node_modules/ say "module.exports": git ignores them, and so does search
    app = ltt(big_repo(ws))
    app.press("ctrl+f")
    app.type("exports")
    s = app.wait(lambda s: "found:" in s.status_line, timeout=30, what="the search done")
    assert "found: 0" in s.status_line


def test_search_in_a_big_repository_covers_untracked_files(ltt, ws):
    app = ltt(big_repo(ws))
    app.press("ctrl+f")
    app.type("git yet")
    s = app.wait(lambda s: "found:" in s.status_line, timeout=30, what="the search done")
    assert "found: 250" in s.status_line
    assert {h.path for h in s.results} == {"src/module00/Untracked.cs"}
