"""Shared bits of the keyboard, mouse and change-highlighting tests."""

DOWN, UP = b"\x1b[B", b"\x1b[A"


def select(app, name):
    """Move the tree selection (the tree has focus) to the visible row of that name; returns the frame."""
    s = app.snap()
    for _ in range(3):
        target, sel = s.tree_row(name), s.tree_selected
        if target is None:
            raise AssertionError(f"{name!r} not in the tree:\n{s.dump()}")
        if sel is not None and sel.y == target.y:
            return s
        steps = target.y - sel.y if sel is not None else target.y - 1
        app.write((DOWN if steps > 0 else UP) * abs(steps))
        s = app.settle()
    raise AssertionError(f"could not select {name!r}:\n{s.dump()}")


def expand(app, *names):
    """Expand these folders one after another (each must be visible once the previous one is open)."""
    for name in names:
        select(app, name)
        app.press("right")
        app.wait(lambda s: s.tree_row(name).expanded, what=f"{name} expanded")


def open_in_tree(app, name):
    """Select a file in the tree and open it with Enter (focus goes to the editor)."""
    select(app, name)
    app.press("enter")
    return app.wait(lambda s: s.active_tab is not None and s.active_tab.name == name and s.mode == "EDITOR",
                    what=f"{name} open in the editor")


def edit(ltt, ws, text="hello world\n", name="proj/a.txt", **kw):
    """ltt with one file open, the editor focused."""
    ws.write(name, text)
    app = ltt(ws.path(name), **kw)
    app.wait_mode("EDITOR")
    return app


def pos(app):
    """(line, column) of the cursor, 1-based, as the status line shows it."""
    p = app.snap().pos
    return p[0], p[2]
