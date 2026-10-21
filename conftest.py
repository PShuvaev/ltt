"""Fixtures for every test: `ws` — a sealed-off workspace, `ltt` — start ltt in a pty inside it."""
import os
import sys

import pytest

from harness import build
from harness.term import Ltt
from harness.workspace import Tools, Workspace


def pytest_addoption(parser):
    g = parser.getgroup("ltt")
    g.addoption("--ltt-src", help="the ltt checkout to test (default: the worktree on main of this repository)")
    g.addoption("--ltt-dll", help="test this built ltt.dll as it is, without building")


def pytest_configure(config):
    # Build once, in the main process; xdist workers inherit LTT_DLL.
    if os.environ.get("PYTEST_XDIST_WORKER") or os.environ.get("LTT_DLL"):
        return
    dll = config.getoption("--ltt-dll")
    if not dll:
        src = build.find_source(config.getoption("--ltt-src"))
        sys.stderr.write(f"ltt tests: building {src}\n")
        dll = build.build(src)
        os.environ["LTT_SRC"] = src
    os.environ["LTT_DLL"] = os.path.abspath(dll)


@pytest.fixture(scope="session")
def tools():
    dll = os.environ.get("LTT_DLL")
    if not dll or not os.path.isfile(dll):
        pytest.exit(f"no ltt.dll to test (LTT_DLL={dll!r})", returncode=2)
    return Tools(dll)


@pytest.fixture
def ws(tmp_path, tools):
    """A workspace: files, git repositories, HOME, recovery folder, clipboard (see harness/workspace.py)."""
    return Workspace(tmp_path, tools)


@pytest.fixture
def ltt(ws, tools):
    """Start ltt: ltt(*args, cwd=None, cols=100, rows=30, env=None, keyboard="legacy", ready=True, path=None).

    args are passed as they are (paths: use ws.path(...)); cwd defaults to the
    workspace root; env adds to (None removes from) the sealed environment.
    Every session is killed at the end of the test if still running, and a
    test fails if ltt showed an internal error or crashed.
    """
    sessions = []

    def start(*args, cwd=None, cols=100, rows=30, env=None, keyboard="legacy", ready=True, path=None):
        e = ws.env(env, path=path)
        app = Ltt(tools.dotnet, tools.dll, args, cwd or ws.root, e, cols=cols, rows=rows,
                  keyboard=keyboard, ready=False)
        sessions.append(app)
        if ready:
            app.wait_ready()
        return app

    yield start
    problems = []
    for app in sessions:
        app.close()
        problems += app.problems
        if b"ltt crashed:" in app.output:
            problems.append("ltt crashed: " + app.output_text.split("ltt crashed:", 1)[1][:300])
    if problems:
        pytest.fail("ltt reported problems:\n" + "\n".join(problems), pytrace=False)
