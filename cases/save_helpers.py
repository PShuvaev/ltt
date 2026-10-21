"""Helpers for the save, encoding, undo and clipboard tests."""
import contextlib
import os
import resource
import signal
import stat

from harness import keys as K


def save(app, name):
    """Ctrl+S, then the 'Saved: name' status; returns that frame."""
    app.press("ctrl+s")
    return app.wait_status(f"Saved: {name}")


def open_by_path(app, path):
    """Open a file with Ctrl+L (unlike a command-line file, this reports its format); returns the frame after."""
    app.press("ctrl+l")
    app.type(path)
    app.press("enter")
    return app.wait(lambda s: s.mode not in ("PROMPT", None), what="the go-to prompt closed")


def paste_bytes(app, data, settle=True):
    """A bracketed paste of exactly these bytes (the harness's paste() turns every line break into CR)."""
    app.write(K.PASTE_START + data + K.PASTE_END)
    return app.settle() if settle else None


def tool_dir(ws, name, **scripts):
    """A PATH folder (in ws.sys, out of ltt's tree) with the workspace's tools, some replaced by these scripts."""
    d = os.path.join(ws.sys, name)
    os.makedirs(d)
    for entry in os.listdir(ws.bin):
        if entry not in scripts:
            os.symlink(os.path.realpath(os.path.join(ws.bin, entry)), os.path.join(d, entry))
    for tool, body in scripts.items():
        p = os.path.join(d, tool)
        with open(p, "w") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(p, 0o755)
    return d


@contextlib.contextmanager
def file_size_limit(nbytes):
    """A process started inside inherits RLIMIT_FSIZE = nbytes: a write past it fails with EFBIG."""
    soft, hard = resource.getrlimit(resource.RLIMIT_FSIZE)
    old = signal.signal(signal.SIGXFSZ, signal.SIG_IGN)  # an error, not a kill (Python ignores it already)
    resource.setrlimit(resource.RLIMIT_FSIZE, (nbytes, hard))
    try:
        yield
    finally:
        resource.setrlimit(resource.RLIMIT_FSIZE, (soft, hard))
        signal.signal(signal.SIGXFSZ, old)


@contextlib.contextmanager
def read_only(path):
    """Take the write bits off a file or folder for the block (restored after, so the workspace can be removed)."""
    mode = stat.S_IMODE(os.stat(path).st_mode)
    os.chmod(path, mode & ~0o222)
    try:
        yield
    finally:
        os.chmod(path, mode)
