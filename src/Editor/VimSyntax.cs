using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>What a run of characters is, for its colour: vim's highlight groups come down to these (see VimSyntax.Kinds; KindOf relies on their order).</summary>
public enum TokenKind : byte { Normal, Keyword, Str, Comment, Number, Preproc, Added, Changed, Removed }

/// <summary>A highlighted range of the source line (no text is copied).</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Syntax colours from vim. ltt has no highlighter of its own: vim has a mature one (hundreds of
/// languages) and is on most Unix machines, so a second one would be redundant. When vim is on
/// PATH, one vim runs hidden for the session — ex mode, no vimrc, plugins, modelines, swap file or
/// viminfo — with a buffer per tab that mirrors the tab's text. ltt asks it which highlight group
/// each visible character is in and draws the groups in its own colours (see Kinds). vim picks the
/// language of a file itself, by the name and the first lines, with the syntax files of this
/// machine (~/.vim/syntax as well). Without a vim that answers the text is plain. Not on Windows:
/// vim answers through /dev/stdout.
/// </summary>
internal sealed class VimSyntax : IDisposable
{
    /// <summary>Longer lines are drawn plain, and vim is given only their start (minified code, logs).</summary>
    public const int MaxLine = 20_000;

    private const int InlineChars = 64 * 1024; // a bigger text goes to vim through a file
    private const int StartTimeoutMs = 10_000;
    private const int WaitMs = 20; // how long a frame may wait for the colours of text it would draw plain

    private const int Off = 0, Starting = 1, Ready = 2, Failed = 3;
    private int _state;
    private Process? _vim;
    private readonly BlockingCollection<Action<StreamWriter>> _jobs = new();
    private readonly ConcurrentQueue<string> _replies = new();
    private readonly AutoResetEvent _replied = new(false);
    private readonly ManualResetEventSlim _probed = new(), _started = new();
    private int _scriptVersion; // 9: vim9 script; 8: only the old language
    private volatile bool _works; // the script answered a first question
    private bool _answered, _failureShown; // colours were shown; their end was

    // The text of a load or a big edit goes through a private folder (written by the writer thread).
    private string? _dir;
    private int _files;

    private int _nextId, _nextToken;
    private readonly Dictionary<int, (SyntaxColours Owner, SyntaxColours.Query Query)> _asked = new();

    /// <summary>
    /// vim's standard highlight groups as ltt draws them: the first of these names on a group's chain of
    /// links decides (an Operator stays plain though it links to Statement). n plain, k keyword, s string,
    /// c comment, m number, p preprocessor, a/h/r a diff's added, changed and removed lines.
    /// </summary>
    private static readonly (string Group, char Kind)[] Kinds =
    {
        ("Comment", 'c'), ("SpecialComment", 'c'), ("Todo", 'c'),
        ("String", 's'), ("Character", 's'), ("Special", 's'), ("SpecialChar", 's'),
        ("Constant", 'm'), ("Number", 'm'), ("Float", 'm'), ("Boolean", 'm'),
        ("Statement", 'k'), ("Conditional", 'k'), ("Repeat", 'k'), ("Label", 'k'), ("Keyword", 'k'),
        ("Exception", 'k'), ("Type", 'k'), ("StorageClass", 'k'), ("Structure", 'k'), ("Typedef", 'k'),
        ("Title", 'k'),
        ("PreProc", 'p'), ("Include", 'p'), ("Define", 'p'), ("Macro", 'p'), ("PreCondit", 'p'),
        ("Added", 'a'), ("Changed", 'h'), ("Removed", 'r'),
        ("Identifier", 'n'), ("Function", 'n'), ("Operator", 'n'), ("Delimiter", 'n'), ("Tag", 'n'),
        ("Debug", 'n'), ("Underlined", 'n'), ("Ignore", 'n'), ("Error", 'n'),
    };

    // The kind letters in the order of TokenKind; any other letter is plain.
    internal static TokenKind KindOf(char c) => (TokenKind)Math.Max(0, "nkscmpahr".IndexOf(c));

