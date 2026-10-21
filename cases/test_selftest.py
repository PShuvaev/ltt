"""ltt's own quick checks (--self-test, Debug builds only) run as one test."""
import os
import subprocess

import pytest

from harness import build


@pytest.mark.slow
def test_self_tests_pass(tmp_path):
    src = os.environ.get("LTT_SRC")
    if not src:
        pytest.skip("only with a source checkout (not with --ltt-dll)")
    dll = build.build(src, "Debug")
    env = dict(os.environ, LTT_RECOVERY_DIR=str(tmp_path / "recovery"), DOTNET_NOLOGO="1")
    r = subprocess.run(["dotnet", dll, "--self-test"], capture_output=True, text=True, env=env, timeout=600)
    failures = [line for line in r.stdout.splitlines() if line.startswith("FAIL")]
    assert r.returncode == 0 and not failures, "\n".join(failures) or r.stdout[-3000:] + r.stderr[-2000:]
