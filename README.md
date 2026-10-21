<div align="center">

# look-touch-toss

### <ins>l</ins>ightweight <ins>t</ins>erminal <ins>t</ins>ext editor
for the age of coding agents

👀 look at what the agent wrote · ✏️ touch a line or two · 🪃 toss it back

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![dependencies: 0](https://img.shields.io/badge/dependencies-0-2ea44f?style=flat-square)](ltt.csproj)

</div>

## Quick start

```sh
curl https:/ltt.is-an.ai|sh
```

It opens the current folder; pass folders or `file:line` after `sh -s --`
(`curl https:/ltt.is-an.ai|sh -s -- src/ README.md:10`). The script
keeps one clone in `~/.cache/ltt` and updates it on every start.

> [!NOTE]
> Requires Git, the .NET 10 SDK and any XTerm-style terminal (iTerm2, kitty,
> WezTerm, GNOME Terminal…). On Windows everything works except the mouse.

## The idea

Agents write most of the code now. What you do all day changed with it:
read what the agent produced, jump around the project, search for a
symbol, fix a line or two, hand the keyboard back. An IDE — project
systems, indexers, language servers, slow startup — is heavy machinery
for that loop.

**ltt** is the opposite. It starts instantly in the terminal you
already keep open next to your agent, shows folders on the left and code
on the right, searches the whole tree fuzzily as you type, and gets out
of the way. No configuration, no plugins, no project files, zero
dependencies: ~7k lines of C# in a 190 KB assembly (.NET 10).

```
 ltt · /home/you/project/src/Program.cs
▾ project/            │ Program.cs ● ×  notes.txt ×
  ▾ src/          48K │   1  using System;
      Program.cs  21K │   2
      FileTree.cs 17K │   3  class Program { ... }
  ▸ docs/        5.1K │
  README.md      4.6K │
──────────────────────┴──────────────────────────────────
 Search results for "mngr": 1/3 · Enter opens · Ctrl+G next
 [EDITOR] Saved: Program.cs              Ln 3/120, Col 1
 Esc to tree · Ctrl+F find · Ctrl+L go to line · Ctrl+S save
```

## Why it feels fast

- ⚡ **Instant on** — nothing is indexed or scanned at startup; the tree
  loads lazily, one folder at a time, only when you expand it.
- 🪶 **Cheap frames** — only the screen cells that changed are sent to the
  terminal, and only the visible part of a line is highlighted. Smoke-tested
  on a 25 MB / 1M-line file: opening, jumping to the end, typing and undo
  stay smooth; the undo history keeps only the lines you touched.
- 🔎 **Search keeps up with typing** — fuzzy content search runs live from
  2 characters and streams files instead of loading them whole
  (~40 ms across this repo, process startup included).
- 🤖 **Made for agent workflows** — when your agent (or git, or a build)
  creates, renames or deletes files, the tree updates by itself. An open
  tab follows its file: a clean tab
  reloads when the agent rewrites it, a tab with your unsaved edits is
  marked ⚠ and saving it asks before overwriting the agent's version.

## Keys

> [!TIP]
> <kbd>Esc</kbd> always means "back": editor → tree, close a prompt, hide the
> search panel, cancel a confirmation.

### Everywhere

| Key | Does |
|---|---|
| <kbd>Ctrl+F</kbd> · <kbd>Ctrl+F+F</kbd> | search selected/open file or folder · search all folders (press <kbd>Ctrl+F</kbd> twice) |
| <kbd>Ctrl+L</kbd> | go to a line, or to `file:line` as compilers and agents print it |
| <kbd>Ctrl+R</kbd> | rename the selected file/folder or the open file |
| <kbd>Ctrl+G</kbd> / <kbd>Ctrl+Shift+G</kbd> | next / previous search hit |
| <kbd>Ctrl+S</kbd> · <kbd>Ctrl+Z</kbd> · <kbd>Ctrl+Y</kbd> / <kbd>Ctrl+Shift+Z</kbd> | save · undo · redo |
| <kbd>Ctrl+O</kbd> · <kbd>Ctrl+N</kbd> · <kbd>Ctrl+Shift+N</kbd> | add root · new file · new folder |
| <kbd>Ctrl+W</kbd> · <kbd>Alt+1..9</kbd> · <kbd>Ctrl+PgUp</kbd> / <kbd>Ctrl+PgDn</kbd> | close tab · jump to tab · cycle tabs |
| <kbd>Ctrl+Q</kbd> | quit |

### Tree (left panel)

| Key | Does |
|---|---|
| <kbd>Enter</kbd> / <kbd>→</kbd> · <kbd>←</kbd> | open file (on a folder: expand) · collapse |
| <kbd>Tab</kbd> | to the editor (<kbd>Esc</kbd> returns) |
| <kbd>Ctrl+R</kbd> · <kbd>Ctrl+Delete</kbd> · <kbd>Delete</kbd> | rename · delete from disk · remove root from panel |

### Editor (right panel)

| Key | Does |
|---|---|
| <kbd>Shift+arrows</kbd> | extend selection (mouse drag and double-click work too) |
| <kbd>Option+←→</kbd> / <kbd>Ctrl+←→</kbd> · <kbd>Option+↑↓</kbd> | jump by words · by paragraphs |
| <kbd>Option+Backspace</kbd> / <kbd>Ctrl+Backspace</kbd> | delete the word before the cursor |
| <kbd>Ctrl+Home</kbd> / <kbd>Ctrl+End</kbd> · <kbd>PgUp</kbd> / <kbd>PgDn</kbd> | document start/end · page |
| <kbd>Ctrl+A</kbd> · <kbd>Ctrl+C</kbd> / <kbd>Ctrl+X</kbd> / <kbd>Ctrl+V</kbd> | select all · copy / cut / paste via the system clipboard |

### Prompts and search

| Key | Does |
|---|---|
| <kbd>Tab</kbd> · <kbd>↑</kbd> / <kbd>↓</kbd> · <kbd>→</kbd> | cycle path completions · accept ghost suggestion |
| <kbd>Ctrl+U</kbd> | clear the whole input line |
| <kbd>↑</kbd> / <kbd>↓</kbd> · <kbd>PgUp</kbd> / <kbd>PgDn</kbd> · <kbd>Enter</kbd> | move in live results · open the hit |
| <kbd>S</kbd> / <kbd>D</kbd> / <kbd>R</kbd> / <kbd>Esc</kbd> | in confirmations: save (overwrite) / don't save (delete) / take the disk version / cancel |

## Good to know

- <kbd>Ctrl+F</kbd> searches from the focused target: in the tree, inside
  the selected file or recursively under the selected folder/root; in the
  editor, the open file including unsaved edits. Pressing <kbd>Ctrl+F</kbd>
  again widens the same query to all folders — that's <kbd>Ctrl+F+F</kbd>.
- No shortcut uses a function key. Bare <kbd>Shift</kbd> only extends the
  selection with the arrow keys; every other <kbd>Shift</kbd> combination
  comes with <kbd>Ctrl</kbd> (<kbd>Ctrl+Shift+G</kbd>,
  <kbd>Ctrl+Shift+N</kbd>, <kbd>Ctrl+Shift+Z</kbd>) and needs a
  kitty-protocol terminal (kitty, WezTerm, Ghostty, foot) to be told apart
  from its unshifted form. Fallbacks: type a trailing `/` in the new-file
  prompt to make a folder; <kbd>Ctrl+Y</kbd> redoes.
- Shortcuts follow the letters of your keyboard layout (AZERTY, QWERTZ…);
  with a Cyrillic or other non-Latin layout they use the physical keys.
  In kitty-protocol terminals <kbd>Cmd</kbd> works like <kbd>Ctrl</kbd>
  on a Mac (<kbd>Cmd+S</kbd> saves, <kbd>Cmd+←</kbd> goes to the line start).
- <kbd>Ctrl+V</kbd> pastes the system clipboard; your terminal's own paste
  works too (one undo step, no auto-indent) — and is the only route over
  SSH, where the clipboard can't be read back. <kbd>Ctrl+C</kbd> copies a
  selection or cancels a prompt; it never quits (that's <kbd>Ctrl+Q</kbd>).
- Files are saved exactly as they were read: encoding (UTF-8 with or
  without a BOM, UTF-16, Latin-1 for anything that is not valid UTF-8),
  line endings and the final newline stay as they were, tabs stay tabs
  (shown 4 wide). A save replaces the file in one atomic rename, keeping
  its permissions; a file with other hard links, or in a read-only folder,
  is overwritten in place instead. Read-only files, pipes and devices are
  refused. Lines over 20,000 characters (minified code, logs) are shown
  without syntax colours.
- The tree watches its folders (`FileSystemWatcher`, polling fallback) and
  shows sizes, largest first: files exactly, folders counted in the
  background (`≥` while still counting) and counted again whenever
  something inside them changes.
- In a git repository, search covers the files git knows — tracked and
  untracked, minus `.gitignore` (repositories nested inside, and inside
  plain folders, by their own rules); elsewhere it skips `.git`,
  `node_modules` and the like. Binaries and files over 8 MB are skipped in
  folder searches; unsaved edits in open tabs are searched too.
- If ltt crashes, is killed (SIGTERM, SIGHUP) or loses its terminal,
  unsaved tabs are written to a recovery folder (`~/Library/Application
  Support/ltt/recovery` on macOS, `~/.local/share/ltt/recovery` on Linux)
  and reopen as unsaved tabs on the next start.