    // Before anything else: the settings, the language detection, and which script language vim speaks.
    private static string[] Bootstrap() => new[]
    {
        "set nocompatible encoding=utf-8",
        "set nomore nomodeline noswapfile undolevels=-1 hidden synmaxcol=0",
        "let g:ltt_bufs = {} | let g:ltt_dirty = {} | let g:ltt_kinds = {}",
        "let g:ltt_std = {" + string.Join(", ", Kinds.Select(k => $"'{k.Group}': '{k.Kind}'")) + "}",
        "syntax on",
        "call writefile(['ltt V ' . (v:version >= 900 ? 9 : 8)], '/dev/stdout')",
    };

    // ltt's side of the conversation, in vim. A buffer per tab (LttLoad), named as its file so that
    // vim's filetype detection sees the name; edits arrive as they are made (LttEdit); LttRuns answers
    // with one line: for each line asked, the byte offsets where a kind of colour starts (spaces and
    // tabs take the colour before them). vim keeps what it worked out for every line it parsed, and
    // in ex mode nothing tells it that an edit made that stale: LttRuns parses from the first edited
    // line down (ltt_dirty) — or, far below an edit, has vim start over and find its footing near the
    // lines asked, as it does when it jumps there itself. These run once per tab or per edit: they are
    // in the old script language, which every vim speaks; LttKind and LttRuns come in two (see WriteLoop).
    private const string Script = """
        function! g:LttLoad(id, name, path)
          call g:LttClose(a:id)
          let b = bufadd(a:name)
          if b == 0 || index(values(g:ltt_bufs), b) >= 0
            let b = bufadd('/ltt/' . a:id . a:name)
          endif
          call setbufvar(b, '&buftype', 'nofile')
          call setbufvar(b, '&swapfile', 0)
          call setbufvar(b, '&undolevels', -1)
          call setbufvar(b, '&modeline', 0)
          call bufload(b)
          let g:ltt_bufs[a:id] = b
          call setbufline(b, 1, readfile(a:path, 'b'))
          call delete(a:path)
          call g:LttFiletype(a:id)
        endfunction
        function! g:LttFiletype(id)
          execute 'noautocmd buffer ' . g:ltt_bufs[a:id]
          setlocal filetype=
          filetype detect
          let g:ltt_dirty[a:id] = 2147483647
        endfunction
        function! g:LttRename(id, name)
          execute 'noautocmd buffer ' . g:ltt_bufs[a:id]
          try
            execute 'keepalt noautocmd file ' . fnameescape(a:name)
          catch
            execute 'keepalt noautocmd file ' . fnameescape('/ltt/' . a:id . a:name)
          endtry
          call g:LttFiletype(a:id)
        endfunction
        function! g:LttClose(id)
          if has_key(g:ltt_bufs, a:id)
            execute 'bwipeout! ' . remove(g:ltt_bufs, a:id)
          endif
        endfunction
        function! g:LttEdit(id, at, count, lines)
          let b = g:ltt_bufs[a:id]
          let both = min([a:count, len(a:lines)])
          if both > 0
            call setbufline(b, a:at + 1, a:lines[: both - 1])
          endif
          if a:count > both
            call deletebufline(b, a:at + both + 1, a:at + a:count)
          elseif len(a:lines) > both
            call appendbufline(b, a:at + both, a:lines[both :])
          endif
          let g:ltt_dirty[a:id] = min([g:ltt_dirty[a:id], a:at + 1])
        endfunction
        function! g:LttEditFile(id, at, count, path)
          let lines = readfile(a:path, 'b')
          call delete(a:path)
          call g:LttEdit(a:id, a:at, a:count, lines)
        endfunction

        """;

