"""Shared bits of the tree, roots and delete tests: moving the selection, every frame drawn, big fixtures."""
import os

import pyte

from harness import shared
from harness.screen import Cell, Snap, color_name
from harness.term import FRAME_END

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


def names(s):
    return [r.name for r in s.tree]


def frames(app):
    """Every complete frame ltt drew so far, replayed from its output (no resize in between)."""
    data = app.output
    screen = pyte.Screen(app.cols, app.rows)
    stream = pyte.ByteStream(screen)
    out, pos = [], 0
    while True:
        end = data.find(FRAME_END, pos)
        if end < 0:
            return out
        stream.feed(data[pos:end + len(FRAME_END)])
        pos = end + len(FRAME_END)
        cells = [[Cell(c.data, color_name(c.fg), color_name(c.bg)) for c in (screen.buffer[y][x] for x in range(screen.columns))]
                 for y in range(screen.lines)]
        out.append(Snap(cells, (screen.cursor.x, screen.cursor.y), screen.cursor.hidden, screen.title, len(out) + 1, 0))


def first_frame(app):
    return frames(app)[0]


def size_fixture():
    """bytes/many: 3000 one-byte files; empty/many: 3000 empty files — more than a first load counts."""
    with shared("tree-sizes") as (path, ready):
        if not ready:
            for kind, data in (("bytes", b"x"), ("empty", b"")):
                folder = os.path.join(path, kind, "many")
                os.makedirs(folder)
                for i in range(3000):
                    with open(os.path.join(folder, f"f{i:04d}"), "wb") as f:
                        f.write(data)
    return path


def burst_fixture():
    """burst/p0000/lib … burst/p2999/lib: thousands of folders to copy in at once."""
    with shared("tree-burst") as (path, ready):
        if not ready:
            for i in range(3000):
                os.makedirs(os.path.join(path, "burst", f"p{i:04d}", "lib"))
    return os.path.join(path, "burst")
