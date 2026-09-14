"""A line a diagnostic quotes inside its own sentence is a line of the reader's file (#303).

`claim()` refuses the second site that asks a resource for another value, and it names the
site that claimed it first. That citation is text in the middle of a message, not the
diagnostic's own header, so none of the machinery that maps a position back to the user's
file touched it.

It needed to. `pymcu build` does not hand the compiler the file the user wrote: a program
that calls `print()` is compiled as a synthetic entry under `dist/_generated`, four lines
longer. The header was mapped back and the citation was not, so one message stated two
different numberings at once and the quoted line pointed past the end of the file --
measured at "already 3 for PD6 at line 43" for a 41-line program.

Both halves of the fix are exercised here, end to end through `pymcu build`: the compiler
writes the citation as `file:line` so it can be recognised, and the driver maps it like a
header. A program with no `print()` gets no preamble, and its lines must come out unchanged
-- which is why the bug survived every test that ran the compiler directly.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

HEADER = re.compile(r"^(?P<path>[^\s:]+):(?P<line>\d+):(?P<col>\d+): error:", re.MULTILINE)
CITED = re.compile(r"already \d+ for \S+ at (?P<file>[\w.]+):(?P<line>\d+)")

PYPROJECT = (
    '[project]\nname = "cited"\nversion = "0.1.0"\nrequires-python = ">=3.11"\n\n'
    '[tool.pymcu]\ntarget = "atmega328p"\nfrequency = 16000000\n'
    'sources = "src"\nentry = "main.py"\n'
)

# Two channels of Timer0 asking for different prescalers. Line 7 claims first, line 8 is
# refused. `print()` on line 9 is what makes the driver inject the stdout preamble.
WITH_PRINT = """from pymcu.hal.pwm import PWM
from pymcu.hal.console import print


def main():
    a = PWM("PD5", 128, 5000)
    b = PWM("PD6", 128, 100)
    print("x")
"""

# The same program without print(): no preamble, so the compiler's own numbering is already
# the reader's. Line 6 claims first, line 7 is refused.
WITHOUT_PRINT = """from pymcu.hal.pwm import PWM


def main():
    a = PWM("PD5", 128, 5000)
    b = PWM("PD6", 128, 100)
"""


def _build(tmp_path: Path, source: str, py_parser: bool) -> str:
    (tmp_path / "pyproject.toml").write_text(PYPROJECT)
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text(source)
    env = dict(os.environ)
    env["COLUMNS"] = "400"
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [os.sys.executable, "-c",
         f"import sys; sys.path.insert(0, {str(REPO)!r}); sys.argv = ['pymcu', 'build']; "
         "from src.driver.main import run_cli; run_cli()"],
        cwd=tmp_path, capture_output=True, text=True, env=env,
    )
    return proc.stdout + proc.stderr


def _positions(out: str) -> tuple[int, int]:
    """(line the diagnostic is reported at, line it quotes for the earlier site)."""
    header = HEADER.search(out)
    assert header, f"expected a diagnostic, got:\n{out}"
    cited = CITED.search(out)
    assert cited, f"expected the message to quote the earlier site, got:\n{out}"
    assert cited.group("file") == "main.py", "the citation names the file it is a line of"
    return int(header.group("line")), int(cited.group("line"))


@pytest.mark.parametrize("py_parser", [False, True], ids=["cs-parser", "py-parser"])
def test_a_program_with_print_quotes_the_line_the_user_wrote(tmp_path, py_parser):
    # Four injected lines sat between the two numberings. The citation used to come out as
    # line 11 for a program whose last line is 8.
    reported, quoted = _positions(_build(tmp_path, WITH_PRINT, py_parser))
    assert reported == 7, "the refusal is at the second channel"
    assert quoted == 6, "the first channel is the site it quotes"


@pytest.mark.parametrize("py_parser", [False, True], ids=["cs-parser", "py-parser"])
def test_a_program_without_print_is_unchanged(tmp_path, py_parser):
    # No preamble, so nothing to map: this is the shape every direct-pymcuc test uses, and
    # it was right all along. It is here so a mapping applied twice would show up.
    reported, quoted = _positions(_build(tmp_path, WITHOUT_PRINT, py_parser))
    assert reported == 6
    assert quoted == 5
