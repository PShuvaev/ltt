"""Helpers for the copy/move tests: tree navigation by path, file snapshots, a second disk, a big shared folder."""
import os
import platform
import shutil
import subprocess
import time

import pytest

from harness import shared

NOTHING_PICKED = ("Ctrl+V here pastes a file or folder picked up with Ctrl+C or Ctrl+X; "
                  "text goes into the editor (Tab).")
WIDE = 320  # columns for statuses that carry a full temporary path


def rows(s):
    """{'root/sub/name': TreeRow} for the visible tree rows; paths start at the root's name."""
    out, stack = {}, []
    for r in s.tree:
        del stack[r.depth:]
        stack.append(r.name)
        out.setdefault("/".join(stack), r)
    return out


def _move_to(app, want):
    s = app.snap()
    for _ in range(100):
        assert s.mode == "TREE", f"select({want!r}) needs the tree focused\n{s.dump()}"
        here = rows(s)
        assert want in here, f"{want!r} is not in the tree\n{s.dump()}"
        sel = s.tree_selected
        if sel is not None and sel.y == here[want].y:
            return s
        s = app.press("down" if sel is None or here[want].y > sel.y else "up")
    raise AssertionError(f"could not select {want!r}\n{s.dump()}")


def select(app, path):
    """Select the tree row `path` ('proj/a/notes.txt', from the root's name) with the arrow keys,
    expanding the folders on the way with →. Returns the frame."""
    parts = path.split("/")
    s = None
    for i in range(1, len(parts) + 1):
        want = "/".join(parts[:i])
        s = _move_to(app, want)
        if i < len(parts) and not rows(s)[want].expanded:
            s = app.press("right")
    return s


def status_is(app, text, timeout=10.0):
    """Wait for exactly this status message."""
    return app.wait(lambda s: s.status == text, timeout, f"status {text!r}")


def contents(base):
    """{relative path: bytes} of every regular file below base (links are not followed)."""
    out = {}
    for d, _, files in os.walk(base):
        for f in files:
            p = os.path.join(d, f)
            if not os.path.islink(p):
                with open(p, "rb") as fh:
                    out[os.path.relpath(p, base)] = fh.read()
    return out


def count_files(base):
    return sum(len(files) for _, _, files in os.walk(base))


needs_unix_permissions = pytest.mark.skipif(
    hasattr(os, "geteuid") and os.geteuid() == 0, reason="permissions do not stop root")


# ---- a second disk: an HFS+ RAM disk mounted inside the workspace (macOS, no root needed) ----

def _run(*cmd):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=60)


@pytest.fixture
def other_disk(ws):
    """A folder on another file system ('disk/C' in the workspace), or the test is skipped.

    Request it before `ltt` in the test's arguments, so that ltt is stopped
    before the disk goes away.
    """
    if platform.system() != "Darwin" or not all(shutil.which(t) for t in ("hdiutil", "newfs_hfs", "diskutil")):
        pytest.skip("a second disk needs macOS (hdiutil, newfs_hfs, diskutil)")
    r = _run("hdiutil", "attach", "-nomount", "ram://262144")  # 128 MB
    if r.returncode != 0 or not r.stdout.split():
        pytest.skip(f"cannot make a RAM disk: {r.stderr.strip()}")
    dev = r.stdout.split()[0]
    try:
        mount = ws.mkdir("disk")
        for cmd in (("newfs_hfs", "-v", "LttTests", dev), ("diskutil", "mount", "-mountPoint", mount, dev)):
            r = _run(*cmd)
            if r.returncode != 0:
                pytest.skip(f"cannot prepare the RAM disk ({cmd[0]}): {r.stderr.strip()}")
        assert os.stat(mount).st_dev != os.stat(ws.root).st_dev, "the RAM disk is not another file system"
        folder = os.path.join(mount, "C")
        os.mkdir(folder)
        yield folder
    finally:
        for attempt in range(5):
            r = _run("hdiutil", "detach", *(["-force"] if attempt else []), dev)
            if r.returncode == 0:
                break
            time.sleep(0.5)  # busy for a moment after ltt stopped: try again
        else:
            raise RuntimeError(f"could not detach the RAM disk {dev}: {r.stderr.strip()}")


# ---- a big folder, made once ----

BIG_DIRS, BIG_FILES = 100, 200  # 20,000 small files


def big_folder():
    """The shared folder `big` of 20,000 small files (100 folders of 200). Read it, never change it."""
    with shared("copymove-big-20k") as (path, ready):
        big = os.path.join(path, "big")
        if not ready:
            for d in range(BIG_DIRS):
                sub = os.path.join(big, f"d{d:03}")
                os.makedirs(sub)
                for f in range(BIG_FILES):
                    with open(os.path.join(sub, f"f{f:03}.txt"), "w") as fh:
                        fh.write(f"folder {d} file {f}\n")
    return big
