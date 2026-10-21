"""Saving: Ctrl+S and the ● marks, byte-for-byte fidelity, line endings, permissions, links, special files, errors."""
import os
import shutil
import stat
import subprocess

import pytest

from save_helpers import file_size_limit, open_by_path, read_only, save


def test_ctrl_s_saves_and_clears_the_dirty_marks(ltt, ws):
    p = ws.write("a.txt", "alpha\n")
    app = ltt(p)
    app.type("X")
    app.wait(lambda s: s.title_dirty and s.tabs[0].dirty, what="● in the title and on the tab")
    assert ws.read("a.txt") == "alpha\n"  # nothing reaches the disk before Ctrl+S
    s = save(app, "a.txt")
    assert not s.title_dirty and not s.tabs[0].dirty and s.mode == "EDITOR"
    assert ws.read("a.txt") == "Xalpha\n"


def test_ctrl_s_in_the_tree_saves_the_active_tab(ltt, ws):
    p = ws.write("proj/a.txt", "alpha\n")
    app = ltt(p)
    app.type("X")
    app.press("esc")
    s = save(app, "a.txt")
    assert s.mode == "TREE" and not s.tabs[0].dirty
    assert ws.read("proj/a.txt") == "Xalpha\n"


def test_ctrl_s_saves_only_the_active_tab(ltt, ws):
    a, b = ws.write("a.txt", "a\n"), ws.write("b.txt", "b\n")
    app = ltt(a, b)
    app.type("1")
    app.press("ctrl+pgdn")
    app.wait(lambda s: s.active_tab.name == "b.txt")
    app.type("2")
    s = save(app, "b.txt")
    assert [(t.name, t.dirty) for t in s.tabs] == [("a.txt", True), ("b.txt", False)]
    assert ws.read("a.txt") == "a\n" and ws.read("b.txt") == "2b\n"


def test_ctrl_s_without_an_open_file(ltt, ws):
    ws.write("proj/a.txt", "a\n")
    app = ltt(ws.path("proj"))
    app.press("ctrl+s")
    assert app.wait_status("No file is open.").mode == "TREE"


# D1: what was not edited stays byte for byte as it was.
FIDELITY = [
    ("lf", b"one\ntwo\n"),
    ("crlf", b"one\r\ntwo\r\n"),
    ("cr", b"one\rtwo\r"),
    ("no_final_newline", b"one\ntwo"),
    ("crlf_no_final_newline", b"one\r\ntwo"),
    ("cr_no_final_newline", b"one\rtwo"),
    ("empty", b""),
    ("lone_lf", b"\n"),
    ("lone_crlf", b"\r\n"),
    ("lone_cr", b"\r"),
    ("blank_lines_at_the_end", b"text\n\n\n"),
    ("blank_lines_only", b"\n\n\n"),
    ("spaces_only", b"   "),
    ("trailing_spaces_and_tabs", b"a  \nb\t \n  \n\t\n"),
    ("tabs", b"all:\n\techo hi\n\t\tdeep\tinner\n"),
    ("long_line", b"x" * 100_000 + b"\n"),
    ("long_crlf_lines", (b"y" * 30_000 + b"\r\n") * 3),
    ("crlf_across_the_read_chunk", b"z" * 65_535 + b"\r\nend\r\n"),
    ("cr_across_the_read_chunk", b"z" * 65_535 + b"\rend\r"),
    ("unicode", "héllo 日本語 ⚡ 👀 e\u0301 \u200d\n".encode()),
    ("control_bytes", b"a\x1b[31mred\x1b[0m\x07\x0c\x7f\n"),
]


@pytest.mark.parametrize("data", [d for _, d in FIDELITY], ids=[n for n, _ in FIDELITY])
def test_open_and_save_keeps_every_byte(ltt, ws, data):  # D1
    p = ws.write("f.txt", data)
    inode = os.stat(p).st_ino
    app = ltt(p)
    assert not app.snap().tabs[0].dirty
    s = save(app, "f.txt")
    assert not s.tabs[0].dirty
    assert os.stat(p).st_ino != inode  # it was written again, not skipped
    assert ws.read_bytes("f.txt") == data


