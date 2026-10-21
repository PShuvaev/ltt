# Known issues

Code review findings (October 2026), grouped in fix order.
✔ = reproduced by running the app; the rest come from reading the code.
Severity: 🔴 high · 🟠 medium · ⚪ low.

## 1. Data integrity

- [x] D1 🔴✔ Saving changes the file even without edits: an extra `\n` on every save, CRLF→LF, a final newline added where there was none (`TextEditor.Save`, `Open`)
- [x] D2 🔴✔ Tabs become 4 spaces on open and are saved that way: Makefile recipes break, whole-file diffs in Go/TSV
- [x] D3 🔴✔ Encoding is not checked: Latin-1 `é` → U+FFFD, UTF-16 silently becomes UTF-8
- [x] D4 🔴✔ An empty selection anchor turns into a real selection after an edit: Shift+← Shift+→ then typing eats characters; Ctrl+End, Shift+→, Backspace ×2 → `ArgumentOutOfRangeException`, the app crashes and every unsaved tab is lost
- [x] D5 🟠✔ Backspace/Delete/arrows split surrogate pairs: one Backspace after an emoji saves U+FFFD
- [x] D6 🟠✔ Non-atomic save: the file is truncated and rewritten in place; an EFBIG-style error is not caught and crashes the app
- [x] D7 🟠✔ No crash/signal handling: an exception, SIGHUP or SIGTERM loses unsaved edits, no recovery files; after SIGTERM the terminal stays raw (mouse, kitty flags, hidden cursor)
- [x] D8 🟠✔ Every undo group copies the whole line array: 200 edits in a 1M-line file hold +1.5 GB
- [x] D9 ⚪✔ An undo group spans Save and selection replacement — undo cannot return to the saved state
- [x] D10 ⚪✔ Undo/Redo drop every comment checkpoint (~0.6 s near the end of a 1M-line file)
- [x] D11 🟠✔ Opening a FIFO (from the tree or a search hit) hangs the UI forever
- [x] D12 🔴✔ File content, file names, snippets and status are written to the terminal raw: a file can set the window title or write the clipboard (OSC 52); an OSC 11 query reply gets typed into the buffer

## 2. Agent next door

- [x] A1 🔴✔ An open tab never notices that the agent changed the file: it shows stale content and Ctrl+S silently overwrites the agent's edits
- [x] A2 🔴 A file the agent deleted or renamed is recreated at the old path by Ctrl+S
- [x] A3 🟠 Ctrl+Delete on a folder silently closes dirty tabs inside it, losing the edits

## 3. Search

