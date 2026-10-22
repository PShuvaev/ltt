"""Unsaved edits that outlive the session (D7, V4, V20).

A kill signal or a closed terminal backs up every dirty tab to the
recovery folder (one file each: "ltt-recovery 1", the line count, the
path, the lines) and ends ltt with 128 + the signal; the next start
reopens them as unsaved tabs and deletes the backups.
"""
import os
import signal
import stat
import sys
import time

import pytest

W = 160  # columns: room for the longest status message
MAGIC = "ltt-recovery 1"
SIGNALS = [(signal.SIGTERM, 143), (signal.SIGINT, 130), (signal.SIGHUP, 129), (signal.SIGQUIT, 131)]


def backups(folder):
    """The complete backups (*.txt) in a recovery folder, oldest first: (name, line count, path, text)."""
    if not os.path.isdir(folder):
        return []
    out = []
    for name in sorted(n for n in os.listdir(folder) if n.endswith(".txt")):
        with open(os.path.join(folder, name), "rb") as f:
            magic, count, path, text = f.read().decode("utf-8").split("\n", 3)
        assert magic == MAGIC, name
        out.append((name, int(count), path, text))
    return out


def texts(folder):
    return [b[3] for b in backups(folder)]


def write_backup(ws, name, path, text, count=None):
    """A backup file as ltt writes it; count: the line count it claims (default: the true one)."""
    os.makedirs(ws.recovery, exist_ok=True)
    n = text.count("\n") + 1 if count is None else count
    with open(os.path.join(ws.recovery, name), "wb") as f:
        f.write(f"{MAGIC}\n{n}\n{path}\n{text}".encode("utf-8"))


def edited(ltt, ws, rel="proj/note.txt", text="hello\n", typed="XYZ", env=None):
    """ltt with rel open and `typed` typed at its start: one dirty tab."""
    ws.write(rel, text)
    app = ltt(ws.path(rel), cols=W, env=env)
    app.type(typed)
    app.wait(lambda s: s.title_dirty)
    return app


def killed(ltt, ws, sig=signal.SIGTERM, **kw):
    """A session with one dirty tab (see edited) ended by a signal; returns its exit code."""
    app = edited(ltt, ws, **kw)
    app.signal(sig)
    return app.wait_exit()


def restart(ltt, ws, *args, **kw):
    """The next session, on proj unless told otherwise; returns it and its first frame."""
    app = ltt(*(args or (ws.path("proj"),)), cols=W, **kw)
    return app, app.snap()


def wait_read(app, timeout=5.0):
    """Wait until ltt has read everything written to it (inside a paste it draws nothing)."""
    deadline = time.monotonic() + timeout
    while app._input_pending():
        assert time.monotonic() < deadline, "ltt did not read its input"
        time.sleep(0.005)


def default_recovery(ws):
    """Where backups go without LTT_RECOVERY_DIR (README: Good to know)."""
    if sys.platform == "darwin":
        return os.path.join(ws.home, "Library", "Application Support", "ltt", "recovery")
    return os.path.join(ws.home, ".local", "share", "ltt", "recovery")


# ---------------------------------------------------------------- backing up (D7)

@pytest.mark.parametrize("sig, code", SIGNALS, ids=[s.name for s, _ in SIGNALS])
def test_signal_backs_up_the_dirty_tab_and_exits_128_plus_n(ltt, ws, sig, code):
    assert killed(ltt, ws, sig) == code
    assert [b[1:] for b in backups(ws.recovery)] == [(2, ws.path("proj/note.txt"), "XYZhello\n")]
    assert ws.read("proj/note.txt") == "hello\n"


def test_backups_are_readable_by_their_owner_only(ltt, ws):  # idea from Vim's test_swap.vim
    assert not os.path.exists(ws.recovery)  # ltt makes it
    app = edited(ltt, ws)
    os.chmod(ws.path("proj/note.txt"), 0o600)
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    [name] = ws.recovery_files()
    assert stat.S_IMODE(os.stat(ws.recovery).st_mode) == 0o700
    assert stat.S_IMODE(os.stat(os.path.join(ws.recovery, name)).st_mode) == 0o600


