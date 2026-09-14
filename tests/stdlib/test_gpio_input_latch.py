"""The AVR Pin re-applies its pull when it becomes an input, and refuses open-drain (PyMCU#309).

The pull-up and the output level are the same latch (PORTx). `high()` then `mode(IN)` left the
pull-up on; `mode(IN_PULLUP)` wrote `3 ^ 1 = 2` into the one-bit direction slot; `mode(OPEN_DRAIN)`
wrote a 3. Read out of the IR: the bit writes to DDRD (0x2A) and PORTD (0x2B) that `mode()` emits.
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
    src.write_text("from pymcu.hal.gpio import Pin\n\n\ndef main():\n"
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


def bit_writes(ops, addr):
    """The (bit, value) sequence written to one register: bset, bclr, or a bwrt of a constant."""
    out = []
    for i in ops:
        if i.get("$t") not in ("bset", "bclr", "bwrt") or i["target"]["address"] != addr:
            continue
        if i["$t"] == "bset": out.append((i["bit"], 1))
        elif i["$t"] == "bclr": out.append((i["bit"], 0))
        elif i["src"].get("$t") == "const": out.append((i["bit"], i["src"]["value"]))
        else: out.append((i["bit"], "runtime"))
    return out


def test_mode_in_after_high_clears_the_latch(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.high()\np.mode(Pin.IN)')
    assert ops is not None, out
    assert bit_writes(ops, PORTD)[-1] == (6, 0), "the pull-up latch is cleared: no pull was asked for"
    assert bit_writes(ops, DDRD)[-1] == (6, 0)


def test_mode_in_keeps_a_pull_up_that_was_asked_for(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.pull(1)\np.low()\np.mode(Pin.IN)')
    assert ops is not None, out
    assert bit_writes(ops, PORTD)[-1] == (6, 1)


def test_in_pullup_through_mode_sets_the_latch(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.mode(Pin.IN_PULLUP)')
    assert ops is not None, out
    assert bit_writes(ops, DDRD)[-1] == (6, 0)
    assert bit_writes(ops, PORTD)[-1] == (6, 1)
    assert all(v in (0, 1) for _, v in bit_writes(ops, DDRD)), "never a 2 or a 3 into a one-bit slot"


def test_open_drain_through_mode_is_refused(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.mode(Pin.OPEN_DRAIN)')
    assert ops is None
    assert "Open-drain mode not supported on AVR" in out


def test_a_plain_output_pin_costs_nothing_more(tmp_path):
    out, ops = build(tmp_path, 'p = Pin("PD6", Pin.OUT)\np.high()\np.low()')
    assert ops is not None, out
    assert bit_writes(ops, PORTD) == [(6, 1), (6, 0)]
    assert bit_writes(ops, DDRD) == [(6, 1)]