- [x] S1 🔴✔ Live refinement filters a set already capped at 500: `config` after `co` finds 0 of 2; on ltt's own src `ReadByte` finds 16 of 40
- [x] S2 🔴✔ Any external change (even an agent's `git status`) cancels a running folder search, leaving `found: 0`
- [x] S3 🔴✔ `packages`, `bin`, `obj` are skipped at any depth — `packages/*` of a JS monorepo is invisible
- [x] S4 🔴✔ Only the first 5000 hits are ranked: an exact match can be crowded out by fuzzy ones; truncation is never shown
- [x] S5 🟠✔ Search follows directory symlinks: two self-links give 500 duplicates, a query without matches never ends
- [x] S6 🟠 The ignore list is hard-coded, `.gitignore` is not used (the tree uses it): search walks `target/`, `dist/`, `.venv/`
- [x] S7 🟠 Fuzzy matching is greedy-leftmost with unlimited gaps: noisy in-file results, MatchStart can land on the wrong word
- [x] S8 ⚪✔ Files over 2 MB are skipped silently, even when searching that very file — one file is now searched whole; folder searches skip files over 8 MB and say how many
- [x] S9 ⚪✔ Esc in the search prompt does not cancel the background scan — the panel pops up later
- [x] S10 ⚪✔ The first Ctrl+G after a search skips hit 1
- [x] S11 ⚪ Overlapping roots duplicate hits; folder search reads the disk, not dirty buffers

## 4. Terminal

- [x] T1 🔴✔ No bracketed paste: a terminal paste goes through auto-indent (staircase), one undo step per line; a multi-line paste into a prompt fires on the first line
- [x] T2 🔴✔ The first frame and every resize send `ESC[3J` and wipe the scrollback; no alternate screen
- [x] T3 🟠✔ Kitty shortcuts follow US key positions: AZERTY Ctrl+A quits, Ctrl+Z closes a tab; QWERTZ Ctrl+Z redoes, `z` at the delete prompt deletes
- [x] T4 🟠✔ `return Read()` inside `TryRead` blocks the main loop until the next key (Ctrl+Space, a lone Shift/Cmd in kitty): no redraw on resize, no tree updates
- [x] T5 🟠✔ Cmd/Super is dropped: Cmd+S in kitty types `s` into the file
- [x] T6 🟠 Windows: Ctrl checks don't exclude Alt — AltGr letters run commands (Polish `ś` saves, German `@` quits) — fixed in the Windows input path, not tested on Windows
- [x] T7 🟠✔ Search-results mode swallows Ctrl+S, Ctrl+Q, Ctrl+W, Ctrl+G
- [x] T8 ⚪✔ `ESC ESC [B` types `[B`; a CSI longer than 32 chars is cut and its tail typed
- [x] T9 ⚪✔ Mouse back/forward buttons read as clicks: "forward" closes a tab, another removes a root without confirmation
- [x] T10 ⚪✔ The numeric keypad does nothing

## 5. Rendering and layout

- [x] U1 🟠✔ Width is counted in UTF-16 chars, not screen cells: with CJK or `⚡` the cursor sits 3 cells short, clicks and markers shift too
- [x] U2 🟠✔ A tab that was not active for a while keeps an old view height: a jump to a search hit can hide the line under the results panel
- [x] U3 🟠✔ Ctrl+W and a tab click in one input burst use a stale index and crash the app
- [x] U4 ⚪✔ A long title is cut on the right, hiding the file name
- [x] U5 ⚪✔ Prompts don't scroll sideways: Ctrl+N prefills the full path and the typed name is invisible in 80 columns
- [x] U6 ⚪✔ Every frame redraws the whole screen (heavy over SSH); every visible line is tokenized whole (24–52 ms per key on a 3 MB minified line)

## 6. Highlighter

- [x] H1 🟠✔ Unknown extensions (`.md`, `.txt`, no extension) get C rules: an apostrophe opens a string, `//` in a URL becomes a comment, `/*` in a Dockerfile or `.gitignore` comments out the rest of the file
- [x] H2 ⚪✔ Edge cases: `/*/` counts as closed, `#define X /* …` doesn't open a comment, `\"` in a C# verbatim string eats the next `/*`
- [x] H3 ⚪ `ToLowerInvariant` on the extension for every line of every frame; no fuzz test that tokenizing always terminates

## 7. Tree, watcher, file operations

- [x] W1 🟠✔ `git check-ignore` runs synchronously on the UI thread for every loaded folder: Refresh with 32 expanded folders freezes for 588 ms (18 ms outside git)
- [x] W2 🟠✔ `git status` (its `index.lock`), `git commit`, writes inside a collapsed `node_modules` trigger a full Refresh and a search reset; `.git/` events are not filtered
- [x] W3 🟠✔ On macOS any FSEvents error arrives as `IOException` and moves the root to polling forever (≈215 ms per 12k folders on the UI thread)
- [x] W4 🟠 Rows move while folder sizes settle, so a double-click can hit another row — a double-click now needs both clicks on the same entry (the size sort itself stays: ltt is a general tool)
- [x] W5 🟠✔ A root with a trailing slash (`ltt ~/proj/`) breaks root rename; the same root can be added twice; a file argument inside an open root adds a nested duplicate root
- [x] W6 ⚪ The background sizing queue is not cleared on Refresh — detached nodes keep being sized; cancelled counts keep directory handles open
- [x] W7 ⚪✔ A directory symlink inside a folder disables ignore-dimming for the whole folder (git exits 128)
- [x] W8 ⚪✔ Refresh loses expansion of folders inside a collapsed folder; `SelectPath` skips paths starting with `..`
- [x] W9 ⚪ A case-only rename on a case-sensitive volume can strand the file under a hidden temp name
- [x] W10 ⚪✔ Delete protection does not cover ancestors of other roots
- [—] W11 ⚪ ~~Deletes bypass the Trash~~; every file operation does a full Refresh — file operations now re-read only the folders they touch; the Trash was dropped as not needed
- [x] W12 🟠 Paths are compared case-insensitively everywhere (`OrdinalIgnoreCase`): wrong on Linux — `a.txt` opens in the `A.txt` tab

## 8. Prompts, clipboard

- [x] P1 🟠 Relative paths in Ctrl+O resolve against the process cwd — with the installer that's the temp clone
- [x] P2 ⚪✔ → accepts a suggestion keeping the typed case: `Main.cs` instead of `main.cs`
- [x] C1 ⚪✔ Without UTF-8 in `LANG`/`LC_CTYPE`, `pbcopy`/`pbpaste` garble non-ASCII text
- [x] C2 ⚪ Copying blocks the UI for up to 2 s; a hung helper is not killed
- [x] C3 ⚪ Dragging over the title writes OSC 52 on every mouse move, flooding clipboard-manager history

## 9. Installer

- [x] I1 🟠✔ `http://ltt.is-an.ai` serves the script without redirecting to HTTPS (GitHub Pages → "Enforce HTTPS") — enforced: http answers 301 to https
- [x] I2 🟠 The script `cd`s into the temp dir first, so relative arguments resolve inside the clone
- [x] I3 🟠✔ Every run clones and builds `main` again; clones are never removed (19 in `$TMPDIR`); no versions

## 10. Tests

- [x] Q1 🟠 No "open → save → same bytes" test; the save test only checks `StartsWith('X')`
- [x] Q2 🟠 Huge test methods share state; watcher tests depend on timing — 18 small groups, each with its own temp folder; the watcher tests still wait for real file events (up to 5 s)
- [x] Q3 ⚪ Tests ship inside the app assembly; temp folders are still named `ceditor-*`; no fuzz tests for the tokenizer and CSI parsing — `--self-test` is in Debug builds only, folders are `ltt-test-*`, both fuzz tests exist

## 11. Missing features

- [x] F1 ⚪ No select-all, no go-to-line, no quick-open by file name — agents constantly print `path:line` — Ctrl+A and Ctrl+L (line, `path:line[:col]`) are done; opening by name is Ctrl+O (a path with live completion, `path:line[:col]` too)
- [x] F2 ⚪ Tabs show only the file name: several `README.md` look the same

## 12. Reduction (≈17% without losing core features)

- [x] R1 Shared process helper, a key table instead of `if` chains, merged search duplicates, one confirmation state, trimmed comments, test helpers (≈340 lines, no behaviour change) — comments: duplicates, history notes and stale mentions removed, long docs tightened (856 → 769 comment lines); the fixes above still added more code than this removed
- [—] R2 ~~Git status in the tree instead of folder sizes~~ — dropped: sizes stay, the editor is a general tool, not only for git repositories
- [x] R3 Drop title drag-selection (keep click-to-copy), tab pinning, the Ctrl+R+R chord, the legacy Windows render path and `--search`

## 13. Second review (three reviewers, after the fixes above)

Editor core and file I/O:

- [x] V1 🔴✔ A failure creating the temporary file (full HFS+/FAT volume, quota) fell back to overwriting in place and truncated the original; a 240-byte name overflowed the temporary name and lost atomicity — only a read-only folder writes in place now, the temporary name is short
- [x] V2 🟠✔ Redo of a reload put the cursor between the halves of a surrogate pair (saved U+FFFD)
- [x] V3 🟠✔ Word jumps stalled on combining marks (NFD accents, Indic vowel signs); Alt+Backspace deleted only the accent
- [x] V4 🟠✔ Recovery: long names were silently not backed up; a backup cut short by a kill was restored as the whole text
- [x] V5 ⚪✔ A backup taken from the signal thread could mix two states of the text
- [x] V6 ⚪✔ Broken UTF-16 (a lone surrogate, an odd byte) was silently altered on save — it is refused now
- [x] V7 ⚪✔ A stray byte after a UTF-8 BOM made the file unopenable — it opens as Latin-1, byte for byte
- [x] V8 ⚪✔ A long run of flag emoji could be split between two flags' letters
- [x] V9 ⚪✔ Saving a FIFO replaced it with a regular file; saving a hard-linked file detached its other names
- [x] V10 ⚪✔ Every visible line was measured in full on every frame (22 ms for a 3 MB line)

Terminal, rendering and input:

- [x] V11 🟠✔ A broken UTF-8 byte swallowed the next key (Alt+ö typed `�ö`); C1 controls and lone surrogates could be typed into the text
- [x] V12 🟠✔ Resizing away and back within one tick left the screen unrepainted
- [x] V13 ⚪✔ A signal during startup (or a stuck UI thread) left the terminal raw; exit code 0 after a signal (now 128+n)
- [x] V14 ⚪✔ The shell was left in application cursor-key and keypad mode after exit
- [x] V15 ⚪✔ Long lines were tokenized from their start on every frame (80 ms per key at the end of a 3 MB minified file) — lines over 20,000 characters are drawn without colours
- [x] V16 ⚪✔ Alt+1..9 and Alt+B/F looked at the typed character, so they failed on AZERTY and Cyrillic layouts
- [x] V17 ⚪✔ A click on the ‹ cell put the cursor outside the view

App, search, tree and watcher:

- [x] V18 🔴✔ A file renamed and then edited outside was taken as unchanged: the next Ctrl+S overwrote the edit
- [x] V19 🟠✔ A file moved onto another open file gave two tabs one path, and folder search failed on every key
- [x] V20 🟠✔ Backups written after an internal error were never removed: discarded edits came back on the next start
- [x] V21 🟠✔ Folder sizes went stale (changes deep inside, counts that finished late) and were still shown as exact
- [x] V22 🟠✔ A root inside another root was never searched when the outer listing left it out (a nested repository, a submodule, an ignored folder)
- [x] V23 ⚪✔ Refining live results found fewer lines than a fresh search (the fuzzy window grew with the query; content changes did not mark the results stale)
- [x] V24 ⚪✔ A file list started before an outside change was cached after it
- [x] V25 ⚪✔ Searching a large unopened file froze the UI
- [x] V26 ⚪✔ `git init` did not show `.git` until another change; a root inside `.git` never refreshed; a burst of thousands of folders re-read the whole tree and restarted every watcher
- [x] V27 ⚪✔ macOS names in decomposed Unicode were treated as other files (a second tab, a stale search hit)
- [x] V28 ⚪✔ Delete protection compared paths as written: a root reached through `/private/tmp` was not protected
- [x] V29 ⚪✔ In a plain folder, repositories inside it had their `.gitignore` ignored by search
