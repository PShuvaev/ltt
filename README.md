<div align="center">

# <ins>l</ins>ightweight <ins>t</ins>erminal <ins>t</ins>ext editor

### 👀 look at what the agent wrote · ✏️ touch a line or two · 🪃 toss it back
for the age of coding agents

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![dependencies: 0](https://img.shields.io/badge/dependencies-0-2ea44f?style=flat-square)](ltt.csproj)

</div>

## Quick start - open the current folder

```sh
curl https:/ltt.is-an.ai|sh
```

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
already keep open next to your agent, shows folders on the left and files
on the right, searches the whole tree fuzzily as you type, and gets out
of the way. No configuration, no plugins, no project files, zero
dependencies: plain C# on .NET 10.

It is a general tool, not only for codebases: notes, docs, configs, logs —
any folder of text works the same way. Open several folders side by side,
search them all at once, copy and move files between them; in a git
repository ignored files are dimmed and left out of search.

👀 **Watch the agent** — new files turn green, changed ones yellow, changed lines get marked;
your unsaved edits are never overwritten without a question.

![The agent creates, rewrites and renames files; the tree, the tabs and the change marks follow; a conflicting save asks first](https://raw.githubusercontent.com/PShuvaev/ltt/media/watch.gif)

✏️ **Find and fix** — jump to `file:line`, search every file as you type, fix the line, copy the code back to the agent.

![Jump to file:line, fuzzy search across the project, a one-character fix, the function copied for the agent](https://raw.githubusercontent.com/PShuvaev/ltt/media/fix.gif)

📂 **Any folder** — notes, configs and logs side by side: one search across them, files copied between them.

![Three folders in one panel: search them all, edit a config, copy a log into the notes, write a new note](https://raw.githubusercontent.com/PShuvaev/ltt/media/folders.gif)

## Change it, don't script it

ltt is not scriptable, on purpose: no configuration language, no plugin
API. Customising an editor through scripts in its own little language —
Vimscript, Emacs Lisp — is tedious, fragile and limited to whatever the
editor chose to expose; it is a habit from before language models. Now
it is quicker in every sense to fork ltt and ask Claude, or any coding
agent, to rework whatever gets in your way: the whole editor is a small
C# codebase with no dependencies, and the tests in the `tests` branch
tell the agent whether anything broke. ltt is meant as a minimal base to
reshape that way, not a platform to configure.

## Why a terminal

With AI, most of a rich interface has nothing left to do. Menus, toolbars,
wizards and settings panels exist to make operations findable; now you say
what you want — by voice or in text — and an agent finds the operation. The
agent works in text anyway: files, commands, diffs, logs. Text is the one
medium you both read and write, and it can be searched, diffed, versioned
and sent over SSH.

What stays is looking. Your part of the work moves from doing to checking,
and checking needs a fast, honest view of what the agent wrote, plus a
precise tool for the line it got wrong: words are good for asking for a
change and poor for fixing a single character. Voice and text to say it, a
terminal to look and touch — that is the whole interface. ltt is the
terminal part.

## Why it feels fast

- ⚡ **Instant on** — nothing is indexed or scanned at startup; the tree
  loads lazily, one folder at a time, only when you expand it.
- 🪶 **Cheap frames** — only the screen cells that changed are sent to the
  terminal, and only the visible part of a line is highlighted. Smoke-tested
  on a 25 MB / 1M-line file: opening, jumping to the end, typing and undo
  stay smooth; the undo history keeps only the lines you touched.
- 🔎 **Search keeps up with typing** — fuzzy content search runs live from
  2 characters and streams files instead of loading them whole
  (~35 ms across this repo).
- 🤖 **Made for agent workflows** — when your agent (or git, or a build)
  creates, renames or deletes files, the tree updates by itself. An open
  tab follows its file: a clean tab
  reloads when the agent rewrites it, a tab with your unsaved edits is
  marked ⚠ and saving it asks before overwriting the agent's version.
  What changed since the last commit stands out: new files green, changed
  ones yellow, changed lines marked next to the line numbers (outside git:
  what changed since ltt started).

## Colours from vim

ltt has no syntax highlighter of its own: the colours come from vim. A
highlighter worth having knows hundreds of languages, each with its own
strings, comments, heredocs and regular expressions, and takes years of
fixes to get their corners right. vim has one — over 700 languages and
file formats, refined for decades — and vim is already there on macOS
and on many Linux systems, one package away elsewhere. A second
highlighter inside ltt would be redundant.

So ltt keeps one vim running in the background — no vimrc, no plugins,
no modelines — hands it a copy of each open file and asks only about the
lines on screen; your own `~/.vim/syntax` files work too. ltt never
waits for vim, and without it the text is simply plain.

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
| <kbd>Ctrl+C</kbd> / <kbd>Ctrl+X</kbd> · <kbd>Ctrl+V</kbd> | copy / cut a file or folder · paste it into the selected folder, in any root |
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

- <kbd>Ctrl+Shift</kbd> shortcuts need a terminal with the kitty keyboard
  protocol (kitty, WezTerm, Ghostty, foot); elsewhere a trailing `/` in the
  new-file prompt makes a folder and <kbd>Ctrl+Y</kbd> redoes.
- Shortcuts follow the letters of your keyboard layout (AZERTY, QWERTZ…);
  with a Cyrillic or other non-Latin layout they use the physical keys.
  On a Mac, <kbd>Cmd</kbd> works like <kbd>Ctrl</kbd> in kitty-protocol terminals.
- In the editor <kbd>Ctrl+V</kbd> pastes the system clipboard; your
  terminal's own paste works too and is the only route over SSH.
  <kbd>Ctrl+C</kbd> never quits (that's <kbd>Ctrl+Q</kbd>).
- Pasting in the tree never overwrites: a taken name gets a number
  (`notes 2.txt`). <kbd>Ctrl+C</kbd> on a tree entry also copies its path.
- Files are saved exactly as they were read — encoding (UTF-8, UTF-16,
  UTF-32, Latin-1), BOM, line endings, the final newline and tabs stay as
  they were — in one atomic rename that keeps the file's permissions and owner.
- The tree shows sizes, largest first; folders are counted in the
  background (`≥` while still counting).
- In a git repository search covers the files git knows (tracked and
  untracked, minus `.gitignore`); elsewhere it skips `.git`,
  `node_modules` and the like. Unsaved edits in open tabs are searched too.
- If ltt crashes, is killed or loses its terminal, unsaved tabs go to a
  recovery folder (`~/Library/Application Support/ltt/recovery` on macOS,
  `~/.local/share/ltt/recovery` on Linux) and reopen on the next start.
