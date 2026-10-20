# ConsoleEditor

**A fast, tiny two-panel console editor for the age of coding agents.**

Agents write most of the code now. What you do all day changed with it:
read what the agent produced, jump around the project, search for a
symbol, fix a line or two, hand the keyboard back. An IDE — project
systems, indexers, language servers, slow startup — is heavy machinery
for that loop.

ConsoleEditor is the opposite. It starts instantly in the terminal you
already keep open next to your agent, shows folders on the left and code
on the right, searches the whole tree fuzzily as you type, and gets out
of the way. No configuration, no plugins, no project files, zero
dependencies: ~5k lines of C# in a 140 KB assembly (.NET 10).

```
 ConsoleEditor · /home/you/project/src/Program.cs
▾ project/            │ Program.cs ×   ⚑ notes.txt
  ▾ src/              │   1  using System;
      Program.cs      │   2
      FileTree.cs     │   3  class Program { ... }
  ▸ docs/             │
  README.md           │
──────────────────────┴──────────────────────────────────
 Search results for "mngr": 1/3 · Enter opens · Ctrl+G next
 [EDITOR] Saved: Program.cs              Ln 3/120, Col 1
 Esc to tree · Option+←→ by words · Ctrl+S save · Ctrl+W/P tabs
```

## Why it feels fast

- **Instant on** — nothing is indexed or scanned at startup; the tree
  loads lazily, one folder at a time, only when you expand it.
- **Cheap frames** — highlighting and rendering are span-based and
  allocate next to nothing per keystroke. Smoke-tested on a 25 MB /
  1M-line file: opening, jumping to the end and typing stay smooth.
- **Search keeps up with typing** — fuzzy content search runs live from
  2 characters and streams files instead of loading them whole
  (~40 ms across this repo, process startup included).
- **Made for agent workflows** — when your agent (or git, or a build)
  creates, renames or deletes files, the tree updates by itself. Open
  tabs are never reloaded behind your back.

## Quick start

```sh
git clone https://github.com/PShuvaev/ltt.git ~/Downloads/ltt && cd ~/Downloads/ltt && dotnet run
```

Requires Git, the .NET 10 SDK and any XTerm-style terminal (iTerm2, kitty,
WezTerm, GNOME Terminal...). On Windows everything works except the mouse.

## Keys

`Esc` always means "back": editor → tree, close a prompt, hide the
search panel, cancel a confirmation.

**Everywhere**

| Key | Does |
|---|---|
| `Ctrl+F` · `Ctrl+F+F` | search selected/open file or folder · search all folders (press `Ctrl+F` twice) |
| `Ctrl+R+R` | rename file/folder (press `Ctrl+R` twice) |
| `Ctrl+G` / `Ctrl+Shift+G` | next / previous search hit |
| `Ctrl+S` · `Ctrl+Z` · `Ctrl+Y` / `Ctrl+Shift+Z` | save · undo · redo |
| `Ctrl+O` · `Ctrl+N` · `Ctrl+Shift+N` | add root · new file · new folder |
| `Ctrl+W` · `Ctrl+P` · `Alt+1..9` · `Ctrl+PgUp`/`PgDn` | close tab · pin tab · jump to tab · cycle tabs |
| `Ctrl+Q` | quit |

**Tree** (left panel)

| Key | Does |
|---|---|
| `Enter` / `→` · `←` | open file (on a folder: expand) · collapse |
| `Tab` | to the editor (`Esc` returns) |
| `Ctrl+R+R` · `Ctrl+Delete` · `Delete` | rename · delete from disk · remove root from panel |

**Editor** (right panel)

| Key | Does |
|---|---|
| `Shift+arrows` | extend selection (mouse drag and double-click work too) |
| `Option`/`Ctrl+←→` · `Option+↑↓` | jump by words · by paragraphs |
| `Option`/`Ctrl+Backspace` | delete the word before the cursor |
| `Ctrl+Home` / `Ctrl+End` · `PgUp`/`PgDn` | document start/end · page |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | copy / cut / paste via the system clipboard |
| `Ctrl+R+R` | rename the open file (same as in the tree) |

**Prompts and search**

| Key | Does |
|---|---|
| `Tab` · `↑`/`↓` · `→` | cycle path completions · accept ghost suggestion |
| `Ctrl+U` | clear the whole input line |
| `↑`/`↓` · `PgUp`/`PgDn` · `Enter` | move in live results · open the hit |
| `S` / `D` / `Esc` | in confirmations: save / don't save (delete) / cancel |

## Good to know

- `Ctrl+F` searches from the focused target: in the tree, inside the
  selected file or recursively under the selected folder/root; in the
  editor, the open file including unsaved edits. Pressing `Ctrl+F`
  again widens the same query to all folders — that's `Ctrl+F+F`.
- No shortcut uses a function key. Bare `Shift` only extends the
  selection with the arrow keys; every other `Shift` combination comes
  with `Ctrl` (`Ctrl+Shift+G`, `Ctrl+Shift+N`, `Ctrl+Shift+Z`) and
  needs a kitty-protocol terminal (kitty, WezTerm, Ghostty, foot) to
  be told apart from its unshifted form. Fallbacks: type a trailing
  `/` in the new-file prompt to make a folder; `Ctrl+Y` redoes.
- `Ctrl+V` pastes the system clipboard (multi-line, one undo step); your
  terminal's own paste works too — and is the only route over SSH, where
  the clipboard can't be read back. `Ctrl+C` copies a selection or
  cancels a prompt; it never quits (that's `Ctrl+Q`).
- The tree watches its folders (`FileSystemWatcher`, polling fallback);
  file *contents* in open tabs are never reloaded behind your back.
- Search skips `.git`, `bin`, `obj`, `node_modules`, binaries and files
  over 2 MB. Tabs become 4 spaces on open; files are saved as UTF-8
  (a BOM is preserved when the file had one, never added otherwise).
- No terminal? `dotnet run -- --search "mngr" .` prints matches, and
  `dotnet run -- --self-test` runs the built-in checks.

## Code map

All sources live in `src/`, grouped by area: `App/` — entry point, app
core and the rest of the UI (`Program.cs`, `App.*.cs`) · `Editor/` — text
buffer & undo, tabs, syntax colours · `Files/` — the tree, folder
watching, file operations, path completion · `Search/` — fuzzy content
search · `Terminal/` — raw terminal input + SGR mouse, clipboard ·
`Tests/` — the `--self-test` checks.
