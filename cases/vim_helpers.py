"""Syntax colours come from vim: the real vim put on ltt's PATH, a language of the tests' own in ~/.vim
(*.ltttest), and fake vims that hang, die or print junk."""
import os
import shutil
import subprocess
import time

import pytest


def _find_vim():
    """A vim that can colour: +syntax and +eval (vim-tiny cannot), or None."""
    seen = []
    for d in os.environ.get("PATH", "").split(os.pathsep) + ["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"]:
        p = shutil.which("vim", path=d) if d else None
        if not p or os.path.realpath(p) in seen:
            continue
        seen.append(os.path.realpath(p))
        try:
            out = subprocess.run([p, "--version"], capture_output=True, text=True, timeout=10).stdout
        except (OSError, subprocess.SubprocessError):
            continue
        if "+syntax" in out and "+eval" in out:
            return os.path.realpath(p)
    return None


VIM = _find_vim()
needs_vim = pytest.mark.skipif(VIM is None, reason="no vim with +syntax and +eval on this machine")

# vim's standard highlight groups and the colour ltt draws each in: the first standard name on a
# group's chain of links decides (Operator links to Statement, but an operator stays plain).
GROUP_COLOURS = {
    "Comment": "DarkGreen", "SpecialComment": "DarkGreen", "Todo": "DarkGreen",
    "String": "Yellow", "Character": "Yellow", "Special": "Yellow", "SpecialChar": "Yellow",
    "Constant": "Magenta", "Number": "Magenta", "Float": "Magenta", "Boolean": "Magenta",
    "Statement": "Cyan", "Conditional": "Cyan", "Repeat": "Cyan", "Label": "Cyan", "Keyword": "Cyan",
    "Exception": "Cyan", "Type": "Cyan", "StorageClass": "Cyan", "Structure": "Cyan", "Typedef": "Cyan",
    "Title": "Cyan",
    "PreProc": "DarkCyan", "Include": "DarkCyan", "Define": "DarkCyan", "Macro": "DarkCyan", "PreCondit": "DarkCyan",
    "Added": "Green", "Changed": "Yellow", "Removed": "Red",
    "Identifier": "Gray", "Function": "Gray", "Operator": "Gray", "Delimiter": "Gray", "Tag": "Gray",
    "Debug": "Gray", "Underlined": "Gray", "Ignore": "Gray", "Error": "Gray",
}

# The tests' language: a word for every standard group (kComment, kString…), keywords alpha and beta,
# the type gamma, numbers, "strings" with \ escapes, -- line comments, {- block comments -} and
# @directives at a line start. It is parsed from the start of the file, so what vim says is exact.
SYNTAX = r'''
if exists("b:current_syntax")
  finish
endif
for s:group in [GROUPS]
  execute 'syntax keyword ltt' . s:group . ' k' . s:group
  execute 'highlight default link ltt' . s:group . ' ' . s:group
endfor
syntax keyword lttOwn kOwn
highlight lttOwn ctermfg=Red guifg=Red
syntax keyword lttDeep kDeep
highlight default link lttDeep lttDeeper
highlight default link lttDeeper String
syntax keyword lttWord alpha beta
syntax keyword lttTypeWord gamma
syntax match lttNumber "\<\d\+\>"
syntax region lttString start=+"+ skip=+\\\\\|\\"+ end=+"+ contains=lttEscape
syntax match lttEscape +\\.+ contained
syntax region lttBlock start="{-" end="-}"
syntax match lttLineComment "--.*$"
syntax match lttDirective "^@\w\+"
highlight default link lttWord Statement
highlight default link lttTypeWord Type
highlight default link lttNumber Number
highlight default link lttString String
highlight default link lttEscape SpecialChar
highlight default link lttBlock Comment
highlight default link lttLineComment Comment
highlight default link lttDirective PreProc
syntax sync fromstart
let b:current_syntax = "ltttest"
'''.replace("[GROUPS]", "[" + ", ".join(f"'{g}'" for g in GROUP_COLOURS) + "]")


