"""A literal argument wider than its parameter's declared width is refused, not truncated.

An argument bound to a parameter of a declared integer width arrives narrowed to that width.
That is correct and load-bearing -- it is what a real subroutine has always done -- but it
was also SILENT, so a caller could ask for 480 and the callee see 224 with nothing said.

What that cost, measured: the CircuitPython 1-Wire reset held its line low for 224 us where
the protocol needs 480 (PyMCU#501), two compat layers ran their UART at 50000 baud having
been asked for 115200, and `delay_ms(500)` against a `ms: uint8` deleted half of a blink
because the range fold read the loop test as one the counter could never fail.

The check sits in `VisitCallExpr` BEFORE the split between an @inline expansion and a real
call, because the two lose the value in different places: the expansion narrows the constant
into the IR, while a real call keeps 115200 in the IR and the backend truncates it loading
the parameter's registers. A check on either side alone would cover one of them -- and the
real one four times over, once per backend.

It refuses rather than warns because there is nothing to clean up gradually: across the 561
fixtures and examples of pymcu-avr, with delay_us widened, it fires zero times.
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

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]

PRELUDE = "from pymcu.types import uint8, uint16, const, inline, ptr\n\n" \
          "slot: ptr[uint16] = ptr(0x0100)\n\n"


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(PRELUDE + source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        # The suite runs a second pass with PYMCU_PY_PARSER set in the ambient
        # environment. Asking for the C# front end means clearing it, not merely
        # not setting it, or this case runs the Python front end and says csharp.
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--arch", "avr", "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(tmp_path / "out.mir")],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


INLINE_CALLEE = "@inline\ndef take(x: uint8) -> uint8:\n    return x\n\n"
REAL_CALLEE = "def take(x: uint8) -> uint8:\n    return x\n\n"


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("callee", [
    pytest.param(INLINE_CALLEE, id="inline"),
    pytest.param(REAL_CALLEE, id="subroutine"),
])
def test_an_oversized_literal_is_refused_on_both_paths(tmp_path, callee, py_parser):
    ok, out = compile_(tmp_path, callee + "slot.value = take(480)\n", py_parser)

    assert not ok, out
    # Each claim separately, so a regression says which part of the message broke.
    assert "480 does not fit in 'x'" in out, out
    assert "uint8" in out, out
    assert "224" in out, out            # what the callee would have received
    assert "uint8(480)" in out, out     # the way to ask for the narrowing on purpose

    # Located on the ARGUMENT, not on the statement: the echoed source line is the call, and
    # the caret sits under the literal rather than at the start of the line. A diagnostic that
    # named the right problem in the wrong place is how the DHT reports used to read.
    echoed = [ln for ln in out.splitlines() if ln.strip().endswith("slot.value = take(480)")]
    assert echoed, out
    caret = out.splitlines()[out.splitlines().index(echoed[0]) + 1]
    assert caret.lstrip().startswith("^"), out
    assert caret.index("^") > echoed[0].index("take"), out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_literal_that_fits_still_compiles(tmp_path, py_parser):
    """The anchor. A check that refused every call would pass every test above."""
    ok, out = compile_(tmp_path, INLINE_CALLEE + "slot.value = take(255)\n", py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_explicit_narrowing_is_accepted(tmp_path, py_parser):
    """`uint8(480)` says the truncation is meant, which is what lets this refuse at all.

    It is not a way of silencing the check: the cast goes through the same narrowing, and
    `take(uint8(500))` and `take(244)` produce identical IR once the debug text is removed.
    """
    ok, out = compile_(tmp_path, INLINE_CALLEE + "slot.value = take(uint8(480))\n", py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_const_parameter_is_not_narrowed_and_not_refused(tmp_path, py_parser):
    """`const[uintN]` carries its literal through, so there is nothing to refuse.

    Reporting these would send someone to widen a parameter that needs no widening: the live
    PIC UARTs declare a `const[uint16]` baud and compare it against 115200, and are right to.
    """
    source = ("@inline\n"
              "def baud(rate: const[uint16]) -> uint16:\n"
              "    if rate == 115200:\n"
              "        return 1\n"
              "    return 0\n\n"
              "slot.value = baud(115200)\n")
    ok, out = compile_(tmp_path, source, py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_string_argument_is_not_read_as_an_oversized_number(tmp_path, py_parser):
    """The false positive this check had on its first day, kept as a test.

    `[Led(p) for p in ["PD2", ...]]` expands to calls whose argument is the string's INTERNED
    ID, an IntegerLiteral the desugaring made up. 256 as an id is not 256 as a number, and
    the first version of the check told the author their string did not fit in a uint8.

    What tells them apart is position: a literal the parser built from a token carries a line
    and a column, one a desugaring synthesised carries neither. That is the same discriminator
    `UserError(message, at)` already uses, not a new rule invented for this.
    """
    source = ("class Led:\n"
              "    @inline\n"
              "    def __init__(self, pin: uint8):\n"
              "        self._pin = pin\n\n"
              "pins = [\"PD2\", \"PD3\", \"PD4\"]\n"
              "leds = [Led(p) for p in pins]\n"
              "slot.value = 1\n")
    ok, out = compile_(tmp_path, source, py_parser)
    assert ok, out
