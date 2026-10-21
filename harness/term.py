"""ltt in a pseudo-terminal: keys in, frames out, waiting on what the screen shows.

The child is `dotnet ltt.dll args…` on the slave side of a pty, with an
environment the caller builds (see workspace.Workspace.env). A reader
thread feeds every byte into a pyte screen and takes a snapshot whenever a
frame is complete (ltt wraps each frame in DEC 2026 synchronized output),
so a test never looks at a half-drawn frame.

Rules this module keeps (learned the hard way):
- only our own child is ever signalled, by pid — never pkill/killall;
- the reader never blocks in read(): on macOS close() of a pty master
  waits for a thread blocked in read() on it, so it polls with select.
"""
import errno
import fcntl
import os
import re
import select
import signal
import struct
import termios
import threading
import time

import pyte

from . import keys as K
from .screen import Cell, Snap, color_name

FRAME_END = b"\x1b[?2026l"
TIME_SCALE = float(os.environ.get("LTT_TEST_TIME_SCALE", "1"))


class LttError(AssertionError):
    pass


def _scaled(t):
    return t * TIME_SCALE


class Ltt:
    def __init__(self, dotnet, dll, args, cwd, env, cols=100, rows=30, keyboard="legacy", ready=True):
        self.cols, self.rows = cols, rows
        self.keyboard = keyboard
        self.args = list(args)
        self._lock = threading.Lock()
        self._cond = threading.Condition(self._lock)
        self._raw = bytearray()
        self._chunks = []                 # (monotonic time, bytes)
        self._carry = b""                 # tail that may be the start of FRAME_END
        self._screen = pyte.Screen(cols, rows)
        self._stream = pyte.ByteStream(self._screen)
        self._snap = None
        self._frames = 0
        self._frame_times = []
        self._last_output = 0.0
        self._last_input = 0.0
        self._exit_status = None
        self.exited_at = None
        self._stop = threading.Event()
        self.started_at = time.monotonic()
        self.problems = []                # internal errors ltt showed in its status line

        self.master, self.slave = os.openpty()
        self._set_winsize(self.slave, cols, rows)
        pid = os.fork()
        if pid == 0:  # child: become a session leader with the slave as the controlling terminal
            try:
                os.close(self.master)
                os.setsid()
                fcntl.ioctl(self.slave, termios.TIOCSCTTY, 0)
                for fd in (0, 1, 2):
                    os.dup2(self.slave, fd)
                if self.slave > 2:
                    os.close(self.slave)
                os.chdir(cwd)
                os.execve(dotnet, [dotnet, dll] + [str(a) for a in args], env)
            except BaseException as e:  # noqa: BLE001 — report and leave, whatever happened
                try:
                    os.write(2, f"ltt test harness: exec failed: {e!r}\r\n".encode())
                finally:
                    os._exit(127)
        self.pid = pid
        self._reader = threading.Thread(target=self._read_loop, name=f"ltt-reader-{pid}", daemon=True)
        self._reader.start()
        if ready:
            self.wait_ready()

    # ---------------------------------------------------------------- output side

    @staticmethod
    def _set_winsize(fd, cols, rows):
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))

    def _read_loop(self):
        while not self._stop.is_set():
            try:
                r, _, _ = select.select([self.master], [], [], 0.02)
            except (OSError, ValueError):
                break
            if not r:
                continue
            try:
                data = os.read(self.master, 1 << 16)
            except OSError as e:
                if e.errno in (errno.EIO, errno.EBADF):
                    break
                raise
            if not data:
                break
            with self._cond:
                now = time.monotonic()
                self._raw.extend(data)
                self._chunks.append((now, data))
                self._last_output = now
                self._feed(data, now)
                self._cond.notify_all()
        with self._cond:
            if self._carry:
                self._stream.feed(self._carry)
                self._carry = b""
            self._cond.notify_all()

    def _feed(self, data, now):
        data = self._carry + data
        self._carry = b""
        pos = 0
        while True:
            i = data.find(FRAME_END, pos)
            if i < 0:
                break
            end = i + len(FRAME_END)
            self._stream.feed(data[pos:end])
            pos = end
            self._take_snapshot(now)
        rest = data[pos:]
        keep = 0
        for k in range(min(len(FRAME_END) - 1, len(rest)), 0, -1):
            if rest.endswith(FRAME_END[:k]):
                keep = k
                break
        if keep:
            self._stream.feed(rest[:-keep])
            self._carry = rest[-keep:]
        else:
            self._stream.feed(rest)

    def _take_snapshot(self, now):
        s = self._screen
        cells = []
        for y in range(s.lines):
            row = s.buffer[y]
            cells.append([Cell(ch.data, color_name(ch.fg), color_name(ch.bg)) for ch in (row[x] for x in range(s.columns))])
        self._frames += 1
        self._frame_times.append(now)
        self._snap = Snap(cells, (s.cursor.x, s.cursor.y), s.cursor.hidden, s.title, self._frames, now)
        status = self._snap.lines[-2] if len(self._snap.lines) >= 2 else ""
        if ("Internal error" in status or "Internal drawing error" in status) and status not in self.problems:
            self.problems.append(status.strip())

    # ---------------------------------------------------------------- state

    @property
    def output(self):
        """Every byte ltt wrote so far."""
        with self._lock:
            return bytes(self._raw)

    @property
    def output_text(self):
        return self.output.decode("utf-8", "replace")

    def output_since(self, mark):
        """Bytes written after mark (a value of output_mark)."""
        with self._lock:
            return bytes(self._raw[mark:])

    def output_mark(self):
        with self._lock:
            return len(self._raw)

    @property
    def frames(self):
        with self._lock:
            return self._frames

    def snap(self):
        """The last complete frame."""
        with self._lock:
            snap = self._snap
        if snap is None:
            raise LttError("ltt has not drawn a frame yet\n" + self._tail())
        return snap

    def _tail(self, n=1500):
        text = self.output[-n:].decode("utf-8", "replace")
        return "output tail: " + repr(text)

    @property
    def alive(self):
        return self.poll() is None

    def poll(self):
        """None while running, else the exit code (negative: killed by that signal)."""
        if self._exit_status is None:
            try:
                pid, status = os.waitpid(self.pid, os.WNOHANG)
            except ChildProcessError:
                return self._exit_status
            if pid == self.pid:
                self._exit_status = os.waitstatus_to_exitcode(status)
                self.exited_at = time.monotonic()
                with self._cond:
                    self._cond.notify_all()
        return self._exit_status

    # ---------------------------------------------------------------- waiting

    def wait(self, pred, timeout=10.0, what=None):
        """Wait until pred(snap) is true for a complete frame; returns that frame."""
        deadline = time.monotonic() + _scaled(timeout)
        error = None
        while True:
            with self._lock:
                snap = self._snap
            if snap is not None:
                try:
                    if pred(snap):
                        return snap
                    error = None
                except Exception as e:  # noqa: BLE001 — a predicate may trip over a partial UI
                    error = e
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                desc = what or getattr(pred, "__name__", "condition")
                msg = f"timed out after {timeout}s waiting for: {desc}"
                if error is not None:
                    msg += f"\nlast predicate error: {error!r}"
                if self.poll() is not None:
                    msg += f"\nltt exited with {self.poll()}"
                raise LttError(msg + "\n" + (snap.dump() if snap else "(no frame)") + "\n" + self._tail(600))
            if self.poll() is not None and snap is not None and self._snap is snap:
                # ltt is gone: one last look, then fail
                deadline = min(deadline, time.monotonic() + 0.2)
            with self._cond:
                self._cond.wait(min(remaining, 0.05))

    def wait_ready(self, timeout=20.0):
        """The first frame: the title and a status or prompt line are on screen."""
        return self.wait(lambda s: s.lines[0].startswith(" ltt · ") or s.too_small, timeout, "first frame")

    def wait_text(self, text, timeout=10.0):
        return self.wait(lambda s: s.has(text), timeout, f"text {text!r} on screen")

    def wait_no_text(self, text, timeout=10.0):
        return self.wait(lambda s: not s.has(text), timeout, f"text {text!r} gone from screen")

    def wait_status(self, text, timeout=10.0):
        """Wait for text in the status line (message, prompt or question)."""
        return self.wait(lambda s: text in s.status_line, timeout, f"status containing {text!r}")

    def wait_status_re(self, pattern, timeout=10.0):
        rx = re.compile(pattern)
        return self.wait(lambda s: rx.search(s.status_line), timeout, f"status matching {pattern!r}")

    def wait_mode(self, mode, timeout=10.0):
        return self.wait(lambda s: s.mode == mode, timeout, f"mode {mode}")

    def wait_title(self, text, timeout=10.0):
        return self.wait(lambda s: text in s.lines[0], timeout, f"title containing {text!r}")

    def wait_tree(self, name, present=True, timeout=10.0):
        if present:
            return self.wait(lambda s: s.tree_row(name) is not None, timeout, f"{name!r} in the tree")
        return self.wait(lambda s: s.tree_row(name) is None, timeout, f"{name!r} gone from the tree")

    def wait_editor(self, pred_or_lines, timeout=10.0):
        """Wait for the editor to show these lines (from the top of the view), or for pred(editor_lines)."""
        if callable(pred_or_lines):
            return self.wait(lambda s: pred_or_lines(s.editor_lines), timeout, "editor lines")
        want = list(pred_or_lines)
        return self.wait(lambda s: s.editor_lines[:len(want)] == want, timeout, f"editor lines {want!r}")

    def wait_frame(self, after=None, timeout=10.0):
        """Wait for a frame newer than frame number `after` (default: the current one)."""
        n = self.frames if after is None else after
        return self.wait(lambda s: s.frame_no > n, timeout, f"a frame after #{n}")

    def settle(self, timeout=10.0, quiet=0.04, frame=True):
        """After input: wait until ltt read all of it, drew a frame after it and went quiet.

        frame=False is for input ltt ignores (it draws nothing then): read and quiet is enough.
        """
        deadline = time.monotonic() + _scaled(timeout)
        t_in = self._last_input
        if not frame:
            quiet = max(quiet, 0.15)
        while True:
            now = time.monotonic()
            if self.poll() is not None:
                self._drain()
                return self._snap
            with self._lock:
                drawn = not frame or (bool(self._frame_times) and self._frame_times[-1] >= t_in)
                quiet_for = now - max(self._last_output, t_in)
            if drawn and quiet_for >= quiet and self._input_pending() == 0:
                return self.snap()
            if now > deadline:
                raise LttError(f"ltt did not answer the input within {timeout}s\n"
                               + (self._snap.dump() if self._snap else "(no frame)") + "\n" + self._tail(600))
            time.sleep(0.003)

    def _input_pending(self):
        try:
            buf = fcntl.ioctl(self.slave, termios.FIONREAD, b"\0\0\0\0")
            return struct.unpack("i", buf)[0]
        except OSError:
            return 0

    def _drain(self, quiet=0.1, timeout=2.0):
        """After the exit: let the reader catch the last bytes."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            with self._lock:
                if time.monotonic() - self._last_output >= quiet:
                    return
            time.sleep(0.01)

    def wait_output(self, needle, timeout=10.0, after=0):
        """Wait until the raw output (from byte offset `after`) contains needle; returns its offset."""
        if isinstance(needle, str):
            needle = needle.encode()
        deadline = time.monotonic() + _scaled(timeout)
        while True:
            with self._lock:
                i = self._raw.find(needle, after)
            if i >= 0:
                return i
            if time.monotonic() > deadline:
                raise LttError(f"timed out after {timeout}s waiting for {needle!r} in the output\n" + self._tail(600))
            with self._cond:
                self._cond.wait(0.05)

    def tty_settings(self):
        """termios of ltt's terminal (after the exit too: macOS answers through the master then)."""
        for fd in (self.slave, self.master):
            if fd is not None and fd >= 0:
                try:
                    return termios.tcgetattr(fd)
                except termios.error:
                    continue
        raise LttError("the terminal is closed")

    def wait_exit(self, timeout=10.0):
        """Wait for ltt to end; returns its exit code (negative: the signal that killed it)."""
        deadline = time.monotonic() + _scaled(timeout)
        while self.poll() is None:
            if time.monotonic() > deadline:
                raise LttError(f"ltt did not exit within {timeout}s\n"
                               + (self._snap.dump() if self._snap else "(no frame)") + "\n" + self._tail(600))
            time.sleep(0.01)
        self._drain()
        return self._exit_status

    # ---------------------------------------------------------------- input side

    def write(self, data):
        """Raw bytes to ltt's terminal input."""
        if isinstance(data, str):
            data = data.encode()
        if not data:
            return
        view = memoryview(data)
        while view:
            try:
                n = os.write(self.master, view)
            except BlockingIOError:
                time.sleep(0.005)
                continue
            view = view[n:]
        self._last_input = time.monotonic()

    def press(self, *keys, settle=True, keyboard=None, draws=True):
        """Press keys one after another; each waits for ltt to answer unless settle=False.

        A key is a spec ("ctrl+s", "down", "esc", "a") or raw bytes. draws=False: input that
        ltt ignores (no frame follows) — wait only until it is read and ltt is quiet.
        """
        mode = keyboard or self.keyboard
        snap = None
        for key in keys:
            data = K.encode(key, mode)
            self.write(data)
            if K.is_lone_esc(data) and not settle:
                time.sleep(0.1)  # ltt waits 40 ms to tell a lone Esc from a sequence
            if settle:
                snap = self.settle(frame=draws)
        return snap

    def type(self, text, settle=True, each=False, draws=True):
        """Type text: all at once (as fast typing arrives), or key by key with each=True."""
        if each:
            snap = None
            for ch in text:
                snap = self.press("enter" if ch == "\n" else ch, settle=settle, draws=draws)
            return snap
        if not text:
            return self.snap()
        self.write(text.replace("\n", "\r").encode())
        return self.settle(frame=draws) if settle else None

    def paste(self, text, settle=True):
        """A bracketed paste, line breaks as a terminal sends them (CR)."""
        self.write(K.PASTE_START + text.replace("\r\n", "\r").replace("\n", "\r").encode() + K.PASTE_END)
        return self.settle() if settle else None

    # ---- mouse (0-based cells) ----

    def click(self, x, y, button=K.LEFT, mods=0, settle=True):
        self.write(K.mouse(button | mods, x, y, True) + K.mouse(button | mods, x, y, False))
        return self.settle() if settle else None

    def double_click(self, x, y, settle=True):
        b = K.LEFT
        self.write(K.mouse(b, x, y, True) + K.mouse(b, x, y, False) + K.mouse(b, x, y, True) + K.mouse(b, x, y, False))
        return self.settle() if settle else None

    def drag(self, x0, y0, x1, y1, steps=None, settle=True):
        """Press at (x0, y0), move to (x1, y1) with the button held, release there."""
        seq = K.mouse(K.LEFT, x0, y0, True)
        n = steps or max(abs(x1 - x0), abs(y1 - y0), 1)
        for i in range(1, n + 1):
            x = x0 + (x1 - x0) * i // n
            y = y0 + (y1 - y0) * i // n
            seq += K.mouse(K.LEFT | K.MOTION, x, y, True)
        seq += K.mouse(K.LEFT, x1, y1, False)
        self.write(seq)
        return self.settle() if settle else None

    def wheel(self, x, y, down=True, notches=1, mods=0, settle=True):
        b = (K.WHEEL_DOWN if down else K.WHEEL_UP) | mods
        self.write(b"".join(K.mouse(b, x, y, True) for _ in range(notches)))
        return self.settle() if settle else None

    # ---- terminal ----

    def resize(self, cols, rows, settle=True):
        before = self.frames
        with self._lock:
            self.cols, self.rows = cols, rows
            self._screen.resize(rows, cols)
        self._set_winsize(self.master, cols, rows)
        try:
            os.kill(self.pid, signal.SIGWINCH)
        except ProcessLookupError:
            pass
        self._last_input = time.monotonic()
        if settle:
            # a frame drawn for the new size: the divider sits where the new width puts it
            return self.wait(lambda s: s.frame_no > before and s.cols == cols and s.rows == rows
                             and (s.too_small or s.lines[1][s.left_w:s.left_w + 1] == "│"), 10, f"a {cols}x{rows} frame")
        return None

    def signal(self, sig):
        """Send a signal to ltt (our own child only)."""
        os.kill(self.pid, sig)

    def hang_up(self):
        """Close the terminal under ltt, as a closed window does."""
        self._stop.set()
        self._reader.join(2)
        for fd in (self.slave, self.master):
            try:
                os.close(fd)
            except OSError:
                pass
        self.slave = self.master = -1

    def quit(self, *answers, timeout=10.0):
        """Ctrl+Q (and the answers to its question, if any); returns the exit code."""
        self.press("ctrl+q", settle=False)
        for a in answers:
            self.wait(lambda s: s.mode == "CONFIRM", 5, "the quit question")
            self.press(a, settle=False)
        return self.wait_exit(timeout)

    def close(self):
        """Stop everything: our child if still running, the reader, the pty."""
        if self.poll() is None:
            try:
                os.kill(self.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            try:
                os.waitpid(self.pid, 0)
            except ChildProcessError:
                pass
            self._exit_status = self._exit_status if self._exit_status is not None else -9
        self._stop.set()
        self._reader.join(2)
        for fd in (self.slave, self.master):
            if fd is not None and fd >= 0:
                try:
                    os.close(fd)
                except OSError:
                    pass
        self.slave = self.master = -1

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
