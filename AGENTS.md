# ltt for coding agents

ltt is a small terminal text editor that people change by asking an agent,
not by configuring it. Keep it small: no dependencies beyond .NET 10, no
configuration file, no plugin API.

## Where things are

- `src/App/` — the app, one class `App` in parts: `Program.cs` (start-up,
  main loop, tabs, search, changes on disk), `App.Keys.cs` (keys, per
  mode), `App.Mouse.cs`, `App.PathInput.cs` (the prompts of Ctrl+O, Ctrl+N,
  Ctrl+R and Ctrl+L), `App.Render.cs` (title, tree, editor, tabs, search
  results, status and help lines), `App.FileOps.cs` (create, rename,
  delete, copy and move).
- `src/Editor/` — `TextEditor.cs` (lines, cursor, selection, undo),
  `TextFile.cs` (reading and saving: encodings, BOM, line endings),
  `Tabs.cs`, `Recovery.cs` (unsaved edits after a crash), `LineDiff.cs`
  (change marks), `VimSyntax.cs` (colours from a vim in the background).
- `src/Files/` — `FileTree.cs` (the left panel), `FolderWatcher.cs`
  (changes on disk), `Changes.cs` (git marks), `FileOps.cs`, `Paths.cs`,
  `PathCompletion.cs`, `Proc.cs` (git and clipboard tools).
- `src/Search/FuzzySearch.cs` — the live search.
- `src/Terminal/` — `TerminalInput.cs` (keys, mouse and paste from raw
  bytes), `Screen.cs` (sends only the cells that changed), `Cells.cs`
  (character widths), `Clipboard.cs`.

## Rules

- A new key goes into the help line on screen and the Keys table in
  README.md. No F-keys; Ctrl+C never quits.
- Saving writes a file back the way it was read: encoding, BOM, line
  endings, final newline.
- Write like the code around: its naming, comment density and idiom.
- No test code in main.

## Tests

They live in the orphan branch `tests`:

    git worktree add ../ltt-tests tests
    ../ltt-tests/run                  # unit, functional (in parallel), timing
    ../ltt-tests/run unit             # only the C# unit tests: seconds
    ../ltt-tests/run cases/test_editing.py -k undo

`run` tests the checkout on `main`; `LTT_SRC=PATH` points it at another.
The functional tests start ltt in a real pseudo-terminal, press keys and
read the screen back, one file per area (`cases/test_<area>.py`; the
branch's README shows how to write one). New behaviour gets a test there,
waiting on what the screen shows, never on a fixed sleep. ltt needs a real
terminal: to see what it does, run or write a test.
