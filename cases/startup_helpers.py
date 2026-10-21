"""Helpers for test_startup.py, test_terminal.py and test_input.py: terminal modes, raw output, termios."""
import os
import re
import signal
import subprocess
import termios

# What ltt writes to set the terminal up (after the runtime's own ESC[?1h ESC=):
# alternate screen, hidden cursor, mouse 1000/1002/1006, bracketed paste, kitty flags 1|4|8|16.
ENTER = b"\x1b[?1049h\x1b[?25l\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[>29u"
# ... and to put it back: kitty pop, paste and mouse off, colours reset, cursor shown,
# alternate screen off, normal cursor keys and keypad (V14).
LEAVE = b"\x1b[<u\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l\x1b[0m\x1b[?25h\x1b[?1049l\x1b[?1l\x1b>"
FRAME_START, FRAME_END = b"\x1b[?2026h", b"\x1b[?2026l"

# Every sequence ltt itself writes: cursor moves, colours, clear, private modes, kitty push/pop,
# keypad modes, OSC 52 with base64 (a copy).
_OWN = re.compile(rb"\x1b(?:\[\d+;\d+H|\[\d+;\d+m|\[0m|\[2J|\[\?(?:2026|25|1049|1000|1002|1006|2004|1)[hl]"
                  rb"|\[>29u|\[<u|=|>|\]52;c;[A-Za-z0-9+/=]*\x07)")


def session_output(app):
    """ltt's output up to the end of the session (the note after it may hold line breaks)."""
    out = app.output
    i = out.find(LEAVE)
    return out if i < 0 else out[:i + len(LEAVE)]


def foreign_controls(data):
    """What in the output is not ltt's own: other escape sequences, control bytes, C1 code points,
    bytes that are not UTF-8. Empty when nothing from a file reached the terminal raw (D12)."""
    found = []
    i = 0
    while (j := data.find(b"\x1b", i)) >= 0:
        m = _OWN.match(data, j)
        if m:
            i = m.end()
        else:
            found.append(data[j:j + 24])
            i = j + 1
    rest = _OWN.sub(b"", data)
    found += [bytes([b]) for b in rest if b < 0x20 or b == 0x7F]
    try:
        text = rest.decode("utf-8")
    except UnicodeDecodeError as e:
        found.append(b"not UTF-8: " + rest[max(0, e.start - 8):e.end + 8])
        text = rest.decode("utf-8", "replace")
    found += [ch.encode() for ch in text if 0x80 <= ord(ch) <= 0x9F]
    return found


def fresh_termios():
    """The settings of a new pseudo-terminal: what the harness's terminal had before ltt started."""
    master, slave = os.openpty()
    try:
        return termios.tcgetattr(slave)
    finally:
        os.close(master)
        os.close(slave)


def is_raw(attrs):
    return not attrs[3] & (termios.ICANON | termios.ECHO | termios.ISIG) and not attrs[0] & termios.IXON


def shell_status(code):
    """The exit code as a shell shows it ($?): a process killed by signal n reads 128 + n."""
    return 128 - code if code < 0 else code


SIGNALS = {"SIGTERM": signal.SIGTERM, "SIGINT": signal.SIGINT, "SIGHUP": signal.SIGHUP}


def run_plain(tools, ws, args, stdin, stdout, cwd=None):
    """ltt without the pty harness (redirected input or output); stderr captured."""
    return subprocess.run([tools.dotnet, tools.dll, *args], cwd=cwd or ws.root, env=ws.env(),
                          stdin=stdin, stdout=stdout, stderr=subprocess.PIPE, timeout=60)
