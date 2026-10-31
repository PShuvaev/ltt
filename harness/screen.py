"""A frame of ltt's screen, read back into its parts: title, tree, tab bar, editor lines, results, status, help.

The layout mirrors App.Render.cs: row 0 is the title; the tree takes the
left panel from row 1; the tab bar is row 1 of the right panel and the
editor starts at row 2; the search results panel (when shown) sits above
the status line (rows - 2); the help line is the last row.
"""
import re
from dataclasses import dataclass, field

# ltt draws with ConsoleColor; Screen.cs maps them to SGR 30-37/90-97, which pyte names like this.
COLORS = {
    "Black": "black", "DarkBlue": "blue", "DarkGreen": "green", "DarkCyan": "cyan",
    "DarkRed": "red", "DarkMagenta": "magenta", "DarkYellow": "brown", "Gray": "white",
    "DarkGray": "brightblack", "Blue": "brightblue", "Green": "brightgreen", "Cyan": "brightcyan",
    "Red": "brightred", "Magenta": "brightmagenta", "Yellow": "brightbrown", "White": "brightwhite",
}
_PYTE_FIXES = {"bfightmagenta": "brightmagenta"}  # a typo in pyte 0.8.2's background table
NAMES = {v: k for k, v in COLORS.items()}


def color_name(pyte_color):
    """ConsoleColor name of a pyte colour ('brightgreen' -> 'Green'); 'default' stays as it is."""
    c = _PYTE_FIXES.get(pyte_color, pyte_color)
    return NAMES.get(c, c)


@dataclass(frozen=True)
class Cell:
    ch: str      # "" for the right half of a wide character
    fg: str      # ConsoleColor name: "Green", "DarkGray", ... or "default"
    bg: str


@dataclass
class TreeRow:
    y: int
    name: str            # as shown, without the trailing "/" of a folder
    depth: int
    is_dir: bool
    expanded: bool
    size: str            # "", "4", "1.5K", "≥12M", "…"
    fg: str              # colour of the name
    bg: str
    selected: bool


@dataclass
class Tab:
    name: str            # label without the marks ("a.txt", "README.md · docs")
    dirty: bool          # ●
    changed: bool        # ⚠ changed on disk under unsaved edits
    missing: bool        # ✗ gone from disk
    active: bool
    x0: int
    x1: int
    close_x: int         # the × cell; -1 on a pinned tab, which has none
    pinned: bool = False  # ⚑


@dataclass
class EditorRow:
    y: int
    number: int          # 1-based line number
    mark: str            # " ", "▎", "▁", "▔"
    mark_color: str
    text: str            # the visible text, right spaces stripped
    current: bool        # the line number is highlighted (cursor line, editor focused)


@dataclass
class Hit:
    y: int
    location: str        # "rel/path.txt:12"
    path: str
    line: int
    snippet: str
    selected: bool


_STATUS_RE = re.compile(r"^ \[(TREE|EDITOR|SEARCH|EXACT)\] (.*?)\s*$")
_POS_RE = re.compile(r" Ln (\d+)/(\d+), Col (\d+)(?: · selected: (\d+))? $")


