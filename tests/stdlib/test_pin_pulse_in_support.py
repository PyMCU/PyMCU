"""`Pin.pulse_in()` says it is unimplemented instead of measuring zero.

Only the ATmega48/88/168/328 family has the hand-written cycle-counted loop `pulse_in`
needs. The other AVRs say so themselves -- `atmega2560.py` and `atmega32u4.py` define
`pin_pulse_in` as a `raise NotImplementedError`, and the ATtiny GPIO files have no
`pin_pulse_in` at all -- but the facade's `match` ended in `case _: return 0`, which
swallowed all of that and answered a zero-length pulse.

A zero is a MEASUREMENT. On an HC-SR04 it is a range of zero, on a DHT it is a bit that
never arrived: the shape of a sensor that is present and answering, on a clean build. So
the arm refuses now, and the tests below are as much about the chips that must KEEP
working as about the ones that must stop.
"""

import json
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
GPIO = STDLIB / "pymcu" / "hal" / "avr" / "gpio"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PROGRAM = """\
from pymcu.hal.gpio import Pin
from pymcu.types import uint8, uint16


def main():
    p = Pin("{pin}", Pin.IN)
    w: uint16 = p.pulse_in(1, 500)
    q: uint8 = w & 0xFF
"""

# The pin has to exist on the part, or the program fails before it reaches pulse_in.
PIN_FOR = {
    "atmega328p": "PD2",
    "atmega2560": "PD0",
    "atmega32u4": "PD0",
    "attiny85": "PB0",
    "attiny84": "PB0",
}


def build(tmp_path: Path, target: str):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(pin=PIN_FOR[target]))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "avr",
         "--target", target, "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def diagnostic(proc):
    """The error MESSAGE only; stderr also echoes the offending source line."""
    for line in proc.stderr.splitlines():
        m = re.search(r"error: (?:\w+Error: )?(.*)", line)
        if m:
            return m.group(1)
    return ""


@pytest.mark.parametrize("target", ["atmega2560", "atmega32u4", "attiny85", "attiny84"])
def test_a_chip_without_the_loop_refuses_instead_of_answering_zero(tmp_path, target):
    proc, _ = build(tmp_path, target)

    assert proc.returncode != 0, \
        f"{target} still compiles pulse_in, which can only mean it returns a made-up 0"
    assert "pulse_in()" in diagnostic(proc), \
        f"the rejection must name the call; got: {diagnostic(proc)}"


def test_the_rejection_names_the_family_that_does_have_it(tmp_path):
    proc, _ = build(tmp_path, "atmega2560")
    msg = diagnostic(proc)

    assert "328" in msg, "the reader needs to know which parts do implement it"
    assert "timer" in msg, "and what to do on the parts that do not"


def test_it_says_the_old_zero_was_not_a_reading(tmp_path):
    # The whole hazard: a caller cannot tell a refusal from a very short pulse.
    proc, _ = build(tmp_path, "atmega32u4")

    assert "0" in diagnostic(proc)


def test_the_family_that_implements_it_still_compiles(tmp_path):
    # The AVR codegen lives in its own package, so reaching it is as far as this can go --
    # and reaching it means the whole front end accepted the call.
    proc, _ = build(tmp_path, "atmega328p")

    assert "pulse_in" not in diagnostic(proc), \
        f"atmega328p must keep pulse_in: {diagnostic(proc)}"


def test_the_chips_that_refuse_are_the_ones_with_no_implementation():
    """The census: what the facade refuses has to match what the chip files provide.

    An implementation added later without opening the facade would otherwise stay
    unreachable, which is the failure this pair of files already had once for
    pin_irq_setup.
    """
    implemented = {p.stem for p in GPIO.glob("*.py")
                   if p.stem != "__init__"
                   and re.search(r"^def pin_pulse_in\(", p.read_text(), re.M)
                   and "NotImplementedError" not in p.read_text().split("def pin_pulse_in(")[1][:200]}

    assert implemented == {"atmega328p"}, \
        f"the facade refuses everything but the 328p family, but these files implement " \
        f"pin_pulse_in: {implemented}"


def test_the_stubs_still_say_so_in_their_own_files():
    """The chip files are where a reader looks; they must not go quiet either."""
    for chip in ("atmega2560", "atmega32u4"):
        body = (GPIO / f"{chip}.py").read_text().split("def pin_pulse_in(")[1][:300]
        assert "NotImplementedError" in body, \
            f"{chip}.py no longer marks pin_pulse_in unimplemented; check the facade agrees"