    // LttKind and LttRuns run for every character asked: in vim9 script, for a vim that has it.
    private const string Vim9Runs = """
        def g:LttKind(id: number): string
          const key = string(id)
          if has_key(g:ltt_kinds, key)
            return g:ltt_kinds[key]
          endif
          var name = synIDattr(id, 'name')
          var k = 'n'
          for _ in range(20)
            if name == ''
              break
            elseif has_key(g:ltt_std, name)
              k = g:ltt_std[name]
              break
            endif
            name = matchstr(execute('highlight ' .. name), 'links to \zs\S\+')
          endfor
          g:ltt_kinds[key] = k
          return k
        enddef
        def g:LttRuns(id: number, tok: number, first: number, ranges: list<list<number>>)
          var out = 'ltt R ' .. tok .. ' '
          try
            const b = g:ltt_bufs[id]
            if bufnr() != b
              execute 'noautocmd buffer ' .. b
            endif
            const last = first + len(ranges) - 1
            var dirty = g:ltt_dirty[id]
            if dirty < first
              if first - dirty > 3000
                execute 'setlocal syntax=' .. &l:syntax
                dirty = v:numbermax
              else
                for l in range(dirty, first - 1)
                  synID(l, 1, 1)
                endfor
              endif
            endif
            var fields: list<string> = []
            var l = first
            for [b0, b1] in ranges
              var runs = ''
              if b1 > b0
                var prev = ''
                var c = b0
                for cp in str2list(strpart(getline(l), b0, b1 - b0))
                  if cp != 32 && cp != 9
                    const k = g:LttKind(synID(l, c + 1, 1))
                    if k != prev
                      runs ..= c .. k
                      prev = k
                    endif
                  endif
                  c += cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4
                endfor
              else
                synID(l, 1, 1)
              endif
              add(fields, runs)
              l += 1
            endfor
            g:ltt_dirty[id] = dirty <= last ? last + 1 : dirty
            out ..= join(fields, '|')
          catch
            out ..= '!'
          endtry
          writefile([out], '/dev/stdout')
        enddef

        """;

    // The same in the old script language, for a vim before 9.0 (three times slower).
    private const string OldRuns = """
        function! g:LttKind(id)
          if has_key(g:ltt_kinds, a:id)
            return g:ltt_kinds[a:id]
          endif
          let name = synIDattr(a:id, 'name')
          let k = 'n'
          for i in range(20)
            if name ==# ''
              break
            elseif has_key(g:ltt_std, name)
              let k = g:ltt_std[name]
              break
            endif
            let name = matchstr(execute('highlight ' . name), 'links to \zs\S\+')
          endfor
          let g:ltt_kinds[a:id] = k
          return k
        endfunction
        function! g:LttRuns(id, tok, first, ranges)
          let out = 'ltt R ' . a:tok . ' '
          try
            let b = g:ltt_bufs[a:id]
            if bufnr('%') != b
              execute 'noautocmd buffer ' . b
            endif
            let last = a:first + len(a:ranges) - 1
            let dirty = g:ltt_dirty[a:id]
            if dirty < a:first
              if a:first - dirty > 3000
                execute 'setlocal syntax=' . &l:syntax
                let dirty = 2147483647
              else
                for l in range(dirty, a:first - 1)
                  call synID(l, 1, 1)
                endfor
              endif
            endif
            let fields = []
            let l = a:first
            for [b0, b1] in a:ranges
              let runs = ''
              if b1 > b0
                let prev = ''
                let c = b0
                for cp in str2list(strpart(getline(l), b0, b1 - b0))
                  if cp != 32 && cp != 9
                    let k = g:LttKind(synID(l, c + 1, 1))
                    if k !=# prev
                      let runs .= c . k
                      let prev = k
                    endif
                  endif
                  let c += cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4
                endfor
              else
                call synID(l, 1, 1)
              endif
              call add(fields, runs)
              let l += 1
            endfor
            let g:ltt_dirty[a:id] = dirty <= last ? last + 1 : dirty
            let out .= join(fields, '|')
          catch
            let out .= '!'
          endtry
          call writefile([out], '/dev/stdout')
        endfunction

        """;

    // ---------------------------------------------------------------- the process

    private static string? FindVim()
    {
        if (OperatingSystem.IsWindows()) return null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathRooted(dir)) continue; // never a vim from the current folder
            string path = Path.Combine(dir, "vim");
            try
            {
                if (File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute
                        | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private void Start()
    {
        if (Interlocked.CompareExchange(ref _state, Starting, Off) != Off) return;
        if (FindVim() is not { } path)
        {
            Fail();
            return;
        }
        Process? vim = null;
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), // a lone surrogate goes as U+FFFD (see Utf8)
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = "/",
            };
            foreach (string arg in new[] { "-N", "-u", "NONE", "-i", "NONE", "-n", "-es", "-X" }) psi.ArgumentList.Add(arg);
            vim = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { }
        if (vim == null)
        {
            Fail();
            return;
        }
        _vim = vim;
        vim.ErrorDataReceived += (_, _) => { }; // read and dropped: it must never fill up
        vim.BeginErrorReadLine();
        var input = vim.StandardInput;
        input.AutoFlush = false;
        input.NewLine = "\n";
        var output = vim.StandardOutput;
        new Thread(() => ReadLoop(output)) { IsBackground = true, Name = "vim answers" }.Start();
        new Thread(() => WriteLoop(input)) { IsBackground = true, Name = "vim questions" }.Start();
    }

