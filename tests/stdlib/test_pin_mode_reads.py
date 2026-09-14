"""`Pin.mode()` has the reading half its signature advertises (PyMCU#312).

`mode(self, m: const = -1) -> uint8` wrote the direction when given an argument and returned
nothing on any path, so the no-argument half of the signature did not exist: the read was
refused by the #302 check, and before that check it handed back whatever the register held.

The getter answers with the class's own constants -- OUT (0), IN (1), IN_PULLUP (3) -- read
from the direction bit and, for an input, the pull-up latch. Both are read at run time,
because either can have been changed since the pin was constructed.

The same file's PIC sibling gained a getter too, and every `match __CHIP__.name:` in
pymcu.hal.pic14.gpio gained a `case _:`; neither is exercised here, because this suite builds
for the ATmega328P.
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

DDRD, PORTD = 0x2A, 0x2B


def build(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.gpio import Pin\nfrom pymcu.types import uint8\n"
                   "from pymcu.chips.atmega328p import GPIOR0\n\n\ndef main():\n"
                   + "".join(f"    {line}\n" for line in body.splitlines())
                   + "    while True:\n        pass\n")
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    ops = None
    if "[BUILD_OK]" in proc.stdout:
        ir = json.loads(mir.read_text())
        ops = next(f for f in ir["functions"] if f["name"] == "main")["body"]
    return out, ops


def reads_of(ops, addr):
    """Every bit of one register the program tests."""
    return [i.get("bit") for i in ops
            if i.get("$t") == "bchk"
            and i.get("source", {}).get("address") == addr]


def constants(ops):
    return {i["src"]["value"] for i in ops
            if i.get("$t") == "copy" and i.get("src", {}).get("$t") == "const"}


def test_the_no_argument_form_compiles_as_a_read(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.IN_PULLUP)\nm: uint8 = p.mode()\nGPIOR0.value = m')
    assert ops is not None, out


def test_it_reads_the_direction_bit_and_the_pull_latch(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.IN_PULLUP)\nm: uint8 = p.mode()\nGPIOR0.value = m')
    assert ops is not None, out
    assert 6 in reads_of(ops, DDRD), "the direction has to come from DDRx, not from the constructor"
    assert 6 in reads_of(ops, PORTD), "IN_PULLUP is told apart from IN by the pull-up latch"


def test_it_answers_with_the_classes_own_constants(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.IN_PULLUP)\nm: uint8 = p.mode()\nGPIOR0.value = m')
    assert ops is not None, out
    # OUT is 0, IN is 1, IN_PULLUP is 3: all three are the answers this getter can give.
    assert {0, 1, 3} <= constants(ops), constants(ops)


def test_the_writing_form_still_builds_as_a_statement(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.mode(Pin.IN)')
    assert ops is not None, out


def test_open_drain_is_still_refused(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.mode(Pin.OPEN_DRAIN)')
    assert ops is None
    assert "Open-drain" in out, out
