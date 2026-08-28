"""The width of an unannotated module-level name, at the boundaries where it can go wrong.

#205: `total = 0` at module level stayed uint8 when a function assigned it a uint16, so a
moving average wrapped and went on reporting plausible small numbers. The fix settles the
width in the scan that runs before anything is lowered, which is the only place it can be
settled: a store already emitted at the narrow width would keep writing one byte of a two-byte
name at RUNTIME, and functions are lowered in an order the program does not control.

The silicon tests in pymcu-avr (GlobalAccumulatorWidthTests) pin that the widening HAPPENS.
What is pinned here is the other side, which a compile-and-read test can see and a running
program cannot: the programs the widening must leave alone. A width pass keyed on the type of
the RESULT rather than on the widest OPERAND widens every 8-bit accumulator in the corpus to
16 bits -- `a + b` over two uint8 operands produces a uint16 temporary so the sum cannot
overflow before it is stored -- and that costs flash and SRAM on every part without failing a
single behavioural test. `test_an_eight_bit_accumulator_is_left_alone` is that assertion.
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

STDOUT_IMPORT = "from pymcu.hal.uart import UART as _stdout\n"
STDOUT_OPEN = "    _stdout(115200)\n"
HEAD = STDOUT_IMPORT + "from pymcu.types import uint8, uint16\n\n\n"


def build(tmp_path: Path, source: str):
    (tmp_path / "main.py").write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    return json.loads(mir.read_text())


BYTES = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4}


def global_width(mir, name):
    """The declared width of a module-level name, in bytes."""
    g = next(g for g in mir["globals"] if g["name"] == name)
    return BYTES[g["type"]]


def stores_to(mir, name):
    """The value of every store to `name` in main, in order."""
    body = next(f["body"] for f in mir["functions"] if f["name"].endswith("main"))
    return [i["src"].get("value") for i in body
            if i["$t"] == "copy" and i["dst"].get("name") == name]


def printed_widths(mir):
    """The writer each print() reached for, which carries the width it decided on."""
    body = next(f["body"] for f in mir["functions"] if f["name"].endswith("main"))
    return [i["functionName"].rsplit("_", 1)[-1] for i in body
            if i["$t"] == "call" and "write_decimal" in i["functionName"]]


# --- what the widening must leave alone ---------------------------------------------------

EIGHT_BIT = (
    HEAD
    + "total = 0\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    a: uint8 = 7\n"
    + "    global total\n"
    + "    total = total + a\n"
    + "    total = total + 1\n"
    + "    print(total)\n"
)


def test_an_eight_bit_accumulator_is_left_alone(tmp_path):
    """The invariant that catches the expensive mistake, and it is not hypothetical.

    `uint8 + uint8` promotes to a uint16 temporary so the sum cannot overflow before it is
    stored. A width pass that reads the RESULT type widens this accumulator to 16 bits, and
    every other 8-bit accumulator with it -- silently, since the program still computes the
    right numbers and no behavioural test can see the difference. It shows up as flash and
    SRAM, on a part where SRAM is the scarce thing.

    Wrapping an 8-bit counter is PyMCU's integer model, not a narrowing to be repaired.
    """
    mir = build(tmp_path, EIGHT_BIT)
    assert global_width(mir, "total") == 1, "the 8-bit accumulator was widened"
    assert printed_widths(mir) == ["u8"], printed_widths(mir)


ANNOTATED = (
    HEAD
    + "total: uint16 = 0\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    r: uint16 = 300\n"
    + "    global total\n"
    + "    total = total + r\n"
    + "    print(total)\n"
)


def test_a_written_annotation_is_what_the_name_gets(tmp_path):
    """An annotated name is the author's choice and the scan must not revise it in either
    direction, so this pins the value as well as the width."""
    mir = build(tmp_path, ANNOTATED)
    assert global_width(mir, "total") == 2
    assert stores_to(mir, "total") == [0, 300], stores_to(mir, "total")
    assert printed_widths(mir) == ["u16"], printed_widths(mir)


NARROWER = (
    HEAD
    + "n = 0\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    a: uint8 = 7\n"
    + "    global n\n"
    + "    n = a\n"
    + "    print(n)\n"
)


def test_a_value_that_fits_does_not_widen_the_name(tmp_path):
    """Only a value that does not fit is a reason to widen."""
    mir = build(tmp_path, NARROWER)
    assert global_width(mir, "n") == 1
    assert printed_widths(mir) == ["u8"], printed_widths(mir)


LOCAL = (
    HEAD
    + "def main():\n"
    + STDOUT_OPEN
    + "    total = 0\n"
    + "    r: uint16 = 300\n"
    + "    total = total + r\n"
    + "    print(total)\n"
)


def test_a_local_accumulator_is_untouched(tmp_path):
    """A local already widened before #205, which is what made the module-level case look
    like a scoping quirk rather than a width bug. It must keep doing so."""
    mir = build(tmp_path, LOCAL)
    assert printed_widths(mir) and printed_widths(mir)[0] != "u8", printed_widths(mir)


# --- the boundary the widening does not reach yet -------------------------------------------

INITIALIZER_VS_FUNCTION = (
    HEAD
    + "wide = 300\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    print(wide)\n"
    + "    global wide\n"
    + "    wide = 7\n"
    + "    print(wide)\n"
)


# Was xfail(strict=True) for #212 and collected: it went red when the defect was fixed, which
# is what a strict xfail is for. The reason it carried, "the initializer's own literal is
# dropped once a function assigns the name; measured at 576dee6e, `wide = 300` stores 44", is
# kept here because it is the shape a regression would take.
def test_the_initializers_own_width_survives_a_narrower_store(tmp_path):
    """A function assigning the name NARROWS it, by removing the initializer from the scan.

    `wide = 300` alone is uint16 and prints 300. Adding one function that assigns `wide = 7`
    collapses the name to uint8, and the module-level store becomes `copy const 44`: the
    literal the author wrote is truncated at its own defining store. #205's fix takes the
    width from what functions assign, and the initializer's literal is dropped by
    NarrowLiteralOnlyGlobals before that, so nothing puts it back.

    Marked xfail rather than deleted: the shape is real, it is measured, and it belongs next
    to the tests for the pass it is a gap in.
    """
    mir = build(tmp_path, INITIALIZER_VS_FUNCTION)
    assert global_width(mir, "wide") == 2, "the initializer's width was lost"
    assert stores_to(mir, "wide") == [300, 7], stores_to(mir, "wide")