def test_closed_terminal_backs_up_the_dirty_tab_and_exits_129(ltt, ws):
    app = edited(ltt, ws, typed="HUP")
    app.hang_up()
    assert app.wait_exit() == 129
    assert [b[1:] for b in backups(ws.recovery)] == [(2, ws.path("proj/note.txt"), "HUPhello\n")]


def test_signal_without_unsaved_edits_backs_up_nothing(ltt, ws):
    ws.write("proj/note.txt", "hello\n")
    app = ltt(ws.path("proj/note.txt"), cols=W)
    mark = app.output_mark()
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    assert ws.recovery_files() == [] and b"ltt stopped" not in app.output_since(mark)


def test_one_backup_per_dirty_tab(ltt, ws):
    for n in "abc":
        ws.write(f"proj/{n}.txt", f"{n}\n")
    app = ltt(*(ws.path(f"proj/{n}.txt") for n in "abc"), cols=W)
    app.type("A")
    app.press("alt+3")
    app.type("C")
    app.wait(lambda s: [t.dirty for t in s.tabs] == [True, False, True], what="a and c dirty")
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    assert sorted(b[2:] for b in backups(ws.recovery)) == [(ws.path("proj/a.txt"), "Aa\n"),
                                                          (ws.path("proj/c.txt"), "Cc\n")]


def test_saved_edits_are_not_backed_up(ltt, ws):
    app = edited(ltt, ws)
    app.press("ctrl+s")
    app.wait_status("Saved: note.txt")
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143 and ws.recovery_files() == []


def test_edits_undone_back_to_the_file_are_not_backed_up(ltt, ws):
    app = edited(ltt, ws)
    app.press("ctrl+z")
    app.wait(lambda s: not s.title_dirty, what="clean again")
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143 and ws.recovery_files() == []


def test_signal_while_a_question_is_open_still_backs_up(ltt, ws):
    app = edited(ltt, ws)
    assert app.press("ctrl+q").mode == "CONFIRM"
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    assert texts(ws.recovery) == ["XYZhello\n"]


def test_signal_while_the_ui_is_stuck_backs_up_from_the_handler(ltt, ws):
    # V5: the UI thread waits inside a paste that never ends: after 2 s the signal handler backs
    # the edits up itself (whole edits only) and lets the signal end ltt (test_terminal.py: the terminal)
    app = edited(ltt, ws)
    app.write(b"\x1b[200~p")
    wait_read(app)
    app.signal(signal.SIGTERM)
    deadline = time.monotonic() + 10
    while app.poll() is None and time.monotonic() < deadline:
        app.write(b"q")  # on purpose, slowly: more of the paste keeps the UI thread waiting
        time.sleep(0.2)
    assert app.wait_exit() == -signal.SIGTERM
    assert texts(ws.recovery) == ["XYZhello\n"]


@pytest.mark.xfail(strict=True, reason="BUG: a terminal closed during a paste is noticed only when the paste times "
                                       "out (2 s), racing the SIGHUP handler's own 2 s wait: two backups, or the "
                                       "paste lost")
def test_closed_terminal_mid_paste_ends_ltt_at_once_with_what_arrived(ltt, ws):
    app = edited(ltt, ws)
    app.write(b"\x1b[200~partial")
    wait_read(app)
    t0 = time.monotonic()
    app.hang_up()
    assert app.wait_exit() == 129
    assert time.monotonic() - t0 < 1.0
    assert texts(ws.recovery) == ["XYZpartialhello\n"]


def test_unwritable_recovery_folder_still_ends_the_session(ltt, ws):
    ws.write("blocker", "a file where the folder should be\n")
    app = edited(ltt, ws, env={"LTT_RECOVERY_DIR": ws.path("blocker/recovery")})
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143


@pytest.mark.skipif(sys.platform == "darwin", reason="on macOS .NET finds Application Support through the "
                    "system, not $HOME: the backup would land in the real recovery folder")
