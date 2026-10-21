"""Find the ltt checkout to test and build it into .build/ of this folder.

The checkout's working tree (uncommitted changes included) is copied to
.build/src and built there, so the run never touches the checkout's own
bin/ and obj/ and never collides with a `dotnet run` in it.
"""
import fcntl
import os
import shutil
import subprocess
from contextlib import contextmanager

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(HERE, ".build")
_TOP_LEVEL = (".csproj", ".props", ".targets")
_TOP_FILES = ("global.json", "nuget.config", "NuGet.Config")


def find_source(explicit=None):
    """The ltt checkout: explicit, $LTT_SRC, the worktree on `main` of this repository, or ../ltt."""
    tried = []
    for cand in (explicit, os.environ.get("LTT_SRC")):
        if cand:
            cand = os.path.abspath(os.path.expanduser(cand))
            if _is_ltt(cand):
                return cand
            tried.append(cand)
    try:
        out = subprocess.run(["git", "-C", HERE, "worktree", "list", "--porcelain"],
                             capture_output=True, text=True, check=True).stdout
        trees, cur = [], {}
        for line in out.splitlines() + [""]:
            if not line:
                if cur:
                    trees.append(cur)
                cur = {}
            elif " " in line:
                k, v = line.split(" ", 1)
                cur[k] = v
        for t in sorted(trees, key=lambda t: t.get("branch") != "refs/heads/main"):
            p = t.get("worktree")
            if p and os.path.realpath(p) != os.path.realpath(HERE) and _is_ltt(p):
                return p
            tried.append(p)
    except (subprocess.CalledProcessError, FileNotFoundError):
        pass
    sibling = os.path.join(os.path.dirname(HERE), "ltt")
    if _is_ltt(sibling):
        return sibling
    tried.append(sibling)
    raise RuntimeError("cannot find the ltt checkout (tried: " + ", ".join(map(str, tried))
                       + "); pass --ltt-src PATH or set LTT_SRC")


def _is_ltt(path):
    return bool(path) and os.path.isfile(os.path.join(path, "ltt.csproj")) and os.path.isdir(os.path.join(path, "src"))


@contextmanager
def _locked(name):
    os.makedirs(BUILD, exist_ok=True)
    with open(os.path.join(BUILD, name + ".lock"), "w") as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(f, fcntl.LOCK_UN)


def _sync(src, dst):
    """Mirror the build inputs of src into dst; unchanged files keep their times (incremental builds)."""
    wanted = set()
    for name in os.listdir(src):
        p = os.path.join(src, name)
        if os.path.isfile(p) and (name.endswith(_TOP_LEVEL) or name in _TOP_FILES):
            wanted.add(name)
    for d, dirs, files in os.walk(os.path.join(src, "src")):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
        for f in files:
            wanted.add(os.path.relpath(os.path.join(d, f), src))
    for rel in wanted:
        a, b = os.path.join(src, rel), os.path.join(dst, rel)
        sa = os.stat(a)
        try:
            sb = os.stat(b)
            if sb.st_size == sa.st_size and int(sb.st_mtime) == int(sa.st_mtime):
                continue
        except FileNotFoundError:
            pass
        os.makedirs(os.path.dirname(b), exist_ok=True)
        shutil.copy2(a, b)
    # files gone from the checkout leave the copy too (bin/obj stay: they are the build's own)
    for d, dirs, files in os.walk(dst):
        rel_d = os.path.relpath(d, dst)
        if rel_d == ".":
            dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
        for f in files:
            rel = os.path.normpath(os.path.join(rel_d, f))
            if rel not in wanted:
                os.remove(os.path.join(d, f))


def build(src, configuration="Release"):
    """Build the checkout; returns the path of ltt.dll."""
    dst = os.path.join(BUILD, "src")
    with _locked("build"):
        os.makedirs(dst, exist_ok=True)
        _sync(src, dst)
        r = subprocess.run(["dotnet", "build", "-c", configuration, "-nologo", "-v", "q", "-clp:NoSummary"],
                           cwd=dst, capture_output=True, text=True,
                           env=dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1"))
        if r.returncode != 0:
            raise RuntimeError(f"building ltt ({configuration}) failed:\n{r.stdout}\n{r.stderr}")
    dll = os.path.join(dst, "bin", configuration, "net10.0", "ltt.dll")
    if not os.path.isfile(dll):
        raise RuntimeError(f"the build did not produce {dll}")
    return dll


@contextmanager
def shared(name):
    """A folder under .build/fixtures for data that is costly to make (big files, many files).

    Yields (path, ready): when ready is False the caller fills the folder;
    it is then marked ready for every later run and every parallel worker.
    """
    base = os.path.join(BUILD, "fixtures")
    path = os.path.join(base, name)
    marker = path + ".ready"
    with _locked("fixture-" + name):
        ready = os.path.exists(marker)
        if not ready:
            if os.path.isdir(path):
                shutil.rmtree(path)
            os.makedirs(path)
        yield path, ready
        if not ready:
            open(marker, "w").close()
