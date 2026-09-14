"""The two channels of one timer share its prescaler (PyMCU#300).

Measured on an Arduino Uno: PWM("PD5", 128, 5000) then PWM("PD6", 128, 100) left BOTH pins
at 61 Hz and nothing said so. The PWM class now claims the prescaler under the timer's key
(`claim()` from pymcu.types) and the compiler refuses the second channel asking for another
one, where it is written, naming both pins and the buckets. The same bucket is shared, a
channel alone on its timer may retune, and the other timers never meet.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build(tmp_path: Path, body: str) -> str:
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.pwm import PWM\nfrom pymcu.types import uint16\n"
                   "from pymcu.chips.atmega328p import GPIOR0\n\n\ndef main():\n"
                   + "".join(f"    {line}\n" for line in body.splitlines())
                   + "    while True:\n        pass\n")
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True)
    return proc.stdout + proc.stderr


def test_two_channels_at_different_buckets_are_refused_naming_both(tmp_path):
    out = build(tmp_path, 'a = PWM("PD5", 128, 5000)\nb = PWM("PD6", 128, 100)')
    assert "[BUILD_OK]" not in out
    assert "Timer0 prescaler" in out and "PD5" in out and "PD6" in out
    assert "already 2" in out and "asks for 5" in out
    assert "main.py:7" in out, "the second construction is the line to act on"


def test_two_channels_in_the_same_bucket_share_it(tmp_path):
    out = build(tmp_path, 'a = PWM("PD5", 128, 1000)\nb = PWM("PD6", 128, 2000)')
    assert "[BUILD_OK]" in out, out


def test_a_channel_alone_on_its_timer_may_retune(tmp_path):
    out = build(tmp_path, 'a = PWM("PD6", 128, 5000)\na.set_freq(100)')
    assert "[BUILD_OK]" in out, out


def test_a_channel_with_a_sibling_may_not_retune(tmp_path):
    out = build(tmp_path, 'a = PWM("PD5", 128)\nb = PWM("PD6", 128)\nb.set_freq(20000)')
    assert "[BUILD_OK]" not in out
    assert "PD5" in out and "PD6" in out


def test_the_other_timers_never_meet(tmp_path):
    out = build(tmp_path, 'a = PWM("PD6", 128, 5000)\nb = PWM("PD3", 128, 100)\nc = PWM("PB1", 128, 61)')
    assert "[BUILD_OK]" in out, out


@pytest.mark.parametrize("a,b", [("PB1", "PB2"), ("PB3", "PD3")])
def test_timer1_and_timer2_pairs_are_guarded_too(tmp_path, a, b):
    out = build(tmp_path, f'x = PWM("{a}", 128, 5000)\ny = PWM("{b}", 128, 100)')
    assert "[BUILD_OK]" not in out
    assert a in out and b in out


def test_a_run_time_frequency_has_nothing_to_claim(tmp_path):
    # Read from a register, not assigned a literal: since PyMCU#327 a local that holds a
    # compile-time constant IS passed as one, so `f: uint16 = 5000` claims exactly as the
    # literal does -- which is the row below.
    out = build(tmp_path, 'f: uint16 = GPIOR0.value\na = PWM("PD5", 128, f)\n'
                          'b = PWM("PD6", 128, 100)')
    assert "[BUILD_OK]" in out, out


def test_a_constant_frequency_held_in_a_local_claims_like_a_literal(tmp_path):
    # PyMCU#327: the local carries the constant into the claim, so the conflict is seen.
    out = build(tmp_path, 'f: uint16 = 5000\na = PWM("PD5", 128, f)\nb = PWM("PD6", 128, 100)')
    assert "[BUILD_OK]" not in out
    assert "Timer0 prescaler" in out and "PD5" in out and "PD6" in out
