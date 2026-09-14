"""A refusal reached through a property setter is placed on the assignment (PyMCU#306).

`pin.pull = digitalio.Pull.DOWN` is refused, correctly: an AVR has no internal pull-down.
Where it was refused was not. The message came out at `main.py:109:32` for a nine-line
program, which is the position of the `2` in the CircuitPython layer's setter, printed
against the file the user wrote.

The two halves of that location arrived from different places. Every other expansion tells
the rest of the generator which file it is lowering; the property-setter expansion did not,
so the guard that drops an argument's position when the call is inside a library saw an empty
path, read it as "the entry file", and kept a position belonging to a file the reader has
never opened.

The layer under test here is a plain two-file program rather than the CircuitPython package,
so the test pins the compiler's rule and not a particular library's line numbers.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

HEADER = re.compile(r"^(?P<path>[^\s:]+):(?P<line>\d+):(?P<col>\d+): error:", re.MULTILINE)

# The library. Its setter refuses one value, two @inline levels down, and the `2` it passes
# sits at a line and column that exist in this file and nowhere else.
LIB = """from pymcu.exceptions import CompileError
from pymcu.types import uint8, inline


@inline
def act(n: uint8):
    if n == 2:
        raise CompileError("value 2 is not supported on this chip")


class Dev:
    @inline
    def __init__(self):
        self._m = 0

    @property
    def mode(self) -> uint8:
        return self._m

    @mode.setter
    def mode(self, p: uint8):
        self._m = p
        match p:
            case 2:
                act(2)
            case _:
                act(0)
"""

# The program. Line 4 is the assignment the reader has to change.
MAIN = """from lib import Dev


d = Dev()
d.mode = 2
"""


def _diagnose(tmp_path: Path, py_parser: bool):
    (tmp_path / "lib.py").write_text(LIB)
    (tmp_path / "main.py").write_text(MAIN)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB), "-o", "/dev/null"],
        capture_output=True, text=True, env=env,
    )
    out = proc.stdout + proc.stderr
    m = HEADER.search(out)
    assert m, f"expected a diagnostic, got:\n{out}"
    return Path(m.group("path")).name, int(m.group("line")), out


@pytest.mark.parametrize("py_parser", [False, True], ids=["cs-parser", "py-parser"])
def test_the_refusal_names_the_assignment_not_the_librarys_line(tmp_path, py_parser):
    name, line, out = _diagnose(tmp_path, py_parser)
    assert "value 2 is not supported" in out
    assert name == "main.py"
    assert line == 5, "the assignment, which is the line the reader can change"


@pytest.mark.parametrize("py_parser", [False, True], ids=["cs-parser", "py-parser"])
def test_the_line_it_names_exists_in_the_file_it_names(tmp_path, py_parser):
    # The failure this replaces named a line past the end of the program, so the weaker
    # property is worth stating on its own: whatever line is reported, the named file has it.
    name, line, _ = _diagnose(tmp_path, py_parser)
    assert name == "main.py"
    assert 1 <= line <= len(MAIN.rstrip("\n").split("\n"))
