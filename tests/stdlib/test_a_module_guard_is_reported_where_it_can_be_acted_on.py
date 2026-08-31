"""A per-chip refusal in a HAL points at the line the reader can do something about.

Which line that is depends on where the failing use is, and getting it wrong in either
direction is easy, so both directions are pinned here.

    use in the reader's own file      caret on THEIR line, guard named in the sentence
    use inside an imported module     caret on the GUARD, whose text is the explanation

Before #241 both went to the use. That is right for the first row and wrong for the second: a
reader who wrote `AnalogPin("A0")` for an ATtiny 4313 was sent to adc/__init__.py:36, a call
that is perfectly correct, in a file they had never opened, for a decision taken fourteen lines
earlier. Nothing on the line they were shown mentioned their chip.

The guard is the one line in these modules a user is ever meant to read.
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


def _diagnose(tmp_path: Path, source: str, target: str, py_parser: bool):
    """(basename, line, column) of the first diagnostic, and the caret line."""
    src = tmp_path / "main.py"
    src.write_text(source)
    # Inherit the environment rather than build one. A stripped env silently disables the
    # CPython front end (it needs python3 and its own resolution), and the failure looks like a
    # divergence between the two front ends rather than a broken harness -- which is exactly how
    # it presented the first time this file was run.
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "-I", str(tmp_path), "-I", str(STDLIB), "-o", "/dev/null"],
        capture_output=True, text=True, env=env,
    )
    out = proc.stdout + proc.stderr
    m = HEADER.search(out)
    assert m, f"expected a diagnostic, got:\n{out}"
    caret = next((l for l in out.splitlines() if l.strip().startswith("^")), "")
    return Path(m.group("path")).name, int(m.group("line")), int(m.group("col")), caret.strip()


ADC = ('from pymcu.hal.adc import AnalogPin\n\n\n'
       'def main() -> None:\n'
       '    a = AnalogPin("A0")\n')

UART = ('from pymcu.hal.uart import UART\n\n\n'
        'def main() -> None:\n'
        '    u = UART(9600)\n')

WIFI = ('from pymcu.hal.wifi import CYW43\n\n\n'
        'def main() -> None:\n'
        '    r = CYW43()\n')


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
@pytest.mark.parametrize("source,target,module,guard_line", [
    (ADC, "attiny4313", "__init__.py", 22),
    (UART, "attiny85", "__init__.py", 49),
])
def test_a_use_inside_the_library_points_at_the_guard(
        tmp_path, source, target, module, guard_line, py_parser):
    """The reported case. The use is in the HAL's own class, so the use tells the reader nothing.

    The line number is asserted because it is the whole point: it has to be the `raise`, not a
    line below it that happens to be in the same file. If the guard moves, this number moves
    with it, and that is a real edit rather than a flake.
    """
    name, line, col, caret = _diagnose(tmp_path, source, target, py_parser)

    assert name == module
    assert line == guard_line
    assert col > 0, "a guard with a real position must not report the column-1 fallback"
    assert caret.startswith("^"), "the caret has to be drawn, not just a line named"


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
def test_a_use_in_the_readers_own_file_still_points_at_their_line(tmp_path, py_parser):
    """PINNED, and it is the half #241 must NOT change.

    `CYW43()` on an ATmega fails at a line the reader wrote, and that line is the one they can
    change. Moving this caret into wifi.py would be the same mistake as the one above, pointing
    the other way: it would take away the only line in the program that is theirs.

    The guard is still named, in the sentence, because they will want to know where the refusal
    came from. Caret and sentence answer two different questions and this test says so.
    """
    name, line, col, caret = _diagnose(tmp_path, WIFI, "atmega328p", py_parser)

    assert name == "main.py", "the reader's own file, not the HAL's"
    assert (line, col) == (5, 9), "the CYW43() call they wrote"
    assert caret.startswith("^")


@pytest.mark.parametrize("source,target", [(ADC, "attiny4313"), (UART, "attiny85"),
                                           (WIFI, "atmega328p")])
def test_both_front_ends_report_the_refusal_identically(tmp_path, source, target):
    """The differential axis cannot see this: both front ends refuse, so no image is compared.

    Worth its own case rather than trusting the parametrization above, because the two rules
    (use in the library, use in the reader's file) are selected by a path that the CPython
    bridge could in principle reach differently.
    """
    hand = _diagnose(tmp_path, source, target, py_parser=False)
    cpython = _diagnose(tmp_path, source, target, py_parser=True)

    assert hand == cpython
