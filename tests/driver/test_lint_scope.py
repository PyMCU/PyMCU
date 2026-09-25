# tests/driver/test_lint_scope.py
#
# What `pymcu lint` is allowed to say when it finds nothing.
#
# It used to say "this should port cleanly", and it said it about programs that do not
# compile. Measured on four the compiler refuses -- an undefined call, a literal too wide for
# its parameter, an unknown type, a write to a field that does not exist -- lint reports
# nothing for all four. A user doing the right thing was told their program was fine.
#
# These tests assert the two halves of the fix. The report carries what was looked at, so a
# consumer never writes that sentence itself; and no renderer promises anything about
# compiling. Written as a refusal of the OLD wording and not only as a check of the new one,
# because the way this comes back is somebody restoring a friendlier sentence.

import json
import subprocess
import sys
from pathlib import Path

import pytest

from src.driver.commands.lint import LINT_SCOPE, _lint_source


# Refused by the compiler, invisible to lint. The point is that all four are silent here:
# a test built on a program lint DOES flag would pass with the old sentence in place.
REFUSED_BY_COMPILER_SILENT_HERE = [
    "x: uint8 = nosuchname()\n",
    "def pulse(us: uint8) -> uint8:\n    return us\n\ny: uint8 = pulse(480)\n",
    "x: uint9 = 1\n",
    "class C:\n    def __init__(self):\n        self.real = 1\n\nc = C()\nc.nope = 2\n",
]


@pytest.mark.parametrize("src", REFUSED_BY_COMPILER_SILENT_HERE)
def test_a_program_the_compiler_refuses_produces_no_finding(src):
    """The premise of the other tests, pinned so it cannot rot into a false premise.

    If lint ever grows a semantic check that catches one of these, this test fails and
    whoever added it gets to decide what the scope now says, which is the right moment to
    have that thought.
    """
    findings, _flavor = _lint_source(src, "probe.py")
    assert [f for f in findings if f.severity == "error"] == []


def test_scope_names_what_was_not_looked_at():
    assert "names" in LINT_SCOPE["not_checked"]
    assert "types" in LINT_SCOPE["not_checked"]
    assert "imports" in LINT_SCOPE["not_checked"]
    # The way out, so a report is never a dead end for someone who wants the real answer.
    assert LINT_SCOPE["for_correctness_run"] == "pymcu build"


def _run_lint(tmp_path: Path, src: str, *args: str) -> subprocess.CompletedProcess:
    f = tmp_path / "main.py"
    f.write_text(src)
    return subprocess.run(
        [sys.executable, "-m", "src.driver.main", "lint", str(f), *args],
        capture_output=True, text=True, cwd=Path(__file__).resolve().parents[2])


def test_json_report_carries_the_scope(tmp_path):
    r = _run_lint(tmp_path, REFUSED_BY_COMPILER_SILENT_HERE[0], "--json")
    report = json.loads(r.stdout)
    assert report["scope"]["not_checked"] == LINT_SCOPE["not_checked"]


def test_no_renderer_promises_that_the_program_will_build(tmp_path):
    r = _run_lint(tmp_path, REFUSED_BY_COMPILER_SILENT_HERE[0])
    out = r.stdout + r.stderr
    # The exact sentence that shipped, and the claim underneath it however it is worded.
    assert "port cleanly" not in out
    assert "should port" not in out
    # What it says instead is about the findings, which is what it measured.
    assert "No porting blockers found" in out
