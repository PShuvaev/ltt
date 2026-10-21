"""Helpers for the search tests: the search input line, waiting for results, tree selection,
a git that holds the file listing of a folder search, and a big log file."""
import os
import re
import shutil
from collections import namedtuple

from harness import shared

# " Search folder: needle   · found: 3" — label, query, then a suffix after three spaces
_INPUT_RE = re.compile(r"^ Search (file|folder|all folders): (.*?)   · (.+?)\s*$")

Input = namedtuple("Input", "scope query suffix")


def search_input(s):
    """The search input as (scope, query, suffix), or None when it is not shown."""
    if s.mode != "PROMPT":
        return None
    m = _INPUT_RE.match(s.status_line)
    return Input(*m.groups()) if m else None


def found(s):
    """The count the input shows ("3", "500+"), or None while it shows something else."""
    i = search_input(s)
    return i.suffix[len("found: "):] if i and i.suffix.startswith("found: ") else None


def locations(s):
    """The visible result rows as "path:line"."""
    return [h.location for h in s.results]


def wait_found(app, query, n, scope=None, timeout=10.0):
    """Wait until the input shows "found: n" for exactly this query (n: 3 or "500+").

    Always name the count: right after a fast scan ltt can show the previous count
    for one tick (test_the_count_is_never_stale_after_a_fast_scan), so "any count"
    is not yet the answer — see final_results for that.
    """
    want = str(n)

    def done(s):
        i = search_input(s)
        return (i is not None and i.query == query and i.suffix == "found: " + want
                and (scope is None or i.scope == scope))
    return app.wait(done, timeout, f"found: {want} for {query!r}" + (f" in {scope}" if scope else ""))


def final_results(app, query, timeout=10.0):
    """The frame with the final results of this query, whatever their count.

    Once the input shows a count the scan is over; ltt takes in a finished scan
    before it draws the next key, so the frame after one key that does nothing
    in the input (Home) shows it.
    """
    def counted(s):
        i = search_input(s)
        return i is not None and i.query == query and i.suffix.startswith("found: ")
    app.wait(counted, timeout, f"a count for {query!r}")
    return app.press("home")


def search(app, query, n, scope=None, timeout=10.0):
    """Type a query into the open search input (as fast typing arrives) and wait for its results."""
    app.type(query)
    return wait_found(app, query, n, scope, timeout)


def select(app, name):
    """Move the tree selection to the visible entry with this name."""
    for _ in range(50):
        s = app.snap()
        names = s.tree_names
        if s.tree_selected and s.tree_selected.name == name:
            return s
        if name not in names:
            raise AssertionError(f"{name!r} is not in the tree: {names}")
        app.press("down" if names.index(name) > names.index(s.tree_selected.name) else "up")
    raise AssertionError(f"could not select {name!r}")


class GitGate:
    """A `git` that holds `git ls-files` (the file listing of a folder search) until open().

    hold="before": git waits before it lists anything, so the scan stays at its start;
    hold="after": git lists the files at once, then waits before it answers (a listing
    taken before whatever happens meanwhile). Other git commands run as usual.
    Start ltt with path=gate.path; gate.waiting tells that a listing is being held.
    """

    def __init__(self, ws, hold="before"):
        d = os.path.join(ws.sys, "gate")  # plumbing: outside every folder ltt shows
        bin_dir = os.path.join(d, "bin")
        os.makedirs(bin_dir, exist_ok=True)
        self._waiting = os.path.join(d, "waiting")
        self._open = os.path.join(d, "open")
        git = ws.tools.git
        sleep, cat, rm = (shutil.which(t) or f"/bin/{t}" for t in ("sleep", "cat", "rm"))
        # at most ~30 s, so a test that fails half-way never leaves a git behind for long
        hold_loop = (f"  : > '{self._waiting}'\n"
                     f"  i=0; while [ ! -e '{self._open}' ] && [ $i -lt 1500 ]; do {sleep} 0.02; i=$((i+1)); done\n")
        if hold == "before":
            body = f'if [ "$1" = "ls-files" ]; then\n{hold_loop}fi\nexec \'{git}\' "$@"\n'
        else:
            out = os.path.join(d, "listing.$$")
            body = (f'if [ "$1" = "ls-files" ]; then\n'
                    f'  \'{git}\' "$@" > "{out}"; rc=$?\n{hold_loop}'
                    f'  {cat} "{out}"; {rm} -f "{out}"; exit $rc\nfi\nexec \'{git}\' "$@"\n')
        script = os.path.join(bin_dir, "git")
        with open(script, "w") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(script, 0o755)
        self.path = bin_dir + os.pathsep + ws.bin

    @property
    def waiting(self):
        return os.path.exists(self._waiting)

    def open(self):
        open(self._open, "w").close()


BIG_LINES = 550_000  # lines of filler before the needle: about 41 MB


def big_log(ws, rel):
    """Copy a 41 MB log (over the 8 MB folder-search limit) to rel; its last line holds "zqxneedle"."""
    with shared("search-big-log") as (path, ready):
        src = os.path.join(path, "big.log")
        if not ready:
            line = "the quick brown fox jumps over the lazy dog while counting sheep 0123456789\n"
            with open(src, "w") as f:
                f.write(line * BIG_LINES)
                f.write("the zqxneedle is on the last line\n")
    dst = ws.path(rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)
    return dst
