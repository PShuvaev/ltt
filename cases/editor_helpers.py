"""Helpers for the editor tests: open a text, press keys in a burst, save it back, read cells and colours."""
import base64
import os
import re

from harness import keys as K

# Characters made of several UTF-16 units or code points: the editor must treat each as one (D5, V8).
CLUSTERS = {
    "emoji": "👀",  # a surrogate pair
    "skin-tone": "👍🏽",  # an emoji and its modifier
    "zwj-family": "👨\u200d👩\u200d👧",  # emoji joined by ZWJ
    "flag": "🇺🇸",  # two regional indicators
    "combining-accent": "e\u0301",  # e and a combining acute
    "variation-selector": "\u2764\ufe0f",  # a heart and VS16
    "hangul-jamo": "\u1112\u1161\u11ab",  # one syllable from its three jamo
}


def open_text(ltt, ws, text, name="a.txt", **kw):
    """Write text to `name` and start ltt on it: the file is open, focus in the editor."""
    ws.write(name, text)
    app = ltt(ws.path(name), **kw)
    app.wait_mode("EDITOR")
    return app


def burst(app, *keys):
    """Keys in one write, as key repeat delivers them; returns the frame drawn after them.

    At most 64: ltt handles that many queued keys before it draws, so one frame shows them all.
    """
    assert len(keys) <= 64 and not any(K.is_lone_esc(K.encode(k)) for k in keys)
    app.write(b"".join(K.encode(k, app.keyboard) for k in keys))
    return app.settle()


def save(app, ws, name="a.txt"):
    """Ctrl+S, then the file's text as it is on disk."""
    app.press("ctrl+s")
    app.wait_status(f"Saved: {os.path.basename(name)}")
    return ws.read(name)


def mark_cursor(app, ws, name="a.txt", marker="|"):
    """Type a marker at the cursor and save: the text shows exactly where the cursor was."""
    app.type(marker)
    return save(app, ws, name)


def selection(app):
    """The selected text, or None: Ctrl+C copies it, and ltt sends every copy to the terminal too (OSC 52)."""
    mark = app.output_mark()
    app.press("ctrl+c")
    found = re.findall(rb"\x1b\]52;c;([A-Za-z0-9+/=]*)\x07", app.output_since(mark))
    return base64.b64decode(found[-1]).decode("utf-8") if found else None


def line_col(s):
    """(line, column) of the cursor as the status line shows them."""
    return s.pos[0], s.pos[2]


def row_y(s, line):
    """Screen row of editor line number `line` (it must be in view)."""
    row = s.editor_row(line)
    assert row is not None, f"line {line} is not on screen\n{s.dump()}"
    return row.y


def fg(s, needle, line=1):
    """Colour of the cells showing needle (ASCII, no tabs) in editor line `line`: one name, or 'mixed [...]'."""
    row = s.editor_row(line)
    assert row is not None, f"line {line} is not on screen\n{s.dump()}"
    i = row.text.find(needle)
    assert i >= 0, f"{needle!r} not in line {line}: {row.text!r}"
    colours = [s.fg(s.text_x + i + k, row.y) for k in range(len(needle))]
    return colours[0] if len(set(colours)) == 1 else f"mixed {colours}"


def line_colours(s, line):
    """The set of colours of the text cells of editor line `line` (ASCII text)."""
    row = s.editor_row(line)
    assert row is not None, f"line {line} is not on screen\n{s.dump()}"
    return {s.fg(s.text_x + k, row.y) for k in range(len(row.text))}
