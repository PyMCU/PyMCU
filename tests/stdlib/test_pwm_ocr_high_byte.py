"""A duty written to OCR1A/OCR1B must clear the high byte first.

Those two compare registers are 16-bit, and every 16-bit timer register on the
ATmega328P commits through a single shared TEMP byte: writing the low byte writes
TEMP as the high byte. Writing only OCR1AL therefore commits whatever the last
16-bit write on Timer1 happened to leave in TEMP. A program that also drives
Timer1 through the timer or servo HAL lands a duty far above TOP, the compare
never matches, and the channel sits at 100% however little was asked for.

Measured before the fix, with a servo pulse leaving 0x0B in TEMP:
`PWM("PB1", 128)` read back OCR1A = 2944 and held the pin high for 256 of 256
timer ticks instead of 128.

What makes this hard to test at the wrong altitude: OCR1AL alone reads back
correct on both the broken and the fixed HAL, because the low byte is the one
value that always lands. Only the committed 16-bit register, or the waveform,
tells them apart. Here that is done structurally, by requiring the high-byte
store to sit immediately before the low-byte one; the value-bearing half lives in
the pwm-duty-zero sibling fixture in the pymcu-avr repo.

Timer0 and Timer2 have 8-bit compare registers with no TEMP in the path, so their
four channels must emit no high-byte store at all.
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

# Data-space addresses. The Timer1 pair is 16-bit; the other four are not.
OCR1AL, OCR1AH = 0x88, 0x89
OCR1BL, OCR1BH = 0x8A, 0x8B

# pin -> (low-byte register, high-byte register or None if the register is 8-bit)
CHANNELS = {
    "PD6": (0x47, None),      # OCR0A
    "PD5": (0x48, None),      # OCR0B
    "PB1": (OCR1AL, OCR1AH),  # OCR1A, 16-bit
    "PB2": (OCR1BL, OCR1BH),  # OCR1B, 16-bit
    "PB3": (0xB3, None),      # OCR2A
    "PD3": (0xB4, None),      # OCR2B
}


def stores(tmp_path: Path, pin: str):
    """Every constant-or-variable store to an I/O register in main, in order.

    The duty is read from GPIOR0 so it is a run-time value: a literal would fold
    and the constructor would collapse to a single constant store, which is not
    the shape this test is about.
    """
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.chips.atmega328p import GPIOR0\n"
        "from pymcu.hal.pwm import PWM\n"
        "from pymcu.types import uint8\n\n\n"
        "def main():\n"
        "    d: uint8 = GPIOR0.value\n"
        f'    p = PWM("{pin}", d)\n'
        "    p.set_duty(d)\n"
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
    out = []
    for i in main["body"]:
        if i.get("$t") != "copy":
            continue
        dst = i["dst"]
        if dst.get("$t") != "mem":
            continue
        src_op = i["src"]
        value = src_op.get("value") if src_op.get("$t") == "const" else None
        out.append((dst["address"], value))
    return out


@pytest.mark.parametrize("pin", ["PB1", "PB2"])
def test_the_high_byte_is_cleared_immediately_before_the_low_one(tmp_path, pin):
    low, high = CHANNELS[pin]
    seq = stores(tmp_path, pin)

    low_writes = [n for n, (addr, _) in enumerate(seq) if addr == low]
    assert low_writes, f"{pin}: no write to the compare register at all"

    for n in low_writes:
        assert n > 0 and seq[n - 1] == (high, 0), (
            f"{pin}: the store to {hex(low)} at position {n} is not preceded by "
            f"a store of 0 to {hex(high)}; it commits whatever TEMP holds. "
            f"Neighbourhood: {[(hex(a), v) for a, v in seq[max(0, n - 2):n + 1]]}")


@pytest.mark.parametrize("pin", ["PB1", "PB2"])
def test_both_write_sites_are_covered(tmp_path, pin):
    """The constructor and set_duty both write a duty, and both used to be wrong."""
    low, _ = CHANNELS[pin]
    assert len([1 for addr, _ in stores(tmp_path, pin) if addr == low]) >= 2, \
        f"{pin}: expected a duty write from both the constructor and set_duty"


@pytest.mark.parametrize("pin", ["PD6", "PD5", "PB3", "PD3"])
def test_an_eight_bit_channel_pays_nothing(tmp_path, pin):
    """OCR0A/OCR0B/OCR2A/OCR2B have no TEMP in the path, so the clear must fold away."""
    low, _ = CHANNELS[pin]
    seq = stores(tmp_path, pin)
    assert any(addr == low for addr, _ in seq), f"{pin}: no write to the compare register"
    for addr in (OCR1AH, OCR1BH):
        assert not any(a == addr for a, _ in seq), \
            f"{pin} is an 8-bit channel and must not touch {hex(addr)}"
