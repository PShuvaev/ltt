"""Big shared data and measuring helpers for test_perf.py and test_scale.py.

The data is made once through harness.shared (kept in .build/fixtures) and is
never changed by a test. .build lies inside this repository, so a session on
data that is not a repository of its own gets PLAIN (GIT_CEILING_DIRECTORIES):
git must not take it for ignored files of the tests' repository.
"""
import os
import re
import subprocess
import time

import pyte

from harness import build, keys as K, shared
from harness.screen import Cell, Snap, color_name
from harness.term import FRAME_END, TIME_SCALE

FIXTURES = os.path.realpath(os.path.join(build.BUILD, "fixtures"))
PLAIN = {"GIT_CEILING_DIRECTORIES": FIXTURES}


def secs(limit):
    """A time limit, stretched like every timeout of the harness (LTT_TEST_TIME_SCALE)."""
    return limit * TIME_SCALE


# ---------------------------------------------------------------- a million lines

MILLION = 1_000_000
ZEBRAS = (250_000, 500_000, 750_000, 1_000_000)  # the only lines of million.js with a "z"


def million_line(n):
    """Line n (1-based) of million.js: every line distinct, ~48 characters, coloured as code."""
    tail = "zebra crossing" if n in ZEBRAS else "some text here"
    return f'let v{n:07d} = "line {n:07d}"; // {tail}'


def million_js():
    """A 1,000,000-line JavaScript file (~47 MB) without a final line break."""
    with shared("perf-million") as (path, ready):
        f = os.path.join(path, "million.js")
        if not ready:
            with open(f, "w") as out:
                out.writelines(million_line(n) + "\n" for n in range(1, MILLION))
                out.write(million_line(MILLION))
    return f


LOG_LINES = ("INFO request served in 5 ms", "INFO cache hit for the user page", "WARN slow query took 250 ms")


def million_log():
    """log.txt: 1,000,000 lines that repeat three messages (~30 MB) — the hard case for a line diff."""
    with shared("perf-log") as (path, ready):
        f = os.path.join(path, "log.txt")
        if not ready:
            with open(f, "w") as out:
                out.writelines(LOG_LINES[n % 3] + "\n" for n in range(MILLION))
    return f


# ---------------------------------------------------------------- a 3 MB line

