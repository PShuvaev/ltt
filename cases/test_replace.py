"""Exact replacement in the open buffer, a selected folder and every root."""

from search_helpers import search, select, wait_found


def begin_replace(app, query, n, scope=None):
    """Open replacement directly, type its find query and focus the replacement field."""
    app.press("alt+r")
    s = search(app, query, n, scope)
    app.press("tab")
    return s


def confirm(app):
    return app.wait(lambda s: s.mode == "CONFIRM", what="replacement confirmation")


def test_leaving_replace_restores_fuzzy_results(ltt, ws):
    ws.write("proj/a.txt", "manager\nManager\nm-a-n-a-g-e-r\n")
    app = ltt(ws.path("proj/a.txt"))
    app.press("ctrl+f")
    search(app, "mngr", 3)
    app.press("alt+r")
    s = wait_found(app, "mngr", 0)
    assert s.results_top is None
    app.press("alt+r")
    s = wait_found(app, "mngr", 3)
    assert len(s.results) == 3 and not s.title_dirty


def test_single_replacement_advances_and_undo_stays_in_the_prompt(ltt, ws):
    ws.write("proj/a.txt", "foo one foo\nFoo foo\n")
    app = ltt(ws.path("proj/a.txt"))
    begin_replace(app, "foo", 3)
    app.type("bar")
    s = app.press("enter")
    assert s.editor_lines[:2] == ["bar one foo", "Foo foo"]
    assert s.title_dirty and ws.read("proj/a.txt") == "foo one foo\nFoo foo\n"
    s = app.press("ctrl+z")
    assert s.editor_lines[:2] == ["foo one foo", "Foo foo"] and not s.title_dirty


def test_replace_all_in_file_is_one_undo_and_does_not_save(ltt, ws):
    ws.write("proj/a.txt", "foo one foo\nFoo foo\n")
    app = ltt(ws.path("proj/a.txt"))
    begin_replace(app, "foo", 3)
    app.type("bar")
    app.press("alt+a")
    assert "Replace 3 occurrences" in confirm(app).status
    s = app.press("r")
    assert s.editor_lines[:2] == ["bar one bar", "Foo bar"]
    assert s.status == "Replaced 3 occurrences. Ctrl+Z undoes; Ctrl+S saves."
    assert s.title_dirty and ws.read("proj/a.txt") == "foo one foo\nFoo foo\n"
    s = app.press("ctrl+z")
    assert s.editor_lines[:2] == ["foo one foo", "Foo foo"] and not s.title_dirty


def test_replace_all_in_a_selected_folder_leaves_other_folders_alone(ltt, ws):
    ws.write("proj/a.txt", "foo outside\n")
    ws.write("proj/sub/b.txt", "foo inside foo\n")
    ws.write("proj/other/c.txt", "foo elsewhere\n")
    app = ltt(ws.path("proj"))
    select(app, "sub")
    begin_replace(app, "foo", 2, scope="folder")
    app.type("bar")
    app.press("alt+a")
    assert "2 occurrences in 1 files in folder sub" in confirm(app).status
    app.press("r")
    assert ws.read("proj/sub/b.txt") == "bar inside bar\n"
    assert ws.read("proj/a.txt") == "foo outside\n"
    assert ws.read("proj/other/c.txt") == "foo elsewhere\n"


def test_replace_all_in_all_roots_writes_every_match(ltt, ws):
    ws.write("projA/a.txt", "foo A\n")
    ws.write("projB/b.txt", "foo B foo\n")
    app = ltt(ws.path("projA"), ws.path("projB"))
    app.press("alt+r", "ctrl+f")
    search(app, "foo", 3, scope="all folders")
    app.press("tab")
    app.type("bar")
    app.press("alt+a")
    assert "3 occurrences in 2 files in all folders" in confirm(app).status
    s = app.press("r")
    assert s.status == "Written: 3 replacements in 2 files"
    assert ws.read("projA/a.txt") == "bar A\n"
    assert ws.read("projB/b.txt") == "bar B bar\n"


def test_project_replace_does_not_save_a_dirty_tab(ltt, ws):
    ws.write("proj/a.txt", "disk foo\n")
    ws.write("proj/b.txt", "foo b\n")
    app = ltt(ws.path("proj/a.txt"))
    app.type("unsaved\n")
    app.press("alt+r", "ctrl+f")
    search(app, "foo", 2, scope="all folders")
    app.press("tab")
    app.type("bar")
    app.press("alt+a")
    app.wait(lambda s: s.mode == "CONFIRM", what="project replacement confirmation")
    s = app.press("r")
    assert "unsaved tabs: 1 replacements in 1" in s.status
    assert s.title_dirty and s.editor_lines[:2] == ["unsaved", "disk bar"]
    assert ws.read("proj/a.txt") == "disk foo\n"
    assert ws.read("proj/b.txt") == "bar b\n"


def test_a_file_changed_after_the_review_is_skipped(ltt, ws):
    ws.write("proj/a.txt", "foo a\n")
    ws.write("proj/b.txt", "foo b\n")
    app = ltt(ws.path("proj"))
    begin_replace(app, "foo", 2)
    app.type("bar")
    app.press("alt+a")
    confirm(app)
    ws.write("proj/b.txt", "agent foo\n")
    s = app.press("r")
    assert "skipped: 1" in s.status
    assert ws.read("proj/a.txt") == "bar a\n"
    assert ws.read("proj/b.txt") == "agent foo\n"


def test_alt_d_chooses_a_folder_without_losing_the_query(ltt, ws):
    ws.write("proj/a.txt", "foo outside\n")
    ws.write("proj/sub/b.txt", "foo inside foo\n")
    app = ltt(ws.path("proj"))
    app.press("alt+r")
    search(app, "foo", 3)
    app.press("alt+d", "ctrl+u")
    app.type(ws.path("proj/sub"))
    app.press("enter")
    s = wait_found(app, "foo", 2, scope="folder")
    assert [h.location for h in s.results] == ["b.txt:1", "b.txt:1"]