def test_mixed_line_endings_are_announced_and_saved_as_the_most_common(ltt, ws):
    p = ws.write("m.txt", b"a\r\nb\r\nc\nd\r\n")
    app = ltt(ws.root, cols=140)
    s = open_by_path(app, p)
    assert "(5 lines, mixed line endings, saved as CRLF)" in s.status
    save(app, "m.txt")
    assert ws.read_bytes("m.txt") == b"a\r\nb\r\nc\r\nd\r\n"


def test_typing_keeps_crlf(ltt, ws):  # fix/pty/t1.py #5
    p = ws.write("crlf.txt", b"one\r\ntwo\r\n")
    app = ltt(p)
    app.type("X")
    save(app, "crlf.txt")
    assert ws.read_bytes("crlf.txt") == b"Xone\r\ntwo\r\n"


@pytest.mark.parametrize("nl", [b"\r\n", b"\r", b"\n"], ids=["crlf", "cr", "lf"])
def test_a_new_line_takes_the_files_line_ending(ltt, ws, nl):
    p = ws.write("f.txt", b"one" + nl + b"two" + nl)
    app = ltt(p)
    app.press("end", "enter")
    app.type("mid")
    app.wait_editor(["one", "mid", "two"])
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == nl.join([b"one", b"mid", b"two", b""])


def test_an_edit_keeps_the_missing_final_newline(ltt, ws):
    p = ws.write("f.txt", b"one\ntwo")
    app = ltt(p)
    app.press("ctrl+end")
    app.type("!")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == b"one\ntwo!"


def test_an_edit_leaves_the_other_lines_byte_for_byte(ltt, ws):
    p = ws.write("f.txt", "first\r\n  trailing  \r\n\tтаб\t\r\n\r\nlast".encode())
    app = ltt(p)
    app.type("X")
    save(app, "f.txt")
    assert ws.read_bytes("f.txt") == "Xfirst\r\n  trailing  \r\n\tтаб\t\r\n\r\nlast".encode()


def test_a_makefile_keeps_its_tabs(ltt, ws):  # D2, fix/pty/t1.py #5
    p = ws.write("Makefile", b"all:\n\techo hi\n")
    app = ltt(p)
    app.press("down", "end", "enter", "tab")
    app.type("ls")
    save(app, "Makefile")
    assert ws.read_bytes("Makefile") == b"all:\n\techo hi\n\t\tls\n"


@pytest.mark.parametrize("mode", [0o600, 0o640, 0o755, 0o700])
def test_save_keeps_the_permissions(ltt, ws, mode):
    p = ws.write("run.sh", "echo hi\n", mode=mode)
    app = ltt(p)
    app.type("#")
    save(app, "run.sh")
    assert stat.S_IMODE(os.stat(p).st_mode) == mode
    assert ws.read("run.sh") == "#echo hi\n"


def test_save_replaces_the_file_in_one_rename(ltt, ws):
    p = ws.write("proj/a.txt", "one\n")
    inode = os.stat(p).st_ino
    app = ltt(p)
    app.type("X")
    save(app, "a.txt")
    assert os.stat(p).st_ino != inode
    assert os.listdir(ws.path("proj")) == ["a.txt"]  # no temporary file left behind
    assert ws.read("proj/a.txt") == "Xone\n"


def test_save_keeps_hard_links(ltt, ws):  # V9
    a = ws.write("proj/a.txt", "shared\n")
    os.link(a, ws.path("proj/b.txt"))
    inode = os.stat(a).st_ino
    app = ltt(a)
    app.type("X")
    save(app, "a.txt")
    st = os.stat(a)
    assert st.st_ino == inode and st.st_nlink == 2
    assert ws.read("proj/b.txt") == "Xshared\n"
    assert sorted(os.listdir(ws.path("proj"))) == ["a.txt", "b.txt"]


@pytest.mark.parametrize("relative", [True, False], ids=["relative", "absolute"])
def test_save_through_a_symlink_writes_its_target(ltt, ws, relative):  # V9
    target = ws.write("real/target.txt", "data\n")
    link = ws.path("proj/link.txt")
    ws.mkdir("proj")
    os.symlink("../real/target.txt" if relative else target, link)
    app = ltt(link)
    app.type("X")
    save(app, "link.txt")
    assert os.path.islink(link) and os.readlink(link) == ("../real/target.txt" if relative else target)
    assert ws.read("real/target.txt") == "Xdata\n"
    assert os.listdir(ws.path("proj")) == ["link.txt"] and os.listdir(ws.path("real")) == ["target.txt"]


