"""A module-level object keeps its fields constant unless a method actually writes one.

Calling any method on a module-level instance used to mark EVERY field of that instance
mutable, on the reasoning that a method can write a field through the write-back
convention with no assignment visible at the call site. True, and far wider than it needs
to be: `Pin.high()` is `self._port[self._bit] = 1` and writes no field, yet one call to it
made `_port`, `_ddr`, `_pin` and `_bit` run-time values for the rest of the program. The
bit became a run-time shift loop instead of a constant, and the port a 16-bit access.

Measured on atmega328p, the same program written three ways:

    Pin inside a function              126 B
    module-level, used from main       196 B  ->  126 B
    module-level, used from a helper   248 B  ->  130 B

The narrowing is deliberately all-or-nothing: a method that writes ANY field still marks
them all. Marking exactly the fields written is possible, and is what issues #124 and #127
were about when it went wrong -- a write landing on a name with no storage and the reader
folding the constructor's value, compiling clean. The guard tests below pin that half.
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

PORTD = 0x2B


def build(tmp_path: Path, source: str):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    return json.loads(mir.read_text())


def func(ir, name):
    return next(f for f in ir["functions"] if f["name"] == name)


def ops(fn):
    """Real instructions: debug records, labels and the trailing return are not code."""
    return [i for i in fn["body"] if i.get("$t") not in ("dbg", "lbl", "ret")]


CROSS_FUNCTION = (
    "from pymcu.hal.gpio import Pin\n\n"
    'led = Pin("PD5", Pin.OUT)\n\n\n'
    "def blink():\n"
    "    led.high()\n"
    "    led.low()\n\n\n"
    "def main():\n"
    "    blink()\n"
    "    while True:\n"
    "        pass\n"
)

WRITER = (
    "from pymcu.types import uint8\n"
    "from pymcu.chips.atmega328p import GPIOR1\n\n\n"
    "class Box:\n"
    "    def __init__(self, n: uint8):\n"
    "        self.n: uint8 = n\n\n"
    "    def mark(self) -> None:\n"
    "        self.n = 77\n\n\n"
    "obj = Box(0)\n\n\n"
    "def touch() -> None:\n"
    "    obj.mark()\n\n\n"
    "def main() -> None:\n"
    "    touch()\n"
    "    GPIOR1.value = obj.n\n"
    "    while True:\n"
    "        pass\n"
)


# --- the narrowing -----------------------------------------------------------
# These fail before the fix: the helper emitted a shift loop over a run-time bit.

def test_a_read_only_method_leaves_the_bit_constant(tmp_path):
    ir = build(tmp_path, CROSS_FUNCTION)
    blink = func(ir, "blink")

    kinds = [i["$t"] for i in ops(blink)]
    assert kinds == ["bset", "bclr"], (
        "led.high()/low() must fold to a single bit operation each; "
        f"got {kinds}, which is the run-time shift the wide mark forced")



def test_the_bit_operation_names_the_right_port_and_bit(tmp_path):
    """Folding to the wrong bit would also be two instructions."""
    ir = build(tmp_path, CROSS_FUNCTION)
    got = [(i["$t"], i["target"]["address"], i["bit"]) for i in ops(func(ir, "blink"))]
    assert got == [("bset", PORTD, 5), ("bclr", PORTD, 5)], got


# --- the guard: #124 and #127 --------------------------------------------------
# These hold before the fix too. They are invariants, not evidence: they exist so that
# narrowing further cannot quietly drop the storage a written field needs.

def test_a_field_a_method_writes_still_has_storage(tmp_path):
    ir = build(tmp_path, WRITER)
    main = func(ir, "main")

    assert any(i.get("$t") == "copy"
               and i["src"].get("$t") == "var" and i["src"]["name"] == "obj_n"
               for i in ops(main)), \
        "main must READ obj_n, not fold the constructor's 0 (issues #124 and #127)"


def test_the_writing_method_receives_the_field(tmp_path):
    ir = build(tmp_path, WRITER)
    touch = func(ir, "touch")
    call = next(i for i in ops(touch) if i.get("$t") == "call")
    assert any(a.get("name") == "obj_n" for a in call["args"]), \
        "the write-back convention passes the field in and out; it needs a home"
