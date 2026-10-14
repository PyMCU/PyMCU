# tests/stdlib/test_rp_pwm_frequency.py
#
# The RP2040 and RP2350 PWM report the frequency they actually emit, which is not always the one
# asked for (the period is a whole number of system clocks times a divider in sixteenths). The
# CircuitPython layer's PWMOut.frequency, and adafruit_motor's Servo through it, read it.

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


@pytest.mark.parametrize("chip,freq", [("rp2040", 125_000_000), ("rp2350", 150_000_000)])
def test_pwm_frequency_compiles_and_is_a_32_bit_value(tmp_path, chip, freq):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.hal.pwm import PWM\n"
        "from pymcu.types import ptr, uint32\n\n"
        "def main():\n"
        "    p = PWM(7, 50)\n"
        "    out: ptr[uint32] = ptr(0x20000000)\n"
        "    out.value = p.frequency()\n")
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", str(freq), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True)
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