def test_save_through_a_chain_of_symlinks_writes_the_final_target(ltt, ws):  # V9
    ws.write("target.txt", "data\n")
    os.symlink("target.txt", ws.path("middle.txt"))
    os.symlink("middle.txt", ws.path("first.txt"))
    app = ltt(ws.path("first.txt"))
    app.type("X")
    save(app, "first.txt")
    assert os.readlink(ws.path("first.txt")) == "middle.txt" and os.readlink(ws.path("middle.txt")) == "target.txt"
    assert ws.read("target.txt") == "Xdata\n"


def test_save_through_a_dangling_symlink_recreates_its_target(ltt, ws):
    ws.write("target.txt", "data\n")
    os.symlink("target.txt", ws.path("link.txt"))
    app = ltt(ws.path("link.txt"))
    os.remove(ws.path("target.txt"))
    app.wait_tree("target.txt", present=False)  # the watcher's own status comes and goes first
    app.type("X")
    save(app, "link.txt")
    assert os.readlink(ws.path("link.txt")) == "target.txt"
    assert ws.read("target.txt") == "Xdata\n"


@pytest.mark.xfail(strict=True, reason="BUG: a tab opened through a symlink never sees its target change on disk; "
                                       "Ctrl+S overwrites the outside edit without asking")
def test_save_through_a_symlink_asks_when_the_target_changed_on_disk(ltt, ws):
    ws.write("target.txt", "mine\n")
    os.symlink("target.txt", ws.path("link.txt"))
    app = ltt(ws.path("link.txt"))
    app.type("X")
    ws.write("target.txt", "the agent's version\n")
    s = app.press("ctrl+s")
    assert s.mode == "CONFIRM" and "changed on disk" in s.question
    assert ws.read("target.txt") == "the agent's version\n"


def test_saving_over_a_fifo_is_refused(ltt, ws):  # V9
    p = ws.write("a.txt", "hello\n")
    app = ltt(p)
    app.type("X")
    os.remove(p)
    os.mkfifo(p)
    app.wait(lambda s: s.tabs[0].changed, what="⚠ on the tab")
    s = app.press("ctrl+s")  # the disk version differs: the save asks first
    assert s.mode == "CONFIRM" and "changed on disk" in s.question
    app.press("s")
    s = app.wait_status("Save error: Not a regular file:")
    assert stat.S_ISFIFO(os.stat(p).st_mode)
    assert s.tabs[0].dirty and app.alive


def test_saving_a_read_only_file_reports_an_error(ltt, ws):
    p = ws.write("proj/a.txt", "hello\n", mode=0o444)
    app = ltt(p)
    app.type("X")
    app.press("ctrl+s")
    s = app.wait_status("Save error: The file is read-only:")
    assert s.tabs[0].dirty and s.title_dirty and app.alive
    assert ws.read("proj/a.txt") == "hello\n"
    assert os.listdir(ws.path("proj")) == ["a.txt"]


def test_save_works_again_once_the_file_is_writable(ltt, ws):
    p = ws.write("a.txt", "hello\n", mode=0o444)
    app = ltt(p)
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Save error:")
    os.chmod(p, 0o644)
    save(app, "a.txt")
    assert ws.read("a.txt") == "Xhello\n"


def test_saving_in_a_read_only_folder_writes_in_place(ltt, ws):  # V1
    p = ws.write("ro/a.txt", "hello\n")
    inode = os.stat(p).st_ino
    with read_only(ws.path("ro")):
        app = ltt(p)
        app.type("X")
        save(app, "a.txt")
        assert os.stat(p).st_ino == inode
        assert os.listdir(ws.path("ro")) == ["a.txt"]
    assert ws.read("ro/a.txt") == "Xhello\n"