MINIFIED_UNIT = 'function a(b){return b*2/*x*/+"s"}var c=1;'
MINIFIED_LEN = len(MINIFIED_UNIT) * (3_000_000 // len(MINIFIED_UNIT) + 1)  # 3,000,018
SHORT_LINE = 'var short = "coloured"; // a line of normal length'


def minified_js():
    """min.js: one 3 MB line of minified code, then a short line."""
    with shared("perf-minified") as (path, ready):
        f = os.path.join(path, "min.js")
        if not ready:
            with open(f, "w") as out:
                out.write(MINIFIED_UNIT * (MINIFIED_LEN // len(MINIFIED_UNIT)) + "\n" + SHORT_LINE + "\n")
    return f


# ---------------------------------------------------------------- many files

TREE_FILES = 20_000
NESTED_DIRS, NESTED_PER_DIR = 100, 200
NEEDLES = {  # the only lines of perf-tree with a "z" or a "q"
    "nested/d007/f01400.txt": "zqx needle alpha",
    "nested/d050/f10000.txt": "zqx needle beta",
    "nested/d099/f19999.txt": "zqy needle gamma",
}
FILLER = ("the brown cat sits on a warm mat near the old door\n"
          "and every line of this file reads much the same way\n") * 10


def tree():
    """perf-tree: flat/ — 20,000 files of one size in one folder (f00000.txt … f19999.txt);
    nested/ — 100 folders × 200 files of ~1 KB ("brown cat" in every one)."""
    with shared("perf-tree") as (path, ready):
        if not ready:
            flat = os.path.join(path, "flat")
            os.makedirs(flat)
            for i in range(TREE_FILES):
                with open(os.path.join(flat, f"f{i:05d}.txt"), "w") as f:
                    f.write(f"file {i:05d}\nthe brown cat sits on a warm mat\n")
            for d in range(NESTED_DIRS):
                os.makedirs(os.path.join(path, "nested", f"d{d:03d}"))
                for j in range(NESTED_PER_DIR):
                    i = d * NESTED_PER_DIR + j
                    rel = f"nested/d{d:03d}/f{i:05d}.txt"
                    with open(os.path.join(path, rel), "w") as f:
                        f.write(f"file {i:05d} in folder d{d:03d}\n" + FILLER + NEEDLES.get(rel, "nothing special") + "\n")
    return path


# ---------------------------------------------------------------- a big repository

REPO_DIRS, REPO_PER_DIR, REPO_IGNORED = 50, 100, 2_000


def repo_source(d, j):
    body = "".join(f"    public int Value{k}() => {d * 1000 + j * 10 + k}; // computes the value number {k}\n"
                   for k in range(20))
    return f"namespace Big.Module{d:02d};\n\npublic sealed class Item{j:03d}\n{{\n{body}}}\n"


def big_repo(ws):
    """perf-repo: a committed git repository — src/ with 50 × 100 C# files (~1.3 KB each),
    node_modules/ with 2,000 files git ignores, a README. Since the commit
    src/module00/Item000.cs has grown (the second biggest file there) and
    src/module00/Untracked.cs (the biggest) is new."""
    with shared("perf-repo") as (path, ready):
        if not ready:
            for d in range(REPO_DIRS):
                folder = os.path.join(path, "src", f"module{d:02d}")
                os.makedirs(folder)
                for j in range(REPO_PER_DIR):
                    with open(os.path.join(folder, f"Item{j:03d}.cs"), "w") as f:
                        f.write(repo_source(d, j))
            for i in range(REPO_IGNORED):
                folder = os.path.join(path, "node_modules", f"pkg{i // 100:02d}")
                os.makedirs(folder, exist_ok=True)
                with open(os.path.join(folder, f"index{i:04d}.js"), "w") as f:
                    f.write(f"module.exports = {i}; // a dependency, ignored by git\n")
            with open(os.path.join(path, ".gitignore"), "w") as f:
                f.write("node_modules/\n")
            with open(os.path.join(path, "README.md"), "w") as f:
                f.write("# A big repository\n\nNothing to see here: it is big on purpose.\n")
            time.sleep(1.1)  # the index must be newer than every file: no "racily clean" entries to re-hash
            ws.git("init", "-q", "-b", "main", cwd=path)
            ws.git("add", "-A", cwd=path)
            ws.git("commit", "-q", "-m", "initial", cwd=path)
            ws.git("gc", "-q", cwd=path)
            with open(os.path.join(path, "src", "module00", "Item000.cs"), "a") as f:
                f.write("// changed since the commit\n" * 80)
            with open(os.path.join(path, "src", "module00", "Untracked.cs"), "w") as f:
                f.write("// not in git yet\n" * 250)
    return path


# ---------------------------------------------------------------- measuring

def first_frame(app):
    """The first frame ltt drew, read back from its output (later frames may have replaced it)."""
    app.wait_ready()
    raw = app.output
    screen = pyte.Screen(app.cols, app.rows)
    pyte.ByteStream(screen).feed(raw[:raw.index(FRAME_END) + len(FRAME_END)])
    cells = [[Cell(c.data, color_name(c.fg), color_name(c.bg)) for c in (screen.buffer[y][x] for x in range(app.cols))]
             for y in range(app.rows)]
    with app._lock:
        at = app._frame_times[0]
    return Snap(cells, (screen.cursor.x, screen.cursor.y), screen.cursor.hidden, screen.title, 1, at)


def first_frame_seconds(app):
    """From starting the process to its first complete frame."""
    return first_frame(app).at - app.started_at


def quickest_start(ltt, *args, limit, tries=5, **kw):
    """Start ltt until a first frame comes within limit, at most `tries` times; returns (the
    quickest time to a first frame, that session). A busy machine slows some starts: before
    its first frame ltt starts file watchers, and on macOS each of them runs a system-wide sync()."""
    best = None
    for _ in range(tries):
        app = ltt(*args, **kw)
        t = first_frame_seconds(app)
        if best is not None and t >= best[0]:
            app.close()
            continue
        if best is not None:
            best[1].close()
        best = (t, app)
        if t < limit:
            break
    return best


def answer(app, key, pred=lambda s: True, timeout=10.0):
    """Send a key (a spec or raw bytes); returns (seconds until the first frame after it for
    which pred holds, that frame)."""
    n = app.frames
    data = K.encode(key, app.keyboard)
    t0 = time.monotonic()
    app.write(data)
    s = app.wait(lambda s: s.frame_no > n and pred(s), timeout, f"the answer to {key!r}")
    return s.at - t0, s


def sent(app, key, pred=lambda s: True):
    """Every byte ltt wrote to answer one key (until it went quiet)."""
    n = app.frames
    mark = app.output_mark()
    app.write(K.encode(key, app.keyboard))
    app.wait(lambda s: s.frame_no > n and pred(s), what=f"the frame for {key!r}")
    app.settle()
    return app.output_since(mark)


_RUN = re.compile(rb"\x1b\[(\d+);(\d+)H(?!\x1b\[\?25h)")  # a run of changed cells (not the final cursor move)


def runs(data):
    """(x, y), 0-based, where each run of changed cells in the output starts."""
    return [(int(x) - 1, int(y) - 1) for y, x in _RUN.findall(data)]


def rss_mb(pid):
    """Resident memory of a process, in MB."""
    out = subprocess.run(["ps", "-o", "rss=", "-p", str(pid)], capture_output=True, text=True).stdout
    return int(out.strip()) / 1024
