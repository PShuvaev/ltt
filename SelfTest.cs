namespace ConsoleEditor;

/// <summary>Quick checks without a terminal: dotnet run -- --self-test</summary>
internal static class SelfTest
{
    private static int _failures;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    public static int Run()
    {
        _failures = 0;
        TestScoringAndHighlight();
        TestInputParsing();
        TestTreeEditorTabs();
        TestWatcher();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"FAILED: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestScoringAndHighlight()
    {
        // --- Fuzzy matching ---
        Check(FuzzySearch.ScoreQuery("manager", "var manager = 1;", out _, out _) > 900,
            "fuzzy: an exact substring scores high");
        Check(FuzzySearch.ScoreQuery("mngr", "manager lines", out int st, out int len) > 0 && st == 0 && len > 0,
            "fuzzy: subsequence \"mngr\" finds \"manager lines\"");
        Check(FuzzySearch.ScoreQuery("xyzq", "manager lines", out _, out _) < 0,
            "fuzzy: absent characters do not match");
        Check(FuzzySearch.ScoreQuery("Read File", "File.ReadAllLines(path)", out _, out _) > 0,
            "fuzzy: several words, case-insensitive, word order free");
        Check(FuzzySearch.ScoreQuery("read zzz", "File.ReadAllLines(path)", out _, out _) < 0,
            "fuzzy: if even one word fails — no result");
        int exact = FuzzySearch.ScoreQuery("open", "OpenFile()", out _, out _);
        int fuzzy = FuzzySearch.ScoreQuery("opnf", "OpenFile()", out _, out _);
        Check(exact > fuzzy && fuzzy > 0, "fuzzy: an exact hit ranks above a fuzzy one");

        // --- Syntax highlighting ---
        bool inBlock = false;
        var toks = Highlighter.Tokenize("class Foo { string s = \"hi\"; int n = 42; } // note", ".cs", ref inBlock);
        Check(toks.Any(t => t.Kind == TokenKind.Keyword && t.Text == "class"), "highlight: class — keyword");
        Check(toks.Any(t => t.Kind == TokenKind.Str && t.Text.Contains("hi")), "highlight: string is highlighted");
        Check(toks.Any(t => t.Kind == TokenKind.Number && t.Text == "42"), "highlight: number is highlighted");
        Check(toks.Any(t => t.Kind == TokenKind.Comment && t.Text.Contains("note")), "highlight: // comment");
        Check(string.Concat(toks.Select(t => t.Text)) == "class Foo { string s = \"hi\"; int n = 42; } // note",
            "highlight: tokens add up to the original line");
        inBlock = false;
        Highlighter.Tokenize("int a; /* start", ".cs", ref inBlock);
        var mid = Highlighter.Tokenize("middle of the comment", ".cs", ref inBlock);
        Check(inBlock && mid.All(t => t.Kind == TokenKind.Comment), "highlight: multi-line /* */ comment");

    }

    private static void TestInputParsing()
    {
        // --- Terminal input parsing: CSI keys and SGR mouse ---
        var wheel = RawTerminalInput.ParseCsi("<65;40;5", 'M') as MouseInput;
        Check(wheel is { IsWheel: true, WheelDown: true, X: 39, Y: 4, Pressed: true },
            "input: SGR wheel down, coordinates converted to 0-based");
        var click = RawTerminalInput.ParseCsi("<0;10;3", 'M') as MouseInput;
        Check(click is { IsLeftClick: true, X: 9, Y: 2 }, "input: SGR left click");
        var release = RawTerminalInput.ParseCsi("<0;10;3", 'm') as MouseInput;
        Check(release is { Pressed: false, IsLeftClick: false }, "input: button release is not a click");
        var ctrlRight = (RawTerminalInput.ParseCsi("1;5", 'C') as KeyInput)?.Key;
        Check(ctrlRight is { Key: ConsoleKey.RightArrow } && ctrlRight.Value.Modifiers.HasFlag(ConsoleModifiers.Control),
            "input: CSI 1;5C — Ctrl+→");
        var f6 = (RawTerminalInput.ParseCsi("17", '~') as KeyInput)?.Key;
        Check(f6 is { Key: ConsoleKey.F6 }, "input: CSI 17~ — F6");
        var del = (RawTerminalInput.ParseCsi("3", '~') as KeyInput)?.Key;
        Check(del is { Key: ConsoleKey.Delete }, "input: CSI 3~ — Delete");
        Check(RawTerminalInput.ParseCsi("?25", 'l') == null, "input: unknown CSI is ignored");
        var ctrlO = (RawTerminalInput.ParseCsi("111;5", 'u') as KeyInput)?.Key;
        Check(ctrlO is { Key: ConsoleKey.O } && ctrlO.Value.Modifiers.HasFlag(ConsoleModifiers.Control),
            "input: CSI u 111;5 — Ctrl+O (kitty protocol)");
        var ctrlShiftZ = (RawTerminalInput.ParseCsi("122;6", 'u') as KeyInput)?.Key;
        Check(ctrlShiftZ is { Key: ConsoleKey.Z }
              && ctrlShiftZ.Value.Modifiers.HasFlag(ConsoleModifiers.Control)
              && ctrlShiftZ.Value.Modifiers.HasFlag(ConsoleModifiers.Shift),
            "input: CSI u 122;6 — Ctrl+Shift+Z");

        // --- Shift+F on a selected word must not overwrite the selection ---
        Check(App.IsShiftF(new ConsoleKeyInfo('F', ConsoleKey.F, shift: true, alt: false, control: false)),
            "search: Shift+F is recognised as the search shortcut");
        Check(!App.IsShiftF(new ConsoleKeyInfo('F', ConsoleKey.F, shift: false, alt: false, control: false)),
            "search: plain F without Shift stays typing");
        Check(!App.IsShiftF(new ConsoleKeyInfo('F', ConsoleKey.F, shift: true, alt: false, control: true)),
            "search: Ctrl+Shift+F is not confused with Shift+F");
        Check(App.NormalizeSearchSeed("  hello\n brave\tworld  ") == "hello brave world",
            "search: selected text becomes a tidy query");
        Check(App.NormalizeSearchSeed(" \n\t ") == "",
            "search: a whitespace-only selection does not become a query");

    }

    private static void TestTreeEditorTabs()
    {
        // --- Tree + folder search + editor on a temp directory ---
        string tmp = Path.Combine(Path.GetTempPath(), "ceditor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "sub"));
            File.WriteAllText(Path.Combine(tmp, "a.txt"), "first line\nline manager here\n");
            File.WriteAllText(Path.Combine(tmp, "sub", "b.cs"), "// code\nclass LineManager { }\n");

            var tree = new FileTree(tmp);
            Check(tree.Visible.Count == 3 && tree.Visible[0].IsRoot,
                "tree: the root is the first row, with sub/ and a.txt under it");
            tree.MoveDown(); // onto the sub folder (folders first)
            tree.ExpandOrDescend();
            Check(tree.Visible.Count == 4 && tree.Visible.Any(v => v.Name == "b.cs"),
                "tree: right arrow expands a folder");
            tree.CollapseOrAscend();
            Check(tree.Visible.Count == 3, "tree: left arrow collapses a folder");

            File.WriteAllText(Path.Combine(tmp, "c.txt"), "milk and honey\n");
            var hits = FuzzySearch.Search(tmp, "mngr");
            Check(hits.Count == 2 && hits[0].LineNumber == 2,
                "search: \"mngr\" is found by content in both files, recursively");

            // Live search: a grown query refines previous results without the disk.
            var broad = FuzzySearch.Search(tmp, "m");
            var refined = FuzzySearch.Refine(broad, "mngr");
            static string Key(SearchHit h) => h.RelativePath + ":" + h.LineNumber;
            Check(broad.Count == 3
                  && refined.Select(Key).OrderBy(x => x).SequenceEqual(hits.Select(Key).OrderBy(x => x)),
                "search: Refine of a grown query yields the same set as a full search");

            var ed = new TextEditor();
            ed.Open(Path.Combine(tmp, "a.txt"));
            Check(ed.LineCount >= 2 && !ed.Dirty, "editor: file opened, unmodified");
            ed.InsertChar('X');
            Check(ed.Dirty && ed.LineText(0).StartsWith('X'), "editor: inserting a character marks Dirty");
            ed.Save();
            Check(!ed.Dirty && File.ReadAllText(Path.Combine(tmp, "a.txt")).StartsWith('X'),
                "editor: saving writes to disk");

            File.WriteAllText(Path.Combine(tmp, "big.txt"),
                string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}")));
            ed.Open(Path.Combine(tmp, "big.txt"));
            ed.ScrollView(30);
            Check(ed.ScrollY == 30 && ed.Cy == 0, "editor: the wheel scrolls the view, cursor stays");
            ed.ScrollView(999);
            Check(ed.ScrollY == ed.LineCount - ed.ViewHeight
                  && ed.ScrollY + ed.ViewHeight - 1 == ed.LineCount - 1,
                "editor: scrolling to the limit — last line at the bottom edge, not an empty view");
            ed.SetCursor(9, 3);
            Check(ed.Cy == 9 && ed.Cx == 3, "editor: a click places the cursor by line and column");
            tree.Refresh(); // pick up new files from disk
            tree.MoveBy(2);
            Check(tree.Selected?.Name == "big.txt", "tree: the wheel moves the selection in steps");

            // The tree wheel is slowed 5×: 3 rows per 5 notches instead of 15.
            int wheelNum = 0;
            var perNotch = new List<int>();
            for (int i = 0; i < 5; i++) perNotch.Add(App.TreeWheelSteps(ref wheelNum, true));
            Check(perNotch.SequenceEqual(new[] { 0, 1, 0, 1, 1 }) && perNotch.Sum() == 3 && wheelNum == 0,
                "tree wheel: 5 notches down give exactly 3 rows (5× slower)");
            int wheelUp = 0, upTotal = 0;
            for (int i = 0; i < 5; i++) upTotal += App.TreeWheelSteps(ref wheelUp, false);
            Check(upTotal == -3 && wheelUp == 0,
                "tree wheel: symmetric upward, the remainder is kept");

            // --- Several roots in the left panel ---
            var tree2 = new FileTree(new[] { tmp, Path.Combine(tmp, "sub") });
            Check(tree2.Roots.Count == 2 && tree2.Visible.Count(v => v.IsRoot) == 2,
                "multiroot: two hierarchies in one panel");
            Check(!tree2.AddRoot(tmp) && tree2.Roots.Count == 2,
                "multiroot: a duplicate root is not added");
            tree2.SelectPath(Path.Combine(tmp, "sub", "b.cs"));
            Check(tree2.Selected?.Name == "b.cs",
                "multiroot: SelectPath finds the file in the right root");
            var many = FuzzySearch.SearchMany(new[] { tmp, Path.Combine(tmp, "sub") }, "mngr");
            Check(many.Count == 3 && many.Any(h => h.RelativePath == Path.Combine("sub", "b.cs")),
                "multiroot: search covers all roots, paths prefixed with the root name");
            Check(tree2.RemoveRoot(tree2.Roots[1]) && tree2.Roots.Count == 1,
                "multiroot: a root is removed from the panel");
            Check(!tree2.RemoveRoot(tree2.Roots[0]) && tree2.Roots.Count == 1,
                "multiroot: the last root cannot be removed");
            Check(tree2.AddRoot(Path.Combine(tmp, "sub")) && tree2.Roots.Count == 2
                  && tree2.Selected is { IsRoot: true },
                "multiroot: a new root is added and becomes selected");

            // --- Live path completion (Ctrl+O) ---
            string sep = Path.DirectorySeparatorChar.ToString();
            var (cands1, ghost1) = PathCompletion.Compute(tmp + sep + "su", tmp);
            Check(cands1.Count == 1 && cands1[0] == tmp + sep + "sub" + sep && ghost1 == "b" + sep,
                "complete: fragment \"su\" completes to sub/ with ghost text");
            var (cands2, _) = PathCompletion.Compute(tmp + sep, tmp);
            Check(cands2.Count >= 3 && cands2[0].EndsWith("sub" + sep),
                "complete: empty fragment — all directory entries, folders first");
            var (cands3, _) = PathCompletion.Compute("", tmp);
            Check(cands3.Contains("a.txt"),
                "complete: empty input — relative names of the current folder");
            var (cands4, ghost4) = PathCompletion.Compute("/no/such/folder/x", tmp);
            Check(cands4.Count == 0 && ghost4 == "",
                "complete: a nonexistent directory — empty, no crashes");
            Check(PathCompletion.CommonPrefix(new[] { "/tmp/cedemo/", "/tmp/cedemo2/" }) == "/tmp/cedemo",
                "complete: common candidate prefix for Tab");

            // --- Tabs and pinning ---
            var tabs = new TabManager();
            TextEditor OpenEd(string p) { var e = new TextEditor(); e.Open(p); return e; }
            var t1 = tabs.Add(OpenEd(Path.Combine(tmp, "a.txt")));
            var t2 = tabs.Add(OpenEd(Path.Combine(tmp, "sub", "b.cs")));
            var t3 = tabs.Add(OpenEd(Path.Combine(tmp, "big.txt")));
            Check(tabs.Tabs.Count == 3 && tabs.Active == t3, "tabs: a new tab becomes active");
            tabs.TogglePin(t2);
            Check(tabs.Tabs[0] == t2 && tabs.Active == t3,
                "tabs: a pinned tab moves left, the active one stays");
            Check(tabs.RequestClose(t2) == CloseResult.IsPinned && tabs.Tabs.Count == 3,
                "tabs: a pinned tab cannot be closed");
            var t4 = tabs.Add(OpenEd(Path.Combine(tmp, "c.txt")));
            Check(tabs.Tabs[3] == t4 && tabs.Tabs[0] == t2,
                "tabs: a new tab goes to the end, the pinned one stays left");
            t4.Editor.InsertChar('Z');
            Check(tabs.RequestClose(t4) == CloseResult.IsDirty && tabs.Tabs.Count == 4,
                "tabs: a dirty tab requires confirmation");
            tabs.ForceClose(t4);
            Check(tabs.Tabs.Count == 3 && tabs.Active == t3,
                "tabs: closing the active tab switches to the left neighbour");
            tabs.TogglePin(t2);
            Check(tabs.RequestClose(t2) == CloseResult.Closed && tabs.Tabs.Count == 2,
                "tabs: after unpinning, the tab closes");
            Check(tabs.IndexOfPath(Path.Combine(tmp, "a.txt")) >= 0
                  && tabs.IndexOfPath(Path.Combine(tmp, "missing.txt")) < 0,
                "tabs: finding an open tab by path");
            tabs.ActivateRelative(1);
            Check(tabs.Active == t1, "tabs: cyclic tab switching");

            // --- On-disk rename (FileOps.Move): file, folder, case only ---
            string mvOld = Path.Combine(tmp, "mv-old.txt");
            File.WriteAllText(mvOld, "x");
            FileOps.Move(mvOld, Path.Combine(tmp, "mv-new.txt"), isDir: false);
            Check(!File.Exists(mvOld) && File.ReadAllText(Path.Combine(tmp, "mv-new.txt")) == "x",
                "rename: file moved with its contents");
            string caseOld = Path.Combine(tmp, "CaseName.txt");
            File.WriteAllText(caseOld, "y");
            FileOps.Move(caseOld, Path.Combine(tmp, "casename.txt"), isDir: false);
            Check(Directory.GetFiles(tmp).Select(Path.GetFileName).Contains("casename.txt"),
                "rename: a case-only name change works");
            string dirOld = Path.Combine(tmp, "dir-old");
            Directory.CreateDirectory(dirOld);
            File.WriteAllText(Path.Combine(dirOld, "in.txt"), "z");
            FileOps.Move(dirOld, Path.Combine(tmp, "dir-new"), isDir: true);
            Check(File.Exists(Path.Combine(tmp, "dir-new", "in.txt")),
                "rename: folder moved with its contents");

            // --- Selection: word, replace-by-typing, multi-line delete, paragraphs ---
            File.WriteAllText(Path.Combine(tmp, "sel.txt"), "hello brave new world\nsecond line here\n\nlast para\n");
            ed.Open(Path.Combine(tmp, "sel.txt"));
            ed.SetCursor(0, 8);
            ed.SelectWord();
            Check(ed.SelectedText() == "brave" && ed.SelectionCharCount() == 5,
                "select: double-click selects the whole word");
            ed.InsertChar('X');
            Check(ed.LineText(0) == "hello X new world" && !ed.HasSelection,
                "select: typing replaces the selection");
            ed.SetCursor(0, 0);
            ed.SetAnchorHere();
            ed.SetCursor(1, 6);
            Check(ed.SelectedText() == "hello X new world\nsecond",
                "select: dragging yields multi-line text");
            ed.Backspace();
            Check(ed.LineText(0) == " line here" && ed.Cy == 0 && ed.Cx == 0 && !ed.HasSelection,
                "select: Backspace deletes the whole selection");
            ed.ParagraphDown();
            Check(ed.Cy == 2, "nav: Option+↓ — to the next paragraph start");
            ed.ParagraphUp();
            Check(ed.Cy == 0, "nav: Option+↑ — to the previous paragraph start");
            ed.SetCursor(0, 0);
            ed.WordRight();
            Check(ed.Cx == 5, "nav: word jump skips the space and the word");
            ed.Open(Path.Combine(tmp, "sub", "b.cs"));
            ed.SetCursor(0, 0);
            ed.WordRight();
            Check(ed.Cx == 2, "nav: word jump skips the \"//\" punctuation");
            ed.SetCursor(1, 0);
            ed.SetAnchorHere();
            ed.WordRight();
            Check(ed.HasSelection && ed.SelectedText() == "class",
                "select: anchor + word jump = word selection");

            // --- Title: path segments for mouse selection ---
            var (titleText, pathSeg) = App.BuildTitle("/tmp/cedemo2/code.cs", true, false, "");
            Check(titleText == " ConsoleEditor · /tmp/cedemo2/code.cs"
                  && titleText.Substring(pathSeg.Start, pathSeg.Len) == "/tmp/cedemo2/code.cs",
                "title: one path — of the active element — and the segment points exactly at it");
            var (_, noFileSeg) = App.BuildTitle("no file open", false, false, "");
            Check(noFileSeg.Len == 0, "title: with no file open the path segment is not selectable");

            // Auto-copy of the path selection relies on the system utility + OSC 52.
            var clipCands = Clipboard.HelperCandidates();
            Check(clipCands.Count > 0
                  && (!OperatingSystem.IsMacOS() || clipCands[0].File == "pbcopy")
                  && (!OperatingSystem.IsWindows() || clipCands[0].File == "clip"),
                "clipboard: the current OS has a system clipboard utility");

            // --- Undo / Redo ---
            File.WriteAllText(Path.Combine(tmp, "undo.txt"), "one\ntwo\n");
            ed.Open(Path.Combine(tmp, "undo.txt"));
            ed.InsertChar('X');
            ed.InsertChar('Y');
            Check(ed.LineText(0) == "XYone" && ed.Dirty && ed.CanUndo,
                "undo: typing is marked dirty and can be undone");
            ed.Undo();
            Check(ed.LineText(0) == "one" && !ed.Dirty && ed.CanRedo,
                "undo: continuous typing is undone in one step, back to the saved state");
            ed.Redo();
            Check(ed.LineText(0) == "XYone" && ed.Dirty, "undo: redo brings the edit back");
            ed.Undo();
            ed.SetCursor(1, 0);
            ed.InsertChar('Z');
            Check(!ed.CanRedo && ed.LineText(1) == "Ztwo",
                "undo: a new edit after undo clears redo");
            ed.Undo();
            Check(ed.LineText(1) == "two" && !ed.Dirty, "undo: rolling back the second edit");
            ed.SetCursor(0, 3);
            ed.Backspace();
            ed.Backspace();
            Check(ed.LineText(0) == "o", "undo: two Backspaces in a row");
            ed.Undo();
            Check(ed.LineText(0) == "one", "undo: a Backspace run is one undo step");
            ed.SetCursor(0, 0);
            ed.SelectWord();
            ed.DeleteSelection();
            Check(ed.LineText(0) == "" && ed.Dirty, "undo: deleting the selection");
            ed.Undo();
            Check(ed.LineText(0) == "one" && ed.SelectedText() == "one",
                "undo: rolling back a deletion restores text and selection");
            ed.Undo();
            Check(!ed.CanUndo && ed.LineText(0) == "one",
                "undo: empty history — Undo breaks nothing");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }

    }

    private static void TestWatcher()
    {
        // --- Watching folders for external changes (FolderWatcher) ---
        string tmpW = Path.Combine(Path.GetTempPath(), "ceditor-watch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmpW, "sub"));
            File.WriteAllText(Path.Combine(tmpW, "seed.txt"), "seed\n");

            // Deadline for a due batch: an FSW event or polling + debounce.
            static string? WaitDue(FolderWatcher w)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    w.PollIfDue();
                    if (w.TryConsumeDue(out string s, out _)) return s;
                    Thread.Sleep(20);
                }
                return null;
            }

            using var watcher = new FolderWatcher(debounceMs: 50, maxDelayMs: 500, pollIntervalMs: 100);
            watcher.SyncRoots(new[] { tmpW });
            Check(watcher.WatchedRoots.Contains(tmpW) && watcher.UnwatchableRoots.Count == 0,
                "watch: the root is being watched");

            // Let the polling path record a baseline signature — in the app the
            // first tick of the main loop does this.
            for (int i = 0; i < 25; i++) { watcher.PollIfDue(); Thread.Sleep(20); }

            var wtree = new FileTree(tmpW);
            Check(!wtree.Visible.Any(v => v.Name == "watch-new.txt"),
                "watch: the file is not in the tree before its external creation");

            File.WriteAllText(Path.Combine(tmpW, "watch-new.txt"), "came from outside\n");
            string? created = WaitDue(watcher);
            Check(created != null
                  && (created.Contains("watch-new.txt") || created.Contains("contents of")),
                "watch: external file creation noticed (by event or polling), batch handed out after debounce");
            wtree.Refresh();
            Check(wtree.Visible.Any(v => v.Name == "watch-new.txt"),
                "watch: after refresh the new file is visible in the tree");

            File.Move(Path.Combine(tmpW, "watch-new.txt"), Path.Combine(tmpW, "watch-renamed.txt"));
            Check(WaitDue(watcher) != null,
                "watch: external rename noticed");
            wtree.Refresh();
            Check(wtree.Visible.Any(v => v.Name == "watch-renamed.txt")
                  && !wtree.Visible.Any(v => v.Name == "watch-new.txt"),
                "watch: the tree shows the new name");

            File.WriteAllText(Path.Combine(tmpW, "sub", "deep.txt"), "deep in a collapsed folder\n");
            Check(WaitDue(watcher) != null,
                "watch: a change deep inside the root is noticed too (recursive)");
            wtree.Refresh();
            wtree.SelectPath(Path.Combine(tmpW, "sub"));
            wtree.ExpandOrDescend();
            Check(wtree.Visible.Any(v => v.Name == "deep.txt"),
                "watch: the file from a collapsed folder is there on expansion");

            File.Delete(Path.Combine(tmpW, "watch-renamed.txt"));
            Check(WaitDue(watcher) != null,
                "watch: external deletion noticed");
            wtree.Refresh();
            Check(!wtree.Visible.Any(v => v.Name == "watch-renamed.txt"),
                "watch: the deleted file is gone from the tree");

            // Debounce: a fresh mark is not handed out instantly, and is handed out once.
            watcher.MarkDirty("test mark");
            Check(!watcher.TryConsumeDue(out _, out _),
                "watch: no batch is handed out before the debounce");
            Check(WaitDue(watcher) != null && !watcher.TryConsumeDue(out _, out _),
                "watch: a due batch is handed out exactly once");

            watcher.SyncRoots(Array.Empty<string>());
            Check(watcher.WatchedRoots.Count == 0,
                "watch: removing roots stops the watching");
        }
        finally
        {
            try { Directory.Delete(tmpW, recursive: true); } catch (IOException) { }
        }
    }
}
