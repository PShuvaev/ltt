"""A sealed-off world for one test: its own HOME, recovery folder, clipboard and PATH.

ltt never sees the real home folder, the real recovery folder (it would
reopen test files on the user's next start) or the real clipboard: PATH
holds only git and fake clipboard tools that keep the text in a file.
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

from .build import BUILD

# Shared fixture data lives in .build/fixtures inside this git worktree: git must not see it as ours.
FIXTURES = os.path.join(BUILD, "fixtures")

GIT_IDENTITY = {
    "GIT_AUTHOR_NAME": "ltt tests", "GIT_AUTHOR_EMAIL": "tests@ltt.invalid",
    "GIT_COMMITTER_NAME": "ltt tests", "GIT_COMMITTER_EMAIL": "tests@ltt.invalid",
}


class Tools:
    """The programs a session needs, found once on the real PATH."""

    def __init__(self, dll, dotnet=None, git=None):
        self.dll = dll
        found = dotnet or shutil.which("dotnet")
        if not found:
            raise RuntimeError("dotnet is not on PATH")
        self.dotnet = os.path.realpath(found)
        g = git or shutil.which("git")
        self.git = os.path.realpath(g) if g else None


class Workspace:
    """root — the test's files (HOME is root/home, TMPDIR root/tmp); the plumbing ltt must not
    see in its tree (PATH tools, the clipboard files, the recovery folder) lives next to it."""

    def __init__(self, base, tools):
        base = os.path.realpath(str(base))
        self.base = base
        self.root = os.path.join(base, "w")
        self.sys = os.path.join(base, "sys")
        os.makedirs(self.root, exist_ok=True)
        os.makedirs(self.sys, exist_ok=True)
        self.tools = tools
        self.home = self._dir("home")
        self.tmp = self._dir("tmp")
        self.recovery = os.path.join(self.sys, "recovery")  # ltt creates it when it needs it
        self.bin = self._dir("bin", self.sys)
        self.clip_file = os.path.join(self.sys, "clipboard")
        self.clip_log = os.path.join(self.sys, "clipboard.log")
        self._make_tools()

    def _dir(self, name, parent=None):
        p = os.path.join(parent or self.root, name)
        os.makedirs(p, exist_ok=True)
        return p

    def _script(self, name, body):
        p = os.path.join(self.bin, name)
        with open(p, "w") as f:
            f.write("#!/bin/sh\n" + body)
        os.chmod(p, 0o755)

    def _make_tools(self):
        clip, log = self.clip_file, self.clip_log
        # the log line comes last: once it is there, the clipboard holds this copy's text
        copy = (f"/bin/cat > '{clip}.part' && /bin/mv '{clip}.part' '{clip}'\n"
                f"echo \"copy LC_ALL=$LC_ALL\" >> '{log}'\n")
        paste = f"echo \"paste LC_ALL=$LC_ALL\" >> '{log}'\n[ -f '{clip}' ] && /bin/cat '{clip}'\nexit 0\n"
        for name in ("pbcopy", "wl-copy"):
            self._script(name, copy)
        for name in ("pbpaste", "wl-paste"):
            self._script(name, paste)
        if self.tools.git:
            os.symlink(self.tools.git, os.path.join(self.bin, "git"))

    # ---------------------------------------------------------------- environment

    def env(self, extra=None, path=None):
        """ltt's environment: nothing from the real one but the temp and user names."""
        user = os.environ.get("USER", "tester")
        e = {
            "HOME": self.home,
            "PATH": path or self.bin,
            "TERM": "xterm-256color",
            "LANG": "en_US.UTF-8",
            "TMPDIR": self.tmp,
            "USER": user,
            "LOGNAME": user,
            "SHELL": "/bin/sh",
            "LTT_RECOVERY_DIR": self.recovery,
            # macOS: .NET finds Application Support through Foundation, which ignores HOME
            "CFFIXED_USER_HOME": self.home,
            "DOTNET_NOLOGO": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_EnableDiagnostics": "0",
            "GIT_CONFIG_NOSYSTEM": "1",
            # git never finds a repository above the workspace or the shared fixtures
            "GIT_CEILING_DIRECTORIES": f"{self.base}:{FIXTURES}",
        }
        for k, v in (extra or {}).items():
            if v is None:
                e.pop(k, None)
            else:
                e[k] = str(v)
        if "LTT_RECOVERY_DIR" not in e:
            # Without it ltt writes backups to its default folder — on macOS the real one, whatever
            # HOME says (a test once left a backup there that the user's next ltt would reopen).
            if sys.platform == "darwin":
                raise RuntimeError("never start ltt without LTT_RECOVERY_DIR on macOS: "
                                   "its default recovery folder is the user's real one")
            if not os.path.realpath(e.get("HOME", "/")).startswith(self.root + os.sep):
                raise RuntimeError("a session without LTT_RECOVERY_DIR needs a HOME inside the workspace")
        return e

    def bin_without(self, *names):
        """A PATH folder like bin but without some tools (e.g. 'git')."""
        d = self._dir("bin-without-" + "-".join(names), self.sys)
        for entry in os.listdir(self.bin):
            if entry not in names and not os.path.lexists(os.path.join(d, entry)):
                os.symlink(os.path.realpath(os.path.join(self.bin, entry)), os.path.join(d, entry))
        return d

    # ---------------------------------------------------------------- files

    def path(self, rel=""):
        """Absolute path of rel inside the workspace (an absolute path is returned as it is)."""
        return rel if os.path.isabs(rel) else os.path.join(self.root, rel)

    def write(self, rel, content="", mode=None, mtime=None):
        """Create or replace a file (str is written as UTF-8 without translating newlines)."""
        p = self.path(rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        data = content.encode("utf-8") if isinstance(content, str) else bytes(content)
        with open(p, "wb") as f:
            f.write(data)
        if mode is not None:
            os.chmod(p, mode)
        if mtime is not None:
            os.utime(p, (mtime, mtime))
        return p

    def append(self, rel, content):
        p = self.path(rel)
        with open(p, "ab") as f:
            f.write(content.encode("utf-8") if isinstance(content, str) else content)
        return p

    def read(self, rel):
        with open(self.path(rel), "rb") as f:
            return f.read().decode("utf-8")

    def read_bytes(self, rel):
        with open(self.path(rel), "rb") as f:
            return f.read()

    def mkdir(self, rel):
        p = self.path(rel)
        os.makedirs(p, exist_ok=True)
        return p

    def exists(self, rel):
        return os.path.lexists(self.path(rel))

    def remove(self, rel):
        """Remove a file, a symlink or a folder tree inside the workspace."""
        p = self.path(rel)
        if not os.path.realpath(p).startswith(self.root) and not os.path.islink(p):
            raise RuntimeError(f"refusing to remove {p}: outside the workspace")
        if os.path.isdir(p) and not os.path.islink(p):
            shutil.rmtree(p)
        else:
            os.remove(p)

    def rename(self, src, dst):
        os.rename(self.path(src), self.path(dst))
        return self.path(dst)

    def files(self, rel=""):
        """Sorted relative paths of every file below rel."""
        base = self.path(rel)
        out = []
        for d, _, fs in os.walk(base):
            out += [os.path.relpath(os.path.join(d, f), base) for f in fs]
        return sorted(out)

    # ---------------------------------------------------------------- git

    def git(self, *args, cwd="", check=True, input=None):
        if not self.tools.git:
            raise RuntimeError("git is not installed")
        env = dict(os.environ, **GIT_IDENTITY, HOME=self.home, GIT_CONFIG_NOSYSTEM="1",
                   GIT_CEILING_DIRECTORIES=f"{self.base}:{FIXTURES}")
        r = subprocess.run([self.tools.git, *args], cwd=self.path(cwd), env=env, input=input,
                           capture_output=True, text=True)
        if check and r.returncode != 0:
            raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr}")
        return r.stdout

    def repo(self, rel="", files=None, commit=True, ignore=None):
        """A git repository at rel with these files ({path: content}) committed; returns its path."""
        p = self.mkdir(rel)
        self.git("init", "-q", "-b", "main", cwd=rel)
        if ignore is not None:
            self.write(os.path.join(rel, ".gitignore"), ignore)
        for name, content in (files or {}).items():
            self.write(os.path.join(rel, name), content)
        if commit:
            self.git("add", "-A", cwd=rel)
            self.git("commit", "-q", "--allow-empty", "-m", "initial", cwd=rel)
        return p

    # ---------------------------------------------------------------- clipboard and recovery

    @property
    def clipboard(self):
        """What ltt last copied through the system tool, or None."""
        try:
            with open(self.clip_file, "rb") as f:
                return f.read().decode("utf-8")
        except FileNotFoundError:
            return None

    def set_clipboard(self, text):
        with open(self.clip_file, "wb") as f:
            f.write(text.encode("utf-8"))

    @property
    def clipboard_log(self):
        """One line per clipboard tool run: 'copy LC_ALL=…' or 'paste LC_ALL=…'."""
        try:
            with open(self.clip_log) as f:
                return f.read().splitlines()
        except FileNotFoundError:
            return []

    def recovery_files(self):
        try:
            return sorted(os.listdir(self.recovery))
        except FileNotFoundError:
            return []