def test_without_ltt_recovery_dir_backups_go_under_home(ltt, ws):
    env = {"LTT_RECOVERY_DIR": None}
    app = edited(ltt, ws, env=env)
    mark = app.output_mark()
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    folder = default_recovery(ws)
    assert [b[2:] for b in backups(folder)] == [(ws.path("proj/note.txt"), "XYZhello\n")]
    assert f"saved to {folder};".encode() in app.output_since(mark)
    assert ws.recovery_files() == []
    app, s = restart(ltt, ws, env=env)
    assert s.editor_lines[0] == "XYZhello" and backups(folder) == []


# ---------------------------------------------------------------- restoring

def test_next_start_restores_the_unsaved_tab(ltt, ws):
    killed(ltt, ws)
    app, s = restart(ltt, ws)
    assert s.status == "Restored unsaved edits from an interrupted session: note.txt — Ctrl+S saves, Ctrl+W discards."
    assert s.mode == "EDITOR" and s.tab_names == ["note.txt"] and s.tabs[0].dirty and s.title_dirty
    assert s.editor_lines[:2] == ["XYZhello", ""]
    assert ws.recovery_files() == []  # restored: the backup is gone
    assert ws.read("proj/note.txt") == "hello\n"


def test_restored_tab_saves_the_edits(ltt, ws):
    killed(ltt, ws)
    app, _ = restart(ltt, ws)
    app.press("ctrl+s")
    s = app.wait_status("Saved: note.txt")
    assert ws.read("proj/note.txt") == "XYZhello\n" and not s.tabs[0].dirty


def test_ctrl_z_in_a_restored_tab_shows_the_file_on_disk(ltt, ws):
    killed(ltt, ws)
    app, _ = restart(ltt, ws)
    s = app.press("ctrl+z")
    assert s.editor_lines[0] == "hello" and not s.tabs[0].dirty
    s = app.press("ctrl+y")
    assert s.editor_lines[0] == "XYZhello" and s.tabs[0].dirty


def test_closed_terminal_edits_are_restored(ltt, ws):
    app = edited(ltt, ws, typed="HUP")
    app.hang_up()
    app.wait_exit()
    app, s = restart(ltt, ws)
    assert s.editor_lines[0] == "HUPhello" and s.tabs[0].dirty


def test_every_dirty_tab_is_restored(ltt, ws):
    for n in "abc":
        ws.write(f"proj/{n}.txt", f"{n}\n")
    app = ltt(*(ws.path(f"proj/{n}.txt") for n in "abc"), cols=W)
    for i, n in enumerate("abc", 1):
        app.press(f"alt+{i}")
        app.type(n.upper())
    app.wait(lambda s: all(t.dirty for t in s.tabs), what="all dirty")
    app.signal(signal.SIGTERM)
    app.wait_exit()
    app, s = restart(ltt, ws)
    assert s.status.startswith("Restored unsaved edits from an interrupted session: a.txt, b.txt, c.txt — ")
    assert s.tab_names == ["a.txt", "b.txt", "c.txt"] and all(t.dirty for t in s.tabs)
    for i, n in enumerate("abc", 1):
        assert app.press(f"alt+{i}").editor_lines[0] == n.upper() + n


def test_long_file_name_is_backed_up_and_restored(ltt, ws):
    # V4: a long name made the backup's own name too long: the edits were silently not saved
    name = "n" * 250 + ".txt"
    assert killed(ltt, ws, rel="proj/" + name) == 143
    assert [b[2:] for b in backups(ws.recovery)] == [(ws.path("proj/" + name), "XYZhello\n")]
    app, s = restart(ltt, ws)
    assert s.status.startswith("Restored unsaved edits from an interrupted session: nnnnnnnn")
    assert s.editor_lines[0] == "XYZhello" and ws.recovery_files() == []


def test_a_line_break_in_the_file_name_is_backed_up_and_restored(ltt, ws):  # idea from micro's backup names
    assert killed(ltt, ws, rel="proj/nl\nname.txt") == 143
    app, s = restart(ltt, ws)
    assert s.status.startswith("Restored unsaved edits from an interrupted session")
    assert s.editor_lines[0] == "XYZhello" and ws.recovery_files() == []


