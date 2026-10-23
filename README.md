# ltt tests

Tests for [ltt](https://github.com/PShuvaev/ltt). The functional tests start
the real editor in a pseudo-terminal, press keys, click, paste, send signals,
change files behind its back — and check what the screen shows, what lands
on disk and how the process ends. The unit tests (`unit/`, C#) call ltt's
classes directly, without a terminal.

This branch has no common history with `main`: the installer clones only
`main`, and the tests stay out of it.

## Run

```sh
git worktree add ../ltt-tests tests   # once, next to your ltt checkout
../ltt-tests/run                      # build ltt, run everything
```

- `./run` tests the worktree on `main` (uncommitted changes included): the
  unit tests first, then it builds ltt into `.build/`, runs the functional
  tests in parallel and the timing tests alone. The first run creates
  `.venv` with the pinned packages (`requirements.txt`: pyte, pytest,
  pytest-xdist, pytest-timeout).
- `./run unit` — the unit tests alone, in seconds (`-v` lists every check).
  Without Python: `dotnet run --project unit`, with `-p:LTT_SRC=PATH` when
  the ltt checkout is not `../ltt`.
- `./run cases/test_save.py -k crlf` — any pytest arguments.
- `./run --ltt-src=PATH …` tests another checkout (`LTT_SRC=PATH ./run` for
  everything); `--ltt-dll=PATH` a built `ltt.dll`.
- `LTT_TEST_TIME_SCALE=3 ./run` stretches every timeout on a slow machine.

Needs Python 3.9+, the .NET 10 SDK and git; macOS or Linux. The unit tests
need only the .NET SDK and git.

Known ltt bugs are strict xfails: `./run -rx` lists them with their reasons.
Once a bug is fixed its test fails as XPASS — then drop the mark.

## Layout

| Path | What |
|---|---|
| `run` | entry point: venv, unit tests, build, pytest |
| `conftest.py` | fixtures: `ws` (a sealed-off workspace), `ltt` (start ltt in it) |
| `harness/term.py` | `Ltt`: a session in a pty — `press`, `type`, `paste`, `click`, `wait…`, `signal`, `resize` |
| `harness/screen.py` | `Snap`: one complete frame read back — title, tree, tabs, editor rows, results, status, help, colours |
| `harness/keys.py` | keys and mouse as bytes: legacy xterm sequences and the kitty protocol |
| `harness/workspace.py` | `Workspace`: files, git repositories, HOME, recovery folder, fake clipboard |
| `harness/build.py` | find the checkout, build it, cache big fixture data |
| `cases/test_*.py` | the functional tests, one file per area; `cases/*_helpers.py` — helpers of an area |
| `unit/` | the unit tests: `UnitTests.cs`, built together with ltt's sources by `unit.csproj`; `EditVectors.cs` — VS Code's edit vectors by way of micro (MIT, notice inside) |

## What is covered

| Area | Files |
|---|---|
| start, command line, terminal modes, signals, safe rendering, input parsing | `test_startup`, `test_terminal`, `test_input` |
| the tree: moving, sizes, git dimming, outside changes; roots; deleting | `test_tree`, `test_roots`, `test_delete` |
| copying and moving in the tree | `test_copy_move`, `test_copy_move_big` |
| editing, cursor movement, selection, display, syntax colours | `test_editing`, `test_movement`, `test_selection`, `test_display`, `test_highlight` |
| undo, clipboard, paste, saving, encodings | `test_undo`, `test_clipboard`, `test_save`, `test_encoding` |
| files changed outside, recovery of unsaved edits | `test_external`, `test_recovery` |
| search | `test_search`, `test_search_results` |
| go to, new file and folder, rename, tabs, quitting | `test_goto`, `test_create`, `test_rename`, `test_tabs`, `test_quit` |
| keyboard protocols and layouts, mouse, change highlighting and the walk through the changes | `test_keyboard`, `test_mouse`, `test_changes` |
| speed and big data | `test_perf`, `test_scale` |
| the harness itself | `test_harness`, `test_smoke` |

## Writing a test

```python
def test_save_keeps_crlf(ltt, ws):
    ws.write("crlf.txt", b"one\r\ntwo\r\n")
    app = ltt(ws.path("crlf.txt"))          # opens the file, focus in the editor
    app.type("X")
    app.press("ctrl+s")
    app.wait_status("Saved: crlf.txt")
    assert ws.read_bytes("crlf.txt") == b"Xone\r\ntwo\r\n"
```

- Every session is sealed: its own `HOME`, `LTT_RECOVERY_DIR`, `TMPDIR` and a
  `PATH` with only git and fake `pbcopy`/`pbpaste` that keep the clipboard in
  `ws.clipboard`. Nothing reaches the real home, recovery folder or clipboard.
- `press`, `type`, `paste`, `click` return once ltt has read the input and
  drawn a frame after it. Anything that runs in the background — search, git,
  the file watcher, folder sizes, copies — needs `app.wait(...)` or one of the
  `wait_*` helpers; never a fixed sleep. So does input that keeps ltt busy
  for a while before it draws (adding a root starts watching it): a
  background frame drawn just as the input arrived would pass for the answer.
- A snapshot (`app.snap()`, or what `wait` returns) is always a whole frame.
  `s.status`, `s.mode`, `s.pos`, `s.tree`, `s.tabs`, `s.editor_lines`,
  `s.editor_rows` (number, change mark, text), `s.results`, `s.fg(x, y)` …
  colours are ConsoleColor names (`"Green"`, `"DarkGray"`).
- A test fails on its own if ltt shows an internal error or crashes.
- Input that ltt ignores draws no frame: `press(..., draws=False)`.
- A status message is cut at the terminal width: give the session more
  columns (`cols=200`) to read a long one whole.
- When a test changes files under ltt's eyes, the watcher's "External change…"
  status can replace the one being waited for: wait for the visible result
  (⚠, ✗, a tree row) first.
- ltt never starts without `LTT_RECOVERY_DIR` on macOS (the harness refuses):
  .NET finds the default folder through the system, not `HOME`, so it would be
  the user's real one.
- On macOS the copy/move tests make a 128 MB RAM disk for moves across disks
  and detach it afterwards.
- Big data (huge files, thousands of files) goes through `harness.shared(name)`,
  made once and kept in `.build/fixtures`.
- Marks: `slow` (more than a few seconds), `perf` (timing thresholds; run
  alone by `./run`).
- A test for a known bug stays in, marked
  `@pytest.mark.xfail(strict=True, reason="BUG: …")`.

## Writing a unit test

`unit/UnitTests.cs` has a `Test…` method per area (scoring, highlighting,
input parsing, editing, undo, saving, search, the tree, git, the watcher…);
each fact is one `Check(condition, "what must hold")`, files go into
`TempDir(...)`, removed after the run. The project compiles every
`src/**/*.cs` of the checkout along with it, so `internal` members are in
reach; a private one is read through reflection, as `RowText` does. Its
settings follow `ltt.csproj`: a change there belongs in `unit.csproj` too.