    private void WriteLoop(StreamWriter input)
    {
        try
        {
            foreach (string line in Bootstrap()) input.WriteLine(line);
            input.Flush();
            if (!_probed.Wait(StartTimeoutMs)) return;
            // The script, then LttKind and LttRuns in the newest language this vim speaks, the old one when
            // that one does not work here. A first question shows they work: there is no buffer 0, so the
            // answer is "ltt R 0 !"; then a line that comes whatever happened to them ends the attempt.
            input.Write(Script.Replace("\r\n", "\n"));
            foreach (string runs in _scriptVersion >= 9 ? new[] { Vim9Runs, OldRuns } : new[] { OldRuns })
            {
                _started.Reset();
                input.Write(runs.Replace("\r\n", "\n"));
                input.WriteLine("call LttRuns(0, 0, 1, [])");
                input.WriteLine("call writefile(['ltt READY'], '/dev/stdout')");
                input.Flush();
                if (!_started.Wait(StartTimeoutMs) || _works) break;
            }
            if (!_works || Interlocked.CompareExchange(ref _state, Ready, Starting) != Starting) return;
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                job(input);
                if (_jobs.Count == 0) input.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException
                                       or UnauthorizedAccessException) { }
        finally { Fail(); }
    }

    private void ReadLoop(StreamReader output)
    {
        try
        {
            while (output.ReadLine() is { } read)
            {
                // Ours start with "ltt " — after whatever a wrapper script printed without a line break.
                int at = read.IndexOf("ltt ", StringComparison.Ordinal);
                if (at < 0) continue;
                string line = at == 0 ? read : read[at..];
                if (line.StartsWith("ltt R 0 ", StringComparison.Ordinal)) _works = true;
                else if (line.StartsWith("ltt R ", StringComparison.Ordinal))
                {
                    _replies.Enqueue(line);
                    _replied.Set();
                }
                else if (line.StartsWith("ltt V ", StringComparison.Ordinal))
                {
                    _scriptVersion = int.TryParse(line.AsSpan(6), out int v) ? v : 0;
                    _probed.Set();
                }
                else if (line == "ltt READY") _started.Set();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        Fail(); // vim is gone
    }

    /// <summary>vim does not answer, or is gone: the text is plain from now on.</summary>
    private void Fail()
    {
        if (Interlocked.Exchange(ref _state, Failed) == Failed) return;
        _probed.Set();
        _started.Set();
        _replied.Set();
        try { _jobs.CompleteAdding(); } catch (ObjectDisposedException) { }
        // Killed, not told to quit: a vim stuck in a syntax file reads nothing, and closing its input
        // could wait for a full pipe. Its input closes when ltt ends (if ltt is killed, vim ends at EOF).
        try { _vim?.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException
                                       or AggregateException) { }
    }

    public void Dispose()
    {
        Fail();
        if (_dir != null)
            try { Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Enqueue(Action<StreamWriter> job)
    {
        try { _jobs.Add(job); }
        catch (InvalidOperationException) { } // vim failed
    }

    // ---------------------------------------------------------------- for the main loop

    /// <summary>Colours for one more tab (its text goes to vim the first time they are needed).</summary>
    public SyntaxColours Attach(TextEditor editor) => editor.Colours = new SyntaxColours(this, editor, ++_nextId);

    /// <summary>vim answers, or will (not started yet, starting): false — the text is plain.</summary>
    internal bool Usable => Volatile.Read(ref _state) != Failed;

    /// <summary>Some colours are on their way: the main loop looks for them often.</summary>
    public bool Busy => _asked.Count > 0 || Volatile.Read(ref _state) == Starting;

    /// <summary>Take vim's answers; true when anything came (or vim failed): the screen may change.</summary>
    public bool Poll()
    {
        bool any = false;
        if (!Usable && !_failureShown)
        {
            _failureShown = true;
            _asked.Clear();
            any = _answered; // the colours shown go
        }
        while (_replies.TryDequeue(out string? line))
        {
            int space = line.IndexOf(' ', 6);
            if (space < 0 || !int.TryParse(line.AsSpan(6, space - 6), out int token)) continue;
            if (!_asked.Remove(token, out var asked)) continue;
            asked.Owner.Answer(asked.Query, line.AsSpan(space + 1));
            any = _answered = true;
        }
        return any;
    }

    /// <summary>A frame about to draw text plain waits a moment for its colours, when vim is free to answer at once.</summary>
    internal void Wait(SyntaxColours.Query q)
    {
        long deadline = Environment.TickCount64 + WaitMs;
        while (_asked.ContainsKey(q.Token) && Usable)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return;
            _replied.WaitOne((int)left);
            Poll();
        }
    }

    internal bool Idle => Volatile.Read(ref _state) == Ready && _asked.Count == 0;

    internal int NextToken() => ++_nextToken;

    // ---------------------------------------------------------------- questions and edits

    internal void Load(SyntaxColours c, string path, string[] lines)
    {
        Start();
        Enqueue(w => w.WriteLine($"call LttLoad({c.Id}, {Quote(path)}, {Quote(TextFile(lines))})"));
    }

    internal void Edit(SyntaxColours c, int at, int count, string[] lines)
    {
        long size = 0;
        foreach (string line in lines) size += Math.Min(line.Length, MaxLine) + 1;
        if (size > InlineChars)
            Enqueue(w => w.WriteLine($"call LttEditFile({c.Id}, {at}, {count}, {Quote(TextFile(lines))})"));
        else
            Enqueue(w => w.WriteLine($"call LttEdit({c.Id}, {at}, {count}, [{string.Join(", ", lines.Select(l => Quote(Cut(l))))}])"));
    }

    internal void Rename(SyntaxColours c, string path) => Enqueue(w => w.WriteLine($"call LttRename({c.Id}, {Quote(path)})"));

    internal void Close(SyntaxColours c) => Enqueue(w => w.WriteLine($"call LttClose({c.Id})"));

    internal void Ask(SyntaxColours c, SyntaxColours.Query q)
    {
        if (!Usable) return; // never answered: nothing to wait for
        _asked[q.Token] = (c, q);
        Enqueue(w => w.WriteLine(
            $"call LttRuns({c.Id}, {q.Token}, {q.First + 1}, [{string.Join(",", q.ByteFrom.Select((b, i) => $"[{b},{q.ByteTo[i]}]"))}])"));
    }

    /// <summary>What vim is given of a line: a long one only up to MaxLine, never half of a surrogate pair.</summary>
    internal static ReadOnlySpan<char> Cut(string line) =>
        line.Length <= MaxLine ? line : line.AsSpan(0, char.IsHighSurrogate(line[MaxLine - 1]) ? MaxLine - 1 : MaxLine);

    /// <summary>A vim string: "…" with \ and " escaped and control characters as \x.. (a NUL is \n there).</summary>
    internal static string Quote(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '\\') sb.Append(@"\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c == '\0') sb.Append(@"\n");
            else if (c < ' ' || c == '\x7f') sb.Append(@"\x").Append(((int)c).ToString("x2"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Lines into a new file of the private folder, for vim to read (and delete); its path.</summary>
    private string TextFile(string[] lines)
    {
        if (_dir == null)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"ltt-vim-{Environment.ProcessId}-{Guid.NewGuid():N}");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _dir = dir;
        }
        string path = Path.Combine(_dir, $"{++_files}.txt");
        using var w = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16),
            new UTF8Encoding(false), 1 << 16);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) w.Write('\n');
            w.Write(Cut(lines[i]));
        }
        return path;
    }

