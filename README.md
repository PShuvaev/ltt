# ConsoleEditor

A tiny two-panel console editor in C# (.NET 10, zero dependencies):
browse folders on the left, edit code on the right, fuzzy-search everything.

```
 ConsoleEditor · /home/you/project/src/Program.cs
▾ project/            │ Program.cs ×   ⚑ notes.txt
  ▾ src/              │   1  using System;
      Program.cs      │   2
      FileTree.cs     │   3  class Program { ... }
  ▸ docs/             │
  README.md           │
──────────────────────┴──────────────────────────────────
 Search results for "mngr": 1/3 · Enter opens · F8/F9 next/prev
 [EDITOR] Saved: Program.cs              Ln 3/120, Col 1
 F6/Esc to tree · Option+←→ by words · Ctrl+S save · Ctrl+W/P tabs
```

## Quick start

```bash
dotnet run                      # current folder
dotnet run -- ~/project         # a folder (or several: ~/a ~/b)
dotnet run -- Program.cs        # open straight on a file
```

Requires the .NET 10 SDK and any XTerm-style terminal (iTerm2, kitty,
WezTerm, GNOME Terminal...). On Windows everything works except the mouse.

## The 30-second tour

- **Navigate** — arrows move in the tree, `→`/`←` expand/collapse folders,
  `Enter` opens a file in a tab. `Tab`/`F6` hops between panels.
- **Edit** — syntax highlighting, line numbers, mouse selection
  (double-click = word), `Option+arrows` jump by words and paragraphs,
  `Ctrl+Z`/`Ctrl+Y` undo/redo, `Ctrl+S` saves.
- **Search** — `Shift+F` and just start typing: results appear live in the
  bottom panel from 2 characters. It's fuzzy: `mngr` finds "manager".
  Select text first and `Shift+F` searches for exactly that.
- **Files** — `Ctrl+N` new file (opens instantly), `Ctrl+Shift+N` new
  folder, `F2` rename, `Shift+Delete` delete from disk, `Ctrl+O` add
  another root folder to the panel — all path prompts autocomplete as
  you type (`Tab` cycles suggestions).
- **It keeps up** — files created/renamed/deleted by git, a build or
  another app show up in the tree automatically. Clicking or dragging
  over the path in the top line copies it to the clipboard.

## Keys worth memorizing

| Key | Does |
|---|---|
| `Enter` · `←`/`→` | open file · collapse/expand folder |
| `Tab` / `F6` / `Esc` | switch / back to the tree |
| `Shift+F` · `F8`/`F9` | fuzzy content search · next/prev hit |
| `Ctrl+O` · `Ctrl+N` · `F2` | add root · new file · rename |
| `Shift+Delete` · `Delete` | delete from disk · remove root from panel |
| `Ctrl+S` · `Ctrl+Z`/`Ctrl+Y` | save · undo/redo |
| `Ctrl+W` · `Ctrl+P` · `Alt+1..9` | close tab · pin tab ⚑ · jump to tab |
| `Ctrl+C` / `Ctrl+X` | copy / cut selection to the system clipboard |
| `Option+←→` · `Option+↑↓` | jump by words · by paragraphs |
| Mouse wheel · drag · 2×click | scroll · select text · select word / open |
| `F5` · `Ctrl+Q` | refresh tree manually · quit |

## Good to know

- `Shift+F` inside the editor types a plain `F` when nothing is selected —
  with a selection it searches for the selected text instead.
- Pinned tabs ⚑ sit on the left and refuse to close until unpinned
  (`Ctrl+P` or right-click the tab).
- The tree watches its folders (`FileSystemWatcher`, polling fallback);
  file *contents* in open tabs are never reloaded behind your back.
- Search skips `.git`, `bin`, `obj`, `node_modules`, binaries and files
  over 2 MB. Tabs become 4 spaces on open; files are saved as UTF-8.
- No terminal? `dotnet run -- --search "mngr" .` prints matches, and
  `dotnet run -- --self-test` runs the built-in checks.

## Code map

`Program.cs` — entry point & app core · `App.Render.cs` / `App.Keys.cs` /
`App.Mouse.cs` / `App.FileOps.cs` / `App.PathInput.cs` — the rest of the UI ·
`FileTree.cs` — the tree · `TextEditor.cs` — buffer & undo ·
`Highlighter.cs` — syntax colours · `FuzzySearch.cs` — search ·
`FolderWatcher.cs` — external-change watching · `TerminalInput.cs` — raw
terminal + SGR mouse · `Tabs.cs` — tabs & pins · `Clipboard.cs`,
`PathCompletion.cs`, `FileOps.cs` — small helpers ·
`SelfTest.cs` — `--self-test` checks.
