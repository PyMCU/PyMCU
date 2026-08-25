# tests/driver/test_compiler_signal_death.py
#
# A compiler that dies on a SIGNAL never judged the program, and must not be reported as
# though it had. Both halves of that used to be wrong: only SIGKILL was retried, so a kill
# delivered as anything else fell straight through, and what came out was "Compilation
# failed (see diagnostics above)" with nothing above -- a message that reads as a rejected
# program and sends the reader looking for an error that was never printed.
#
# This was found in the AVR differential suite, where the pyparser axis spawns a translator
# per module and several builds run at once: macOS reclaims memory from one of them and the
# build fails with an empty screen.

import os
import stat
import subprocess
import sys
from pathlib import Path

import pytest
from rich.console import Console

from src.driver.core.compiler import PyMCUCompiler

pytestmark = pytest.mark.skipif(
    sys.platform == "win32",
    reason="negative return codes do not map to signals on Windows")


def fake_compiler(tmp_path: Path, body: str) -> Path:
    """A stand-in for pymcuc, on disk and executable."""
    script = tmp_path / "pymcuc"
    script.write_text("#!/bin/sh\n" + body)
    script.chmod(script.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    return script


def compiler_for(script: Path) -> PyMCUCompiler:
    c = PyMCUCompiler(Console(quiet=True))
    c.get_compiler_path = lambda: script
    c.get_stdlib_path = lambda verbose=False: ""
    return c


def build(c: PyMCUCompiler, tmp_path: Path):
    src = tmp_path / "main.py"
    src.write_text("def main():\n    pass\n")
    return c.compile(str(src), str(tmp_path / "out.s"), "atmega328p", 16_000_000, {})


def test_a_compiler_killed_by_a_signal_says_so(tmp_path):
    script = fake_compiler(tmp_path, "kill -SEGV $$\n")

    with pytest.raises(RuntimeError) as excinfo:
        build(compiler_for(script), tmp_path)

    assert "killed" in str(excinfo.value)
    assert "SIGSEGV" in str(excinfo.value)


def test_it_does_not_point_at_diagnostics_that_do_not_exist(tmp_path):
    # The whole hazard: a signal death printed nothing, and the message said to go read it.
    script = fake_compiler(tmp_path, "kill -SEGV $$\n")

    with pytest.raises(RuntimeError) as excinfo:
        build(compiler_for(script), tmp_path)

    assert "see diagnostics above" not in str(excinfo.value)


def test_it_says_the_program_is_not_at_fault(tmp_path):
    script = fake_compiler(tmp_path, "kill -ABRT $$\n")

    with pytest.raises(RuntimeError) as excinfo:
        build(compiler_for(script), tmp_path)

    assert "not an error in your code" in str(excinfo.value)


def test_a_signal_other_than_sigkill_is_retried(tmp_path):
    # Only -9 was retried before, so a jetsam kill delivered as anything else failed on the
    # first attempt. Any signal death is unreproducible from the program's side.
    counter = tmp_path / "attempts"
    script = fake_compiler(tmp_path, f"""
n=$(cat {counter} 2>/dev/null || echo 0)
n=$((n + 1))
echo $n > {counter}
if [ "$n" -lt 3 ]; then kill -SEGV $$; fi
exit 0
""")

    build(compiler_for(script), tmp_path)

    assert int(counter.read_text()) == 3, "the first two signal deaths must have been retried"


def test_sigkill_is_still_retried(tmp_path):
    counter = tmp_path / "attempts"
    script = fake_compiler(tmp_path, f"""
n=$(cat {counter} 2>/dev/null || echo 0)
n=$((n + 1))
echo $n > {counter}
if [ "$n" -lt 2 ]; then kill -KILL $$; fi
exit 0
""")

    build(compiler_for(script), tmp_path)

    assert int(counter.read_text()) == 2


def test_an_ordinary_rejection_is_untouched(tmp_path):
    # A non-zero EXIT is the compiler having an opinion about the program, and it printed
    # its reasons. That message must keep pointing at them, and must not be retried.
    counter = tmp_path / "attempts"
    script = fake_compiler(tmp_path, f"""
n=$(cat {counter} 2>/dev/null || echo 0)
echo $((n + 1)) > {counter}
echo "main.py:1:1: error: CompileError: nope" >&2
exit 1
""")

    with pytest.raises(RuntimeError) as excinfo:
        build(compiler_for(script), tmp_path)

    assert "see diagnostics above" in str(excinfo.value)
    assert "killed" not in str(excinfo.value)
    assert int(counter.read_text()) == 1, "a rejected program must not be compiled four times"


def test_a_successful_build_does_not_raise(tmp_path):
    script = fake_compiler(tmp_path, "exit 0\n")

    build(compiler_for(script), tmp_path)