def test_backup_cut_short_is_not_restored(ltt, ws):
    # V4: a backup cut short by a kill was restored as if it were the whole text
    ws.write("proj/note.txt", "".join(f"line {i}\n" for i in range(1, 21)))
    app = ltt(ws.path("proj/note.txt"), cols=W)
    app.type("EDITED ")
    app.wait(lambda s: s.title_dirty)
    app.signal(signal.SIGTERM)
    app.wait_exit()
    [name] = ws.recovery_files()
    p = os.path.join(ws.recovery, name)
    with open(p, "rb") as f:
        data = f.read()
    with open(p, "wb") as f:
        f.write(data[:data.index(b"line 10")])  # the tail is lost, as a full disk or a crash leaves it
    app, s = restart(ltt, ws)
    assert s.tabs == [] and "Restored" not in s.status_line
    assert ws.recovery_files() == [name]  # left alone for whoever wants to dig into it


def test_backup_with_more_lines_than_it_claims_is_not_restored(ltt, ws):
    ws.write("proj/note.txt", "hello\n")
    write_backup(ws, "20260101-000000-000-aaaaaaaa-000001.txt", ws.path("proj/note.txt"), "one\ntwo\nthree\n", count=2)
    app, s = restart(ltt, ws)
    assert s.tabs == [] and "Restored" not in s.status_line


def test_foreign_files_in_the_recovery_folder_are_left_alone(ltt, ws):
    ws.write("proj/note.txt", "hello\n")
    note = ws.path("proj/note.txt")
    files = {
        "readme.txt": "just some text\n",
        "20260101-000000-000-aaaaaaaa-000001.txt.tmp": f"{MAGIC}\n1\n{note}\nhalf written",  # a write cut short
        "20260101-000000-000-aaaaaaaa-000002.txt": f"{MAGIC}\n1\n\nno path",
        "20260101-000000-000-aaaaaaaa-000003.txt": f"ltt-recovery 9\n1\n{note}\na later format",
    }
    os.makedirs(ws.recovery, exist_ok=True)
    for name, text in files.items():
        with open(os.path.join(ws.recovery, name), "w") as f:
            f.write(text)
    app, s = restart(ltt, ws)
    assert s.tabs == [] and "Restored" not in s.status_line
    assert sorted(os.listdir(ws.recovery)) == sorted(files)


def test_newest_backup_of_a_file_wins(ltt, ws):
    ws.write("proj/note.txt", "hello\n")
    note = ws.path("proj/note.txt")
    write_backup(ws, "20260101-000000-000-aaaaaaaa-000001.txt", note, "older edit\n")
    write_backup(ws, "20260102-000000-000-bbbbbbbb-000001.txt", note, "newer edit\n")
    app, s = restart(ltt, ws)
    assert s.tab_names == ["note.txt"] and s.editor_lines[0] == "newer edit"
    assert ws.recovery_files() == []  # the older copy is dropped too


def test_backup_equal_to_the_file_is_dropped_quietly(ltt, ws):
    ws.write("proj/note.txt", "hello\n")
    write_backup(ws, "20260101-000000-000-aaaaaaaa-000001.txt", ws.path("proj/note.txt"), "hello\n")
    app, s = restart(ltt, ws)
    assert s.tabs == [] and "Restored" not in s.status_line and ws.recovery_files() == []


def test_restore_when_the_file_changed_on_disk_since(ltt, ws):
    killed(ltt, ws)
    ws.write("proj/note.txt", "the agent's version\n")
    app, s = restart(ltt, ws)
    assert s.editor_lines[0] == "XYZhello" and s.tabs[0].dirty and not s.tabs[0].changed
    s = app.press("ctrl+z")  # one step back: the file as it is now
    assert s.editor_lines[0] == "the agent's version" and not s.tabs[0].dirty


def test_restore_when_the_file_is_gone(ltt, ws):
    killed(ltt, ws)
    ws.remove("proj/note.txt")
    app, s = restart(ltt, ws)
    assert s.status.startswith("Restored unsaved edits from an interrupted session: note.txt")
    assert s.tabs[0].dirty and s.tabs[0].missing and s.editor_lines[0] == "XYZhello"
    app.press("ctrl+s")  # no file was known on disk: nothing to ask about
    app.wait_status("Saved: note.txt")
    assert ws.read("proj/note.txt") == "XYZhello\n"


