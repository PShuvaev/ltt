"""Shared bits of the prompt, tab and quit tests: the prompt line, its completion list, the prompts themselves."""


def prompt_line(s):
    """The prompt as typed: its label and input (white cells), without the grey ghost suggestion."""
    return "".join(c.ch for c in s.cells[s.status_y] if c.fg == "White").rstrip()


def ghost(s):
    """The grey suggestion drawn after the typed text of a prompt."""
    return "".join(c.ch for c in s.cells[s.status_y] if c.fg == "DarkGray")


def candidates(s):
    """The completion candidates listed in the help line (" Tab ▸ sub/  a.txt")."""
    line = s.lines[s.help_y]
    if not line.startswith(" Tab ▸ "):
        return []
    return [n for n in line[len(" Tab ▸ "):].rstrip().split("  ") if n]


def chosen(s):
    """The highlighted candidate in the help line, or ""."""
    return "".join(c.ch for c in s.cells[s.help_y] if c.bg == "DarkBlue")


def goto(app, text):
    """Ctrl+L, the text, Enter; returns the frame after it."""
    app.press("ctrl+l")
    app.type(text)
    return app.press("enter")


def create(app, text, folder=False):
    """Ctrl+N (Ctrl+Shift+N for a folder), the prefilled folder cleared, the text, Enter."""
    app.press("ctrl+shift+n" if folder else "ctrl+n", "ctrl+u")
    app.type(text)
    return app.press("enter")


def rename(app, name):
    """Ctrl+R, the prefilled name cleared, the new name, Enter."""
    app.press("ctrl+r", "ctrl+u")
    app.type(name)
    return app.press("enter")


def select_in_tree(app, name):
    """Move the tree selection (the tree has focus) down from the top to the first row named so."""
    app.press("home")
    for _ in range(len(app.snap().tree)):
        if app.snap().tree_selected.name == name:
            return app.snap()
        app.press("down")
    raise AssertionError(f"{name!r} not in the tree:\n{app.snap().dump()}")