    /// <summary>UTF-8 bytes of s[from..to) as vim gets them: a lone surrogate is sent as U+FFFD.</summary>
    internal static int Utf8(string s, int from, int to)
    {
        int n = 0;
        for (int i = from; i < to; i++)
        {
            char c = s[i];
            if (c < 0x80) n++;
            else if (c < 0x800) n += 2;
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { n += 4; i++; }
            else n += 3;
        }
        return n;
    }
}

/// <summary>
/// The colours of one tab's text: what vim answered for the lines in view and around them, moved
/// along with edits until vim answers again (typed text takes the colour before it), and the
/// questions for what is missing — one at a time.
/// </summary>
internal sealed class SyntaxColours
{
    private sealed class Line
    {
        public int No;                 // the line's number now
        public required string Text;   // the text the colours are for
        public int From;               // the colours cover Text[From .. From + Kinds.Length); the rest is plain
        public required byte[] Kinds;  // a TokenKind per character
        public bool Stale;             // the text changed above it, or vim was not asked yet: ask again
    }

    /// <summary>A question to vim: lines from First with the text each had, the part of each asked
    /// (none: not needed), and how the text stood (to find the lines again when the answer comes).</summary>
    internal sealed record Query(int Token, int First, string[] Texts, int[] From, int[] To, int[] ByteFrom,
        int[] ByteTo, long Shifts, int Version, (int, int, int, int) View);

