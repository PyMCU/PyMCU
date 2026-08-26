"""PWM duty 0 must be off, and duty 255 must be fully on.

`docs/library/pwm.md` documents duty as "8-bit (0 = 0%, 255 = 100%)". The high end
comes free: fast PWM with OCRx at MAX holds the output constantly high. The low end
does not. ATmega328P section 15.7.3: with OCRx at BOTTOM the output is "a narrow
spike for each MAX+1 timer clock cycle", so writing OCRx = 0 and leaving the
compare output connected is roughly 0.4%, not 0% -- a visibly dim LED. Off is the
COM bits cleared and the pin driven low, which is what Arduino's
analogWrite(pin, 0) does too.

Every one of the six channels has to do it, and each has its own TCCRxA and its
own pair of COM bits, so this reads the register traffic per channel out of the
IR rather than trusting one of them to stand for the rest.
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

AND, OR = 12, 13        # binary opcodes

# pin -> (TCCRxA, OCRx, PORTx, pin bit, COM-clear mask, COM-set mask)
# COMxA is TCCRxA bits 7:6, COMxB is bits 5:4; non-inverting is the high bit set.
CHANNELS = {
    "PD6": (0x44, 0x47, 0x2B, 6, 0x3F, 0x80),   # Timer0 OC0A
    "PD5": (0x44, 0x48, 0x2B, 5, 0xCF, 0x20),   # Timer0 OC0B
    "PB1": (0x80, 0x88, 0x25, 1, 0x3F, 0x80),   # Timer1 OC1A
    "PB2": (0x80, 0x8A, 0x25, 2, 0xCF, 0x20),   # Timer1 OC1B
    "PB3": (0xB0, 0xB3, 0x25, 3, 0x3F, 0x80),   # Timer2 OC2A
    "PD3": (0xB0, 0xB4, 0x2B, 3, 0xCF, 0x20),   # Timer2 OC2B
}


def set_duty_ops(tmp_path: Path, pin: str, duty: int):
    """The instructions `set_duty(duty)` emits, on a channel already running."""
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.hal.pwm import PWM\n\n\n"
        "def main():\n"
        f'    p = PWM("{pin}", 128)\n'
        f"    p.set_duty({duty})\n"
        "    while True:\n"
        "        pass\n"
    )
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    ir = json.loads(mir.read_text())
    main = next(f for f in ir["functions"] if f["name"] == "main")
    # Collect from the call site to the next statement of main.py. Until PyMCU#179 every
    # dbg inside the inlined stdlib body carried MAIN.PY's text, so `collecting` could be
    # recomputed per marker and stayed true through the whole body. Those markers now carry
    # the stdlib's own source, which is the point of that fix, so the span has to be
    # delimited by its two ends instead of by a substring holding across it.
    ops, collecting = [], False
    for i in main["body"]:
        if i.get("$t") == "dbg":
            if "set_duty(" in i["text"]:
                collecting = True
            elif collecting and "while True" in i["text"]:
                break
            continue
        if collecting:
            ops.append(i)
    return ops


def mem_writes(ops):
    """(address, opcode, operand) for each read-modify-write of an I/O register."""
    return [(i["dst"]["address"], i["op"], i["src2"]["value"]) for i in ops
            if i.get("$t") == "binary" and i["dst"].get("$t") == "mem"
            and i["src2"].get("$t") == "const"]


def stores(ops):
    """(address, value) for each plain constant store."""
    return [(i["dst"]["address"], i["src"]["value"]) for i in ops
            if i.get("$t") == "copy" and i["dst"].get("$t") == "mem"
            and i["src"].get("$t") == "const"]


def bit_clears(ops):
    return [(i["target"]["address"], i["bit"]) for i in ops if i.get("$t") == "bclr"]


@pytest.mark.parametrize("pin", sorted(CHANNELS))
def test_duty_zero_disconnects_the_output_and_drives_the_pin_low(tmp_path, pin):
    tccra, ocr, port, bit, clear, _ = CHANNELS[pin]
    ops = set_duty_ops(tmp_path, pin, 0)

    assert (tccra, AND, clear) in mem_writes(ops), \
        f"{pin}: duty 0 must clear the COM bits in TCCR at {hex(tccra)}"
    assert (port, bit) in bit_clears(ops), \
        f"{pin}: duty 0 must drive the pin low once the compare output is off"
    assert not [w for w in stores(ops) if w[0] == ocr], \
        f"{pin}: duty 0 must not be expressed as OCR = BOTTOM -- that is a spike, not off"


@pytest.mark.parametrize("pin", sorted(CHANNELS))
def test_duty_max_is_written_with_the_output_connected(tmp_path, pin):
    tccra, ocr, _, _, _, com = CHANNELS[pin]
    ops = set_duty_ops(tmp_path, pin, 255)

    assert (ocr, 0xFF) in stores(ops), f"{pin}: duty 255 must write OCR = MAX"
    assert (tccra, OR, com) in mem_writes(ops), \
        f"{pin}: a non-zero duty must (re)connect the compare output"


@pytest.mark.parametrize("pin", sorted(CHANNELS))
def test_a_middling_duty_still_reaches_the_compare_register(tmp_path, pin):
    _, ocr, _, _, _, _ = CHANNELS[pin]
    assert (ocr, 0x80) in stores(set_duty_ops(tmp_path, pin, 128))


def test_a_constant_duty_costs_no_branch(tmp_path):
    """The zero test folds away when the duty is known, so off is two instructions."""
    for duty in (0, 255):
        ops = set_duty_ops(tmp_path, "PD6", duty)
        assert not [i for i in ops if i.get("$t") in ("jne", "jz", "jmp", "lbl")], \
            f"duty {duty} is a constant and must not emit a runtime branch"
