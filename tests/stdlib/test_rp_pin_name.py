# tests/stdlib/test_rp_pin_name.py
#
# A layer written against the Pin of any target asks it for `name` to say which pin it drives
# (neopixel_write hands it to the WS2812 driver). The AVR Pin has one; the RP2040 and RP2350 Pin
# did not, and the layer failed to compile there. Here the name is the GPIO number.

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


@pytest.mark.parametrize("chip", ["rp2040", "rp2350"])
def test_pin_name_is_the_gpio_number(tmp_path, chip):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.hal.gpio import Pin\n"
        "from pymcu.hal.ws2812 import ws2812_init\n\n"
        "def main():\n"
        "    p = Pin(6, Pin.OUT)\n"
        "    ws2812_init(p.name)\n")
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", "125000000",
         "-I", str(STDLIB), "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True)
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
