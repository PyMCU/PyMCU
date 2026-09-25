"""A filesystem refusal of the output path was reported as a compiler crash on the user's source.

The compiler writes two files it is TOLD to write: the assembly (`-o`) and the IR
(`--emit-ir`). When the filesystem refused one of them, the `IOException` walked up to
`CompilerPhaseBase`'s catch-all and came out as

    main.py:1:1: error: InternalCompilerError: IOException: <.NET's words>

which is wrong twice over. Nothing in the compiler is broken, so it is not an internal
error; and the header names main.py, so the one fact a reader needs -- WHICH path could
not be written -- appears nowhere in the diagnostic.

That is how PyMCU#498 stayed hidden. Forty-nine files in this suite passed `/dev/null` as
an output path, so two suites running at once handed pymcuc the same file; the loser of
the race reported a compiler crash on a test's main.py, and the failure read as a
miscompilation of whichever test happened to lose. The names changed on every run, which
is what a miscompilation does not do.

The sweep that keeps the paths apart is in
`test_no_output_path_is_shared_between_tests.py`. This file pins the other half: that when
a write does fail, for any reason, the message says so and names the path.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PROGRAM = "x: int = 1\nx = x + 2\n"


def compile_(tmp_path: Path, emit_ir: str):
    (tmp_path / "main.py").write_text(PROGRAM)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--arch", "avr", "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", emit_ir],
        capture_output=True, text=True, env=dict(os.environ),
    )
    return proc.stdout + proc.stderr


def test_a_good_output_path_still_builds(tmp_path):
    """The anchor. Without it a guard that refused every path would pass every case below."""
    out = compile_(tmp_path, str(tmp_path / "out.mir"))
    assert "[BUILD_OK]" in out, out
    assert (tmp_path / "out.mir").exists()


@pytest.mark.parametrize("kind", ["the-path-is-a-directory", "the-parent-is-a-file"])
def test_a_refused_output_path_is_named_and_is_not_a_crash(tmp_path, kind):
    if kind == "the-path-is-a-directory":
        target = tmp_path / "adir"
        target.mkdir()
    else:
        # A regular file where a directory has to go: the compiler's mkdir of the parent
        # fails, which is a different call and a different exception from the open.
        (tmp_path / "afile").write_text("")
        target = tmp_path / "afile" / "out.mir"

    out = compile_(tmp_path, str(target))

    assert "[BUILD_OK]" not in out, out
    # The three claims, separately, so a regression says which one broke.
    assert "InternalCompilerError" not in out, out
    assert "OSError" in out, out
    assert str(target) in out, out