def test_saving_a_file_with_a_255_byte_name(ltt, ws):  # V1: the temporary name stays short
    name = "n" * 251 + ".txt"
    p = ws.write("proj/" + name, "hello\n")
    inode = os.stat(p).st_ino
    app = ltt(p)
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: nnnn")
    assert os.stat(p).st_ino != inode  # still atomic
    assert os.listdir(ws.path("proj")) == [name]
    assert ws.read("proj/" + name) == "Xhello\n"


def test_saving_a_file_with_spaces_and_unicode_in_its_name(ltt, ws):
    p = ws.write("proj/my notes \u00e9 \u65e5\u672c.txt", "hello\n")
    app = ltt(p)
    app.type("X")
    save(app, "my notes \u00e9 \u65e5\u672c.txt")
    assert ws.read("proj/my notes \u00e9 \u65e5\u672c.txt") == "Xhello\n"
    assert len(os.listdir(ws.path("proj"))) == 1


def test_a_write_error_midway_keeps_the_original(ltt, ws):  # D6: EFBIG used to crash ltt
    body = "x" * 99 + "\n"
    p = ws.write("proj/big.txt", body * 3000)  # 300 KB
    with file_size_limit(100_000):
        app = ltt(p, ready=False)
    app.wait_ready()
    app.type("X")
    app.press("ctrl+s")
    s = app.wait_status("Save error:")
    assert s.tabs[0].dirty and app.alive
    assert ws.read("proj/big.txt") == body * 3000
    assert os.listdir(ws.path("proj")) == ["big.txt"]


def test_saving_where_a_folder_now_stands_reports_an_error(ltt, ws):
    p = ws.write("a.txt", "hello\n")
    app = ltt(p)
    app.type("X")
    os.remove(p)
    os.mkdir(p)
    app.wait(lambda s: s.tabs[0].missing and s.tree_row("a.txt") is not None and s.tree_row("a.txt").is_dir,
             what="✗ on the tab, a folder in the tree")
    s = app.press("ctrl+s")
    assert s.mode == "CONFIRM" and "was deleted on disk" in s.question
    app.press("s")
    s = app.wait_status("Save error:")
    assert os.path.isdir(p) and os.listdir(p) == []
    assert s.tabs[0].dirty and app.alive
    assert not [n for n in os.listdir(ws.root) if n.endswith(".tmp")]


def test_saving_after_the_folder_was_deleted_reports_an_error(ltt, ws):
    p = ws.write("proj/sub/a.txt", "hello\n")
    app = ltt(ws.path("proj"), p)
    app.type("X")
    shutil.rmtree(ws.path("proj/sub"))
    app.wait(lambda s: s.tabs[0].missing and s.tree_row("sub") is None, what="✗ on the tab, sub gone from the tree")
    s = app.press("ctrl+s")
    assert s.mode == "CONFIRM" and "was deleted on disk" in s.question
    app.press("s")
    s = app.wait_status("Save error:")
    assert s.tabs[0].dirty and app.alive
    assert not os.path.exists(ws.path("proj/sub"))


def _xattr(path, name, value=None):
    """Set (value given) or read an extended attribute; None when it is not there."""
    if hasattr(os, "setxattr"):
        try:
            return os.setxattr(path, name, value) if value is not None else os.getxattr(path, name)
        except OSError:
            if value is not None:
                pytest.skip("this file system has no extended attributes")
            return None
    if not os.path.exists("/usr/bin/xattr"):
        pytest.skip("no way to set extended attributes here")
    if value is not None:
        subprocess.run(["/usr/bin/xattr", "-w", name, value.decode(), path], check=True)
        return None
    r = subprocess.run(["/usr/bin/xattr", "-p", name, path], capture_output=True)
    return r.stdout.rstrip(b"\n") if r.returncode == 0 else None


@pytest.mark.xfail(strict=True, reason="BUG: the atomic save drops the file's extended attributes "
                                       "(Finder tags, user attributes)")
def test_save_keeps_extended_attributes(ltt, ws):
    p = ws.write("a.txt", "hello\n")
    _xattr(p, "user.ltt-test", b"kept")
    app = ltt(p)
    app.type("X")
    save(app, "a.txt")
    assert ws.read("a.txt") == "Xhello\n"
    assert _xattr(p, "user.ltt-test") == b"kept"
