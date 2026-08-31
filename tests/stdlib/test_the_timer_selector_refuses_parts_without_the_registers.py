"""The AVR millisecond clock is offered only to parts whose registers it programs.

`hal/timer.py` used to select the ATmega328P's timer with an `else`, so any AVR that was not
one of the three ATtinys with their own module got it. Two of them, the ATtiny 2313 and 4313,
declare neither TIMSK0 nor TIFR0, which that module programs, so the build emitted the
ATmega's 0x6E on a die where that address is not a timer-interrupt mask, and said nothing.

The measurement that decides the list: of the 20 AVR chip files, 15 declare all six Timer0
registers the ATmega module uses and 5 do not. Three of the five already had their own module.

    TCCR0A TCCR0B TCNT0 TIMSK0 TIFR0 OCR0A     what avr/timer/atmega328p.py programs
    attiny2313 attiny4313                      declare neither TIMSK0 nor TIFR0
    attiny25 attiny45 attiny85                 same, and already refused by their own module

Registers present is not the same as semantics shared, and this file does not claim otherwise:
attiny13, 13a, 24, 44 and 84 declare all six and are still served the ATmega's prescaler bits
and ISR vector. Whether that is correct for them is a separate question and is recorded in #234.
"""
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"


def _build(tmp_path, chip, body):
    src = tmp_path / "main.py"
    src.write_text(body)
    return subprocess.run(
        [str(PYMCUC), str(src), "--emit-ir", str(tmp_path / "f.mir"),
         "--target", chip, "-I", str(STDLIB)],
        capture_output=True, text=True)


_MILLIS = "from pymcu.time import millis\nfrom pymcu.types import uint32\n\n\ndef main() -> None:\n    t: uint32 = millis()\n"
_DELAY = "from pymcu.time import delay_ms\n\n\ndef main() -> None:\n    delay_ms(10)\n"


@pytest.mark.parametrize("chip", ["attiny2313", "attiny4313"])
def test_a_part_without_TIMSK0_is_refused_rather_than_given_the_ATmega_s(chip, tmp_path):
    r = _build(tmp_path, chip, _MILLIS)
    assert r.returncode == 1, f"{chip} built a millisecond clock it has no registers for"
    assert "no millisecond clock for this chip yet" in r.stdout + r.stderr


@pytest.mark.parametrize("chip", ["attiny2313", "attiny4313"])
def test_the_message_says_missing_work_and_not_absent_hardware(chip, tmp_path):
    # These parts HAVE a Timer0. Telling someone their chip cannot do it would send them to
    # buy hardware they are holding, which is the distinction #238 settled.
    r = _build(tmp_path, chip, _MILLIS)
    assert "have a Timer0" in r.stdout + r.stderr


@pytest.mark.parametrize("chip", ["attiny2313", "attiny4313"])
def test_delay_still_works_on_the_same_parts(chip, tmp_path):
    # The refusal is scoped to the clock. A guard written one condition too wide would take
    # delay_ms with it, and delay_ms is what these parts are usually used with.
    assert _build(tmp_path, chip, _DELAY).returncode == 0


@pytest.mark.parametrize("chip", ["atmega328p", "attiny13"])
def test_parts_that_declare_the_registers_still_get_the_clock(chip, tmp_path):
    assert _build(tmp_path, chip, _MILLIS).returncode == 0