def test_restore_when_the_folder_is_gone_keeps_the_text_on_a_failed_save(ltt, ws):
    killed(ltt, ws, rel="proj/sub/note.txt")
    ws.remove("proj/sub")
    app, s = restart(ltt, ws)
    assert s.tabs[0].missing and s.editor_lines[0] == "XYZhello"
    app.press("ctrl+s")
    s = app.wait_status("Save error:")
    assert s.tabs[0].dirty and s.editor_lines[0] == "XYZhello"


def test_backup_of_a_file_deleted_outside_restores_as_gone(ltt, ws):
    app = edited(ltt, ws)
    ws.remove("proj/note.txt")
    app.wait(lambda s: s.tabs[0].missing, what="✗")
    app.signal(signal.SIGTERM)
    assert app.wait_exit() == 143
    app, s = restart(ltt, ws)
    assert s.tabs[0].dirty and s.tabs[0].missing and s.editor_lines[0] == "XYZhello"


def test_restore_reuses_the_tab_of_a_file_given_again(ltt, ws):
    killed(ltt, ws)
    app, s = restart(ltt, ws, ws.path("proj/note.txt"))
    assert s.tab_names == ["note.txt"] and s.tabs[0].dirty and s.editor_lines[0] == "XYZhello"


def test_restore_opens_a_file_outside_the_given_folder(ltt, ws):
    killed(ltt, ws)
    ws.mkdir("other")
    app, s = restart(ltt, ws, ws.path("other"))
    assert s.tab_names == ["note.txt"] and s.title_path.endswith("/proj/note.txt")
    assert s.editor_lines[0] == "XYZhello"


def test_restored_crlf_file_is_saved_with_crlf(ltt, ws):
    killed(ltt, ws, text="one\r\ntwo\r\n")
    app, _ = restart(ltt, ws)
    app.press("ctrl+s")
    app.wait_status("Saved: note.txt")
    assert ws.read_bytes("proj/note.txt") == b"XYZone\r\ntwo\r\n"


def test_non_ascii_edits_survive_a_kill(ltt, ws):
    killed(ltt, ws, typed="Привет 😀 日本 ")
    app, s = restart(ltt, ws)
    assert s.editor_lines[0] == "Привет 😀 日本 hello"
    app.press("ctrl+s")
    app.wait_status("Saved: note.txt")
    assert ws.read("proj/note.txt") == "Привет 😀 日本 hello\n"


def test_second_interruption_backs_up_the_restored_tab_again(ltt, ws):
    killed(ltt, ws)
    app, _ = restart(ltt, ws)
    app.type("more ")
    app.signal(signal.SIGHUP)
    assert app.wait_exit() == 129
    assert texts(ws.recovery) == ["more XYZhello\n"]


# ---------------------------------------------------------------- nothing comes back that was let go (V20)

def test_restored_edits_discarded_do_not_come_back(ltt, ws):
    # V20: discarded edits came back on the next start
    killed(ltt, ws)
    app, _ = restart(ltt, ws)
    s = app.press("ctrl+w", "d")
    assert s.tabs == []
    assert app.quit() == 0
    assert ws.recovery_files() == []
    app, s = restart(ltt, ws)
    assert s.tabs == [] and "Restored" not in s.status_line
    assert ws.read("proj/note.txt") == "hello\n"


def test_quit_discarding_edits_leaves_nothing_to_restore(ltt, ws):
    app = edited(ltt, ws)
    assert app.quit("d") == 0
    assert ws.recovery_files() == []
    app, s = restart(ltt, ws)
    assert s.tabs == [] and s.mode == "TREE" and "Restored" not in s.status_line


def test_quit_saving_all_leaves_no_backup(ltt, ws):
    app = edited(ltt, ws)
    assert app.quit("s") == 0
    assert ws.read("proj/note.txt") == "XYZhello\n" and ws.recovery_files() == []


def test_quitting_another_session_keeps_the_backup(ltt, ws):
    # a normal quit removes only its own backups: those of a session killed meanwhile stay
    ws.write("proj/other.txt", "other\n")
    first = ltt(ws.path("proj/other.txt"), cols=W)
    assert killed(ltt, ws) == 143
    assert first.quit() == 0
    assert texts(ws.recovery) == ["XYZhello\n"]