class Snap:
    """One complete frame (never half-drawn: frames end at the synchronized-output close)."""

    def __init__(self, cells, cursor, cursor_hidden, title_osc, frame_no, at):
        self.cells = cells                  # rows of Cell
        self.rows = len(cells)
        self.cols = len(cells[0]) if cells else 0
        self.cursor = cursor                # (x, y)
        self.cursor_hidden = cursor_hidden
        self.window_title = title_osc       # what an OSC 0/2 set — ltt must never set one
        self.frame_no = frame_no
        self.at = at
        self.lines = ["".join(c.ch for c in row) for row in cells]

    # ---- raw access ----

    def cell(self, x, y):
        return self.cells[y][x]

    def fg(self, x, y):
        return self.cells[y][x].fg

    def bg(self, x, y):
        return self.cells[y][x].bg

    def text(self, y, x0=0, x1=None):
        """The text of cells [x0, x1) in row y (wide characters once)."""
        row = self.cells[y][x0:x1]
        return "".join(c.ch for c in row)

    def find(self, needle, rows=None):
        """(x, y) cell of the first occurrence of needle on screen, or None."""
        for y in (rows if rows is not None else range(self.rows)):
            row = self.cells[y]
            s, xs = "", []
            for x, c in enumerate(row):
                for _ in c.ch:
                    xs.append(x)
                s += c.ch
            i = s.find(needle)
            if i >= 0:
                return xs[i], y
        return None

    def has(self, needle):
        return any(needle in line for line in self.lines)

    def dump(self):
        out = [f"--- frame {self.frame_no}, {self.cols}x{self.rows}, cursor {self.cursor}"
               f"{' (hidden)' if self.cursor_hidden else ''} ---"]
        out += [f"{y:2d}|{line}|" for y, line in enumerate(self.lines)]
        return "\n".join(out)

    def __str__(self):
        return self.dump()

    # ---- layout ----

    @property
    def left_w(self):
        w = self.cols
        return min(max(w * 32 // 100, 22), max(22, w - 24))

    @property
    def editor_x(self):
        return self.left_w + 1

    @property
    def status_y(self):
        return self.rows - 3 if self.lines[self.rows - 2].startswith(" Replace with: ") else self.rows - 2

    @property
    def help_y(self):
        return self.rows - 1

    @property
    def too_small(self):
        return "Terminal too small" in self.lines[0]

    @property
    def results_top(self):
        """Row of the search results header, or None when the panel is hidden."""
        for y in range(2, self.status_y):
            if self.lines[y].startswith((" Search results (", " Exact search results (", " Replace in ")):
                return y
        return None

    @property
    def content_bottom(self):
        """First row below the tree and the editor."""
        top = self.results_top
        return top if top is not None else self.status_y

    # ---- title ----

    @property
    def title(self):
        return self.lines[0].strip()

    @property
    def title_path(self):
        """The path in the title, without the ● and [tab i/n] tail."""
        t = self.lines[0].strip()
        if t.startswith("ltt · "):
            t = t[len("ltt · "):]
        t = re.sub(r"( ●)?( \[tab \d+/\d+\])?$", "", t)
        return t

    @property
    def title_dirty(self):
        return re.search(r" ●( \[tab \d+/\d+\])?\s*$", self.lines[0]) is not None

    # ---- status and help ----

    @property
    def status_line(self):
        return self.lines[self.status_y]

    @property
    def mode(self):
        """TREE / EDITOR / SEARCH / EXACT, or PROMPT (a prompt or a search input), or CONFIRM (a question)."""
        m = _STATUS_RE.match(self.status_line)
        if m:
            return m.group(1)
        bg = self.bg(0, self.status_y)
        if bg == "DarkRed":
            return "CONFIRM"
        if bg == "DarkBlue":
            return "PROMPT"
        return None

    @property
    def status(self):
        """The status message (without the [MODE] label and the Ln/Col part); a prompt or question as shown."""
        line = self.status_line
        m = _STATUS_RE.match(line)
        if not m:
            return line.strip()
        msg = m.group(2)
        pm = _POS_RE.search(line)
        if pm:
            msg = line[len(f" [{m.group(1)}] "):pm.start()].rstrip()
        return msg

    @property
    def pos(self):
        """(line, lines, column, selected) from the status line, 1-based; None without an open file."""
        pm = _POS_RE.search(self.status_line)
        if not pm:
            return None
        return int(pm.group(1)), int(pm.group(2)), int(pm.group(3)), int(pm.group(4) or 0)

    @property
    def prompt(self):
        """The prompt or search input line as shown (label, text, ghost and suffix), or None."""
        return self.status_line.strip() if self.mode == "PROMPT" else None

    @property
    def question(self):
        return self.status_line.strip() if self.mode == "CONFIRM" else None

    @property
    def help(self):
        return self.lines[self.help_y].strip()

    # ---- tree ----

    @property
    def tree(self):
        rows = []
        name_w = self.left_w - 8
        for y in range(1, self.content_bottom):
            left = self.cells[y][:self.left_w]
            text = "".join(c.ch for c in left[:name_w])
            stripped = text.lstrip(" ")
            if not stripped.strip():
                continue
            indent = len(text) - len(stripped)
            is_dir = stripped[:1] in ("▾", "▸")
            if is_dir:
                depth, name = indent // 2, stripped[2:].rstrip()
                if name.endswith("/"):
                    name = name[:-1]
            else:
                depth, name = max(0, (indent - 2) // 2), stripped.rstrip()
            name_x = indent + (2 if is_dir else 0)
            size = "".join(c.ch for c in left[name_w:]).strip()
            fg = left[min(name_x, len(left) - 1)].fg
            bg = left[0].bg
            rows.append(TreeRow(y=y, name=name, depth=depth, is_dir=is_dir,
                                expanded=stripped.startswith("▾"), size=size, fg=fg, bg=bg,
                                selected=bg in ("DarkCyan", "DarkGray")))
        return rows

    def tree_row(self, name):
        """The first tree row with this name (as shown), or None."""
        return next((r for r in self.tree if r.name == name), None)

    @property
    def tree_names(self):
        return [r.name for r in self.tree]

    @property
    def tree_selected(self):
        return next((r for r in self.tree if r.selected), None)

    # ---- tab bar ----

    @property
    def tabs(self):
        x0 = self.editor_x
        row = self.cells[1]
        text = "".join(c.ch for c in row[x0:])
        if text.strip() == "(no file)":
            return []
        tabs = []
        x = x0
        n = len(row)
        while x < n:
            # a label is " name [●] [⚠|✗] × ", or " ⚑ name [●] [⚠|✗] " on a pinned tab
            if row[x].ch in ("‹", "›") and row[x].fg == "Yellow":
                x += 1
                continue
            start = x
            pinned = "⚑" in (row[x].ch, row[x + 1].ch if x + 1 < n else "")  # its space may be under ‹
            if pinned:
                # no ×: the label ends where its colours change or the next pinned label begins
                x += 1
                while x < n and (row[x].fg, row[x].bg) == (row[start].fg, row[start].bg) \
                        and not (row[x].ch == " " and x + 1 < n and row[x + 1].ch == "⚑"):
                    x += 1
                if x >= n or row[x].ch == "›":
                    break  # cut off by the edge
                end, close_x = x, -1
            else:
                while x < n and row[x].ch != "×":
                    x += 1
                if x >= n:
                    break
                end = close_x = x
                x += 1
                if x < n and row[x].ch == " ":
                    x += 1
            label = "".join(c.ch for c in row[start:end]).strip()
            if not label:
                continue
            parts = label.split(" ")
            if pinned:
                parts.pop(0)  # the ⚑
            marks = set()
            while parts and parts[-1] in ("●", "⚠", "✗"):
                marks.add(parts.pop())
            tabs.append(Tab(name=" ".join(parts), dirty="●" in marks, changed="⚠" in marks,
                            missing="✗" in marks, active=row[start].bg in ("DarkCyan", "DarkGray"),
                            x0=start, x1=x - 1, close_x=close_x, pinned=pinned))
        return tabs

    @property
    def tab_names(self):
        return [t.name for t in self.tabs]

    @property
    def active_tab(self):
        return next((t for t in self.tabs if t.active), None)

    # ---- editor ----

    @property
    def gutter(self):
        """Gutter width: the line numbers end two cells before the text."""
        ex = self.editor_x
        best = None
        for y in range(2, self.content_bottom):
            seg = "".join(c.ch for c in self.cells[y][ex:ex + 12])
            m = re.match(r"^( *)(\d+)", seg)
            if m:
                g = len(m.group(0)) + 1
                best = g if best is None else max(best, g)
        return max(4, best) if best else None

    @property
    def editor_rows(self):
        """The visible lines of the open file (rows past its end are left out)."""
        g = self.gutter
        if g is None:
            return []
        ex = self.editor_x
        out = []
        for y in range(2, self.content_bottom):
            row = self.cells[y]
            num = "".join(c.ch for c in row[ex:ex + g - 1]).strip()
            if not num.isdigit():
                continue
            mark_cell = row[ex + g - 1]
            out.append(EditorRow(y=y, number=int(num), mark=mark_cell.ch or " ", mark_color=mark_cell.fg,
                                 text="".join(c.ch for c in row[ex + g:]).rstrip(),
                                 current=row[ex + g - 2].fg == "Yellow"))
        return out

    @property
    def editor_lines(self):
        """The visible text of the open file, one string per shown line."""
        return [r.text for r in self.editor_rows]

    @property
    def text_x(self):
        """Screen column of the first text cell of the editor."""
        g = self.gutter
        return None if g is None else self.editor_x + g

    def editor_row(self, number):
        return next((r for r in self.editor_rows if r.number == number), None)

    @property
    def editor_hint(self):
        """The hint shown when no file is open (empty when a file is open)."""
        if self.gutter is not None:
            return ""
        return " ".join(self.text(y, self.editor_x).strip() for y in range(2, self.content_bottom)).strip()

    # ---- search results ----

    @property
    def results_header(self):
        top = self.results_top
        return None if top is None else self.lines[top].strip()

    @property
    def results(self):
        top = self.results_top
        if top is None:
            return []
        hits = []
        for y in range(top + 1, self.status_y):
            line = self.lines[y]
            m = re.match(r"^ (.+?):(\d+) (.*)$", line)
            if not m:
                continue
            hits.append(Hit(y=y, location=f"{m.group(1)}:{m.group(2)}", path=m.group(1), line=int(m.group(2)),
                            snippet=m.group(3).rstrip(), selected=self.bg(0, y) == "DarkCyan"))
        return hits