def install_syntax(ws, name, body, pattern):
    """A language in ws.home/.vim: syntax/<name>.vim and the files it is for (an autocmd pattern)."""
    vim_dir = os.path.join(ws.home, ".vim")
    for sub, text in (("syntax", body), ("ftdetect", f"autocmd BufNewFile,BufRead {pattern} setfiletype {name}\n")):
        os.makedirs(os.path.join(vim_dir, sub), exist_ok=True)
        with open(os.path.join(vim_dir, sub, name + ".vim"), "w") as f:
            f.write(text)


def with_vim(ws, syntax=True):
    """Put the real vim on ltt's PATH (ws.bin), and the tests' language in ~/.vim."""
    if VIM is None:
        pytest.skip("no vim with +syntax and +eval on this machine")
    link = os.path.join(ws.bin, "vim")
    if not os.path.lexists(link):
        os.symlink(VIM, link)
    if syntax:
        install_syntax(ws, "ltttest", SYNTAX, "*.ltttest")
    return ws


def fake_vim(ws, body):
    """A `vim` on ltt's PATH that is a shell script; $REAL_VIM is the real one (may be empty), $PIDS a
    file of the pids that ran. Returns the PIDS path."""
    pids = os.path.join(ws.sys, "vim-pids")
    path = os.path.join(ws.bin, "vim")
    if os.path.lexists(path):
        os.remove(path)
    with open(path, "w") as f:
        f.write(f"#!/bin/sh\nREAL_VIM='{VIM or ''}'\nPIDS='{pids}'\necho $$ >> \"$PIDS\"\n{body}\n")
    os.chmod(path, 0o755)
    return pids


def logging_vim(ws):
    """The real vim behind a script that notes its pid (the script execs vim: the pid stays)."""
    with_vim(ws)
    return fake_vim(ws, 'exec "$REAL_VIM" "$@"')


def slow_vim(ws, delay=0.5):
    """The real vim whose every line of output reaches ltt `delay` seconds late: what ltt draws before
    vim answers shows."""
    with_vim(ws)
    return fake_vim(ws, f'"$REAL_VIM" "$@" | while IFS= read -r line; do sleep {delay}; printf "%s\\n" "$line"; done')


def pids_of(pids_file):
    try:
        with open(pids_file) as f:
            return [int(x) for x in f.read().split()]
    except FileNotFoundError:
        return []


def running(pid):
    """Whether the process is still there (a zombie counts as gone)."""
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    try:
        out = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True).stdout
    except OSError:
        return True
    return out.strip() != "" and not out.strip().startswith("Z")


def wait_gone(pids, timeout=5.0):
    """The pids that are still running after waiting up to timeout seconds."""
    deadline = time.monotonic() + timeout
    while True:
        alive = [p for p in pids if running(p)]
        if not alive or time.monotonic() > deadline:
            return alive
        time.sleep(0.05)


def stop(pids):
    """Kill what a test's fake vim left behind (only those pids: our own children's children)."""
    for p in pids:
        try:
            os.kill(p, 9)
        except (ProcessLookupError, PermissionError):
            pass


def colour_of(s, needle, line=1):
    """The colour of needle in an editor line (any text: wide characters, marks), or None while the line
    or the text is not on screen; "mixed" for more than one."""
    row = s.editor_row(line)
    if row is None:
        return None
    cells = s.cells[row.y][s.text_x:]
    text, xs = "", []
    for x, c in enumerate(cells):
        xs += [x] * len(c.ch)
        text += c.ch
    i = text.find(needle)
    if i < 0:
        return None
    colours = {cells[xs[i + k]].fg for k in range(len(needle))}
    return colours.pop() if len(colours) == 1 else "mixed"


def wait_colours(app, expected, what=None, timeout=10.0):
    """Wait until each (needle, line) has its colour: {(needle, line): colour}; returns that frame."""
    def ok(s):
        return all(colour_of(s, needle, line) == colour for (needle, line), colour in expected.items())
    try:
        return app.wait(ok, timeout=timeout, what=what or f"colours {expected}")
    except Exception:
        s = app.snap()
        got = {k: colour_of(s, *k) for k in expected}
        raise AssertionError(f"colours never became {expected}; last frame has {got}\n{s.dump()}") from None


def wait_coloured(app, needle, colour, line=1, timeout=10.0):
    return wait_colours(app, {(needle, line): colour}, timeout=timeout)
