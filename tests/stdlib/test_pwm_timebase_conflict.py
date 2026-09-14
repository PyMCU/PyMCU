"""Timer0 is the millisecond time base AND the timer behind PD5/PD6 (PyMCU#295).

millis_init() runs Timer0 at prescaler 64 and counts its overflows. A PWM on PD5 or PD6 at
another bucket reprograms that prescaler: measured on an Arduino Uno, PWM("PD6", 128, 5000)
after millis_init() made monotonic() run 8.44 times too fast. With `--timebase` (what the
driver passes whenever millis_init() is injected or written) the compiler binds
__TIMEBASE__ = 1 and the HAL refuses the request at compile time, where it is written;
without the flag every bucket stays available on those pins, and the other timers never
mind.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build(tmp_path: Path, pin: str, freq: int, timebase: bool) -> subprocess.CompletedProcess:
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.hal.pwm import PWM\n\n\n"
        "def main():\n"
        f'    p = PWM("{pin}", 128, {freq})\n'
        "    while True:\n"
        "        pass\n"
    )
    # --emit-ir stops the frontend before the backend it does not carry; the driver would
    # hand the .mir to pymcuc-avr, and everything under test happens before that.
    cmd = [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
           "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
           "--emit-ir", str(tmp_path / "firmware.mir")]
    if timebase:
        cmd.append("--timebase")
    return subprocess.run(cmd, capture_output=True, text=True)


@pytest.mark.parametrize("pin", ["PD6", "PD5"])
@pytest.mark.parametrize("freq", [100, 5000, 50000])
def test_a_timer0_pin_at_another_bucket_is_refused_under_the_time_base(tmp_path, pin, freq):
    proc = build(tmp_path, pin, freq, timebase=True)
    assert "[BUILD_OK]" not in proc.stdout, proc.stdout + proc.stderr
    out = proc.stdout + proc.stderr
    assert "share Timer0 with the millisecond time base" in out
    assert "D3/D11" in out and "D9/D10" in out, "the message names the pins that would work"


@pytest.mark.parametrize("pin", ["PD6", "PD5"])
@pytest.mark.parametrize("freq", [500, 1000, 2762])
def test_the_time_base_bucket_itself_is_accepted(tmp_path, pin, freq):
    proc = build(tmp_path, pin, freq, timebase=True)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@pytest.mark.parametrize("pin,freq", [("PD3", 5000), ("PB3", 50000), ("PB1", 100), ("PB2", 5000)])
def test_the_other_timers_never_mind_the_time_base(tmp_path, pin, freq):
    proc = build(tmp_path, pin, freq, timebase=True)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@pytest.mark.parametrize("freq", [100, 5000, 50000])
def test_without_the_time_base_every_bucket_stays_available_on_timer0(tmp_path, freq):
    proc = build(tmp_path, "PD6", freq, timebase=False)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