    private const int Keep = 4000; // lines of colours kept, around the view

    private readonly VimSyntax _vim;
    private readonly TextEditor _editor;
    internal readonly int Id;
    private bool _sent, _closed;
    private readonly List<Line> _known = new(); // by No
    private Query? _asked;
    private (int, int, int, int)? _refused; // vim failed this question: not again until the text or the view changes
    private int _top;

    internal SyntaxColours(VimSyntax vim, TextEditor editor, int id)
    {
        _vim = vim;
        _editor = editor;
        Id = id;
    }

    // ---------------------------------------------------------------- the editor's changes

    /// <summary>The whole text is new (opened, put back after an internal error): vim gets it again.</summary>
    internal void Reset()
    {
        _known.Clear();
        _refused = null;
        if (_sent && !_closed && _editor.FilePath is { } path) _vim.Load(this, path, _editor.Lines.ToArray());
    }

    internal void Renamed()
    {
        if (_closed) return;
        if (_sent && _editor.FilePath is { } path) _vim.Rename(this, path);
        foreach (var line in _known) line.Stale = true;
        _refused = null;
    }

    /// <summary>The tab is closed: vim drops its buffer.</summary>
    internal void Close()
    {
        if (_sent && !_closed) _vim.Close(this);
        _closed = true;
        _known.Clear();
    }

    /// <summary>`old` lines at `at` were replaced by `lines`: vim gets the edit, the colours move along.</summary>
    internal void Spliced(int at, IReadOnlyList<string> old, string[] lines)
    {
        if (_closed) return;
        int count = old.Count, n = lines.Length;
        // Typing in a line too long to colour changes nothing vim has, when its start stays.
        bool sameForVim = count == 1 && n == 1 && old[0].Length > VimSyntax.MaxLine && lines[0].Length > VimSyntax.MaxLine
                          && VimSyntax.Cut(old[0]).SequenceEqual(VimSyntax.Cut(lines[0]));
        if (_sent && !sameForVim) _vim.Edit(this, at, count, lines);

        int i = Index(at), j = i;
        Line? head = null, tail = null;
        var inPlace = count == n ? new Line?[n] : null;
        for (; j < _known.Count && _known[j].No < at + count; j++)
        {
            var known = _known[j];
            int k = known.No - at;
            if (known.Text != old[k]) continue;
            if (inPlace != null) inPlace[k] = known;
            if (k == 0) head = known;
            if (k == count - 1) tail = known;
        }
        _known.RemoveRange(i, j - i);
        for (int k = i; k < _known.Count; k++)
        {
            _known[k].No += n - count;
            _known[k].Stale = true; // what vim makes of a line depends on the lines above it
        }
        var moved = new List<Line>();
        if (inPlace != null)
        {
            for (int k = 0; k < n; k++)
                if (inPlace[k] is { } known) moved.Add(Moved(known, known, lines[k], at + k));
        }
        else
        {
            // A line split in two keeps its start in the first and its end in the last; lines joined, both.
            if (n > 0 && (head != null || (n == 1 && tail != null))) moved.Add(Moved(head, n == 1 ? tail : null, lines[0], at));
            if (n > 1 && tail != null) moved.Add(Moved(null, tail, lines[n - 1], at + n - 1));
        }
        _known.InsertRange(i, moved);
    }

