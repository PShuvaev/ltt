"""The basics every other test relies on: start, show a folder, open, edit, save, quit."""


def test_starts_in_a_folder_and_quits(ltt, ws):
    ws.write("proj/a.txt", "alpha\n")
    app = ltt(ws.path("proj"))
    s = app.snap()
    assert s.mode == "TREE"
    assert s.title.startswith("ltt · ")
    assert s.tree_names[:2] == ["proj", "a.txt"]
    assert s.tabs == []
    assert app.quit() == 0


def test_open_edit_save(ltt, ws):
    ws.write("proj/a.txt", "alpha\nbeta\n")
    app = ltt(ws.path("proj"))
    app.press("down", "enter")
    s = app.wait(lambda s: s.editor_lines[:2] == ["alpha", "beta"], what="a.txt open")
    assert s.tab_names == ["a.txt"]
    assert s.mode == "EDITOR"  # Enter on a file opens it and moves to the editor
    app.type("X")
    s = app.wait(lambda s: s.editor_lines[0] == "Xalpha" and s.title_dirty, what="edited")
    assert s.tabs[0].dirty
    app.press("ctrl+s")
    app.wait_status("Saved: a.txt")
    assert ws.read("proj/a.txt") == "Xalpha\nbeta\n"
    assert app.quit() == 0
