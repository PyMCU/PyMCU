"""The 16-bit duty entry of the PWM HAL lands exactly on the 8-bit channels (pymcu-circuitpython#30).

Fast PWM is high for OCR + 1 of 256 counts. `duty_cycle >> 8` straight into OCR put every
CircuitPython duty 1/256 above what was asked (50.4 % for 32768, measured on an Arduino Uno).
`PWM(pin, duty_u16=...)` and `set_duty_u16()` round the 16-bit value to a number of counts and
program one less; 0 counts is off (compare output disconnected), 256 is fully on. Read out of
the IR: the constant stored into OCRx and the COM bits written to TCCRxA.
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

OCR0A, TCCR0A = 0x47, 0x44
AND, OR = 12, 13


def ir_of(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.pwm import PWM\nfrom pymcu.types import uint16\n\n\ndef main():\n"
                   + "".join(f"    {line}\n" for line in body.splitlines())
                   + "    while True:\n        pass\n")
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    ir = json.loads(mir.read_text())
    return next(f for f in ir["functions"] if f["name"] == "main")["body"]


def ocr_stores(ops):
    return [i["src"]["value"] for i in ops
            if i.get("$t") == "copy" and i["dst"].get("$t") == "mem" and i["dst"]["address"] == OCR0A
            and i["src"].get("$t") == "const"]


def com_writes(ops):
    return [(i["op"], i["src2"]["value"]) for i in ops
            if i.get("$t") == "binary" and i["dst"].get("$t") == "mem" and i["dst"]["address"] == TCCR0A
            and i["src2"].get("$t") == "const"]


@pytest.mark.parametrize("duty,ocr", [(32768, 127), (65535, 255), (16384, 63), (128, 0), (49152, 191), (383, 0), (512, 1)])
def test_a_constructor_duty_u16_lands_one_below_its_count(tmp_path, duty, ocr):
    ops = ir_of(tmp_path, f'p = PWM("PD6", duty_u16={duty})')
    assert ocr_stores(ops) == [ocr]
    assert (OR, 0x83) in com_writes(ops), "connected, fast PWM"


def test_a_constructor_duty_u16_below_half_a_count_is_off(tmp_path):
    ops = ir_of(tmp_path, 'p = PWM("PD6", duty_u16=127)')
    assert (AND, 0x3F) in com_writes(ops), "compare output disconnected"


@pytest.mark.parametrize("duty,ocr", [(32768, 127), (65535, 255), (4915, 18)])
def test_the_setter_lands_one_below_its_count(tmp_path, duty, ocr):
    ops = ir_of(tmp_path, f'p = PWM("PD6", 128)\np.set_duty_u16({duty})')
    assert ocr in ocr_stores(ops)
    assert (OR, 0x80) in com_writes(ops)


def test_the_setter_below_half_a_count_is_off(tmp_path):
    ops = ir_of(tmp_path, 'p = PWM("PD6", 128)\np.set_duty_u16(100)')
    assert (AND, 0x3F) in com_writes(ops)


def test_the_eight_bit_entry_keeps_its_meaning(tmp_path):
    ops = ir_of(tmp_path, 'p = PWM("PD6", 128)\np.set_duty(200)')
    assert ocr_stores(ops) == [128, 200], "duty 0..255 goes straight to OCR, as documented"
