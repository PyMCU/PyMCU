"""A Timer1 PWM at an exact frequency can be retuned, to a frequency known only at run time.

The exact path (fast PWM with TOP = ICR1) used to refuse set_freq(): the period is a
division by the frequency, and a run-time frequency needs that division in the emitted
code. So `PWM(pin)` then `pwm.freq(50)`, the way servo drivers are written, did not compile
in the MicroPython layer (PyMCU#304 is the wider unification).

Pinned here: set_freq() with a run-time frequency selects the prescaler by comparison
(TCCR1B gets each of the five mode-14 codes on some path), writes ICR1, and does it without
a compile-time guard left unverified; and a channel whose sibling is also on the exact path
is refused, because the period is one register for both. The register values themselves
are measured in firmware by the pymcu-avr fixture mp-pwm-retune.
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

TCCR1B, ICR1L, ICR1H = 0x81, 0x86, 0x87


def build(tmp_path, body):
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.pwm import PWM\n"
                   "from pymcu.chips.atmega328p import GPIOR0\n" + body + "\n")
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "avr",
         "--target", "atmega328p", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def test_a_run_time_frequency_reprograms_prescaler_and_period(tmp_path):
    proc, ir = build(tmp_path, 'p = PWM("PB1", freq=1000, duty_u16=16384)\n'
                               'p.set_freq(GPIOR0.value + 50)')
    assert proc.returncode == 0, proc.stderr
    assert "could not be verified" not in proc.stderr + proc.stdout
    body = [i for fn in ir["functions"] for i in fn["body"]]
    tccr1b = {i["src"].get("value") for i in body
              if i.get("$t") == "copy" and isinstance(i.get("dst"), dict)
              and i["dst"].get("address") == TCCR1B}
    assert {0x19, 0x1A, 0x1B, 0x1C, 0x1D} <= tccr1b, sorted(v for v in tccr1b if v is not None)
    icr = {i["dst"]["address"] for i in body if isinstance(i.get("dst"), dict)
           and i["dst"].get("address") in (ICR1L, ICR1H)
           and not (i.get("$t") == "copy" and i["src"].get("$t") == "const")}
    assert icr == {ICR1L, ICR1H}, "both bytes of the period, from the run-time division"


def test_retuning_a_channel_whose_sibling_is_exact_is_refused(tmp_path):
    proc, _ = build(tmp_path, 'a = PWM("PB1", freq=1000, duty_u16=100)\n'
                              'b = PWM("PB2", freq=1000, duty_u16=100)\n'
                              'a.set_freq(GPIOR0.value + 50)')
    assert proc.returncode != 0
    assert "share ICR1" in proc.stderr