    /// <summary>
    /// The colours of a line made of an old line's start (head) and an old line's end (tail) — the same
    /// line for an edit inside it: what lies between takes the colour of the character before it.
    /// </summary>
    private static Line Moved(Line? head, Line? tail, string text, int no)
    {
        int len = text.Length;
        int p = head == null ? 0 : head.Text.AsSpan().CommonPrefixLength(text);
        int s = tail == null ? 0 : CommonSuffix(tail.Text, text);
        if (head != null && ReferenceEquals(head, tail)) s = Math.Min(s, head.Text.Length - p); // no overlap
        s = Math.Min(s, len - p);
        var kinds = new byte[len];
        for (int i = 0; i < len; i++)
            kinds[i] = head != null && i < p ? KindAt(head, i)
                : tail != null && i >= len - s ? KindAt(tail, i - len + tail.Text.Length)
                : i > 0 ? kinds[i - 1] : (byte)TokenKind.Normal;
        return new Line { No = no, Text = text, From = 0, Kinds = kinds, Stale = true };
    }

    private static byte KindAt(Line line, int i) =>
        i >= line.From && i < line.From + line.Kinds.Length ? line.Kinds[i - line.From] : (byte)TokenKind.Normal;

    private static int CommonSuffix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[^(i + 1)] == b[^(i + 1)]) i++;
        return i;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>
    /// Before a frame: asks vim about the lines in view (and a screen above and below) whose colours are
    /// missing or stale; text that would be drawn plain waits a moment for them.
    /// </summary>
    internal void Prepare(int top, int rows, int scrollX, int width)
    {
        if (_closed || _editor.FilePath is not { } path || !_vim.Usable)
        {
            _known.Clear();
            return;
        }
        _top = top;
        var lines = _editor.Lines;
        int end = Math.Min(lines.Count, top + rows);
        bool want = false, bare = false;
        for (int i = top; i < end && !bare; i++)
        {
            string text = lines[i];
            if (text.Length == 0 || text.Length > VimSyntax.MaxLine) continue;
            var known = Find(i);
            if (known == null || known.Text != text) want = bare = true;
            else if (!Covers(known, text, scrollX, width)) want = true;
        }
        var view = (_editor.Version, top, scrollX, width);
        if (!want || _asked != null || _refused == view) return;
        if (!_sent)
        {
            _sent = true;
            _vim.Load(this, path, lines.ToArray());
        }
        int first = Math.Max(0, top - rows), last = Math.Min(lines.Count, end + rows);
        var texts = new string[last - first];
        int[] from = new int[texts.Length], to = new int[texts.Length], byteFrom = new int[texts.Length], byteTo = new int[texts.Length];
        for (int k = 0; k < texts.Length; k++)
        {
            string text = texts[k] = lines[first + k];
            if (text.Length == 0 || text.Length > VimSyntax.MaxLine) continue;
            var known = Find(first + k);
            if (known != null && known.Text == text && Covers(known, text, scrollX, width)) continue;
            (from[k], to[k]) = Window(text, scrollX, width);
            byteFrom[k] = VimSyntax.Utf8(text, 0, from[k]);
            byteTo[k] = byteFrom[k] + VimSyntax.Utf8(text, from[k], to[k]);
        }
        bool idle = _vim.Idle;
        _asked = new Query(_vim.NextToken(), first, texts, from, to, byteFrom, byteTo, _editor.LineShifts, _editor.Version, view);
        _vim.Ask(this, _asked);
        if (bare && idle) _vim.Wait(_asked);
    }

    private static bool Covers(Line known, string text, int scrollX, int width)
    {
        if (known.Stale) return false;
        var (from, to) = Window(text, scrollX, width);
        return known.From <= from && known.From + known.Kinds.Length >= to;
    }

    /// <summary>The part of a line worth colours: what is in view, and half a view to either side.</summary>
    private static (int From, int To) Window(string text, int scrollX, int width)
    {
        int from = scrollX <= width / 2 ? 0 : Cells.IndexAt(text, scrollX - width / 2);
        int to = Cells.IndexAt(text, scrollX + width + width / 2);
        if (to < text.Length) to = Cells.NextBoundary(text, to);
        return (from, to);
    }

    /// <summary>The colour runs of a line, when there are any for this text; false — draw it plain.</summary>
    internal bool Fill(int no, string text, List<Token> tokens)
    {
        if (_closed || !_vim.Usable || Find(no) is not { } known || known.Text != text) return false;
        tokens.Clear();
        int from = Math.Min(known.From, text.Length), end = Math.Min(known.From + known.Kinds.Length, text.Length);
        if (from > 0) tokens.Add(new Token(0, from, TokenKind.Normal));
        int start = from;
        for (int i = from + 1; i <= end; i++)
            if (i == end || known.Kinds[i - known.From] != known.Kinds[start - known.From])
            {
                tokens.Add(new Token(start, i - start, (TokenKind)known.Kinds[start - known.From]));
                start = i;
            }
        if (end < text.Length) tokens.Add(new Token(end, text.Length - end, TokenKind.Normal));
        return true;
    }

    // ---------------------------------------------------------------- vim's answer

    /// <summary>
    /// vim's answer: for each line asked, "byte-offset kind-letter" runs, lines split by '|' ("!" — vim
    /// failed). A line edited since is left alone; lines that moved are found where they went.
    /// </summary>
    internal void Answer(Query q, ReadOnlySpan<char> body)
    {
        if (ReferenceEquals(_asked, q)) _asked = null;
        if (_closed) return;
        if (body is "!")
        {
            _refused = q.View;
            return;
        }
        long shifts = _editor.LineShifts;
        var lines = _editor.Lines;
        int k = 0;
        foreach (var range in body.Split('|'))
        {
            if (k >= q.Texts.Length) break;
            int asked = k++;
            if (q.To[asked] <= q.From[asked]) continue; // not asked
            int no = q.First + asked;
            if (shifts != q.Shifts && (no = _editor.FollowLine(no, q.Shifts)) < 0) continue;
            if (no >= lines.Count || lines[no] != q.Texts[asked]) continue; // edited since
            Put(new Line
            {
                No = no, Text = lines[no], From = q.From[asked], Stale = _editor.Version != q.Version,
                Kinds = Decode(body[range], q.Texts[asked], q.From[asked], q.To[asked], q.ByteFrom[asked]),
            });
        }
        Trim();
    }

    /// <summary>Runs "12k15n…" (a byte offset into the line, then the kind starting there) as a kind per character of text[from..to).</summary>
    internal static byte[] Decode(ReadOnlySpan<char> runs, string text, int from, int to, int byteFrom)
    {
        var kinds = new byte[to - from];
        int r = 0, nextAt = NextRun(runs, ref r, out byte nextKind), b = byteFrom;
        byte kind = (byte)TokenKind.Normal;
        for (int i = from; i < to; i++)
        {
            while (nextAt >= 0 && nextAt <= b)
            {
                kind = nextKind;
                nextAt = NextRun(runs, ref r, out nextKind);
            }
            kinds[i - from] = kind;
            b += VimSyntax.Utf8(text, i, i + 1); // a low surrogate adds nothing: its pair counted all four bytes
            if (char.IsHighSurrogate(text[i]) && i + 1 < to && char.IsLowSurrogate(text[i + 1])) kinds[++i - from] = kind;
        }
        return kinds;
    }

    private static int NextRun(ReadOnlySpan<char> runs, ref int r, out byte kind)
    {
        kind = 0;
        int start = r, at = 0;
        while (r < runs.Length && char.IsAsciiDigit(runs[r])) at = at * 10 + (runs[r++] - '0');
        if (r == start || r >= runs.Length) return -1;
        kind = (byte)VimSyntax.KindOf(runs[r++]);
        return at;
    }

    // ---------------------------------------------------------------- the lines known

    private int Index(int no)
    {
        int lo = 0, hi = _known.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_known[mid].No < no) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private Line? Find(int no)
    {
        int i = Index(no);
        return i < _known.Count && _known[i].No == no ? _known[i] : null;
    }

    private void Put(Line line)
    {
        int i = Index(line.No);
        if (i < _known.Count && _known[i].No == line.No) _known[i] = line;
        else _known.Insert(i, line);
    }

    /// <summary>Colours far from the view go when there are many.</summary>
    private void Trim()
    {
        if (_known.Count <= Keep) return;
        _known.RemoveAll(l => Math.Abs(l.No - _top) > Keep / 2);
    }
}
