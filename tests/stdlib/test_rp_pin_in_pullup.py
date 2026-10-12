# tests/stdlib/test_rp_pin_in_pullup.py
#
# Pin.IN_PULLUP is the AVR spelling of "an input with the pull-up on", and the layers written
# against it (pulseio.PulseIn.resume(trigger_duration) turns a pin round with it) reach the
# RP2040 and RP2350 Pin too. Mode 3 used to fall into the plain-input branch and leave the
# pad's pull-up off.

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

CHIPS = {
    # chip: (pads bank 0 base, SIO GPIO_OE_CLR)
    "rp2040": (0x4001C000, 0xD0000028),
    "rp2350": (0x40038000, 0xD0000040),
}


def stores(tmp_path: Path, chip: str, body: str) -> list[tuple[int, int]]:
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.gpio import Pin\n\ndef main():\n" + body)
    mir = tmp_path / "firmware.mir"
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", "125000000",
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
    main = next(f for f in json.loads(mir.read_text())["functions"] if f["name"] == "main")
    out = []
    for i in main["body"]:
        if i["$t"] == "copy" and i["dst"]["$t"] == "mem" and i["src"]["$t"] == "const":
            out.append((i["dst"]["address"] & 0xFFFFFFFF, i["src"]["value"]))
        # A store through a pointer the compiler knows the value of (a `ptr(...)` local).
        elif i["$t"] == "sind" and i["dstPtr"]["$t"] == "const" and i["src"]["$t"] == "const":
            out.append((i["dstPtr"]["value"] & 0xFFFFFFFF, i["src"]["value"]))
    return out


@pytest.mark.parametrize("chip", sorted(CHIPS))
class TestInPullup:
    def test_the_constructor_turns_the_pad_pull_up_on(self, tmp_path, chip):
        pads, oe_clr = CHIPS[chip]
        s = stores(tmp_path, chip, "    p = Pin(7, Pin.IN_PULLUP)\n")

        assert (pads + 4 + 4 * 7, 0x48) in s            # IE | PUE, after the plain IE write
        assert (oe_clr, 1 << 7) in s                    # an input

    def test_a_plain_input_keeps_the_pull_up_off(self, tmp_path, chip):
        pads, _ = CHIPS[chip]
        s = stores(tmp_path, chip, "    p = Pin(7, Pin.IN)\n")

        assert (pads + 4 + 4 * 7, 0x48) not in s

    def test_mode_in_pullup_turns_the_pull_up_on_later(self, tmp_path, chip):
        pads, oe_clr = CHIPS[chip]
        s = stores(tmp_path, chip, "    p = Pin(7, Pin.OUT)\n    p.mode(Pin.IN_PULLUP)\n")

        assert s.count((pads + 4 + 4 * 7, 0x48)) == 1
        assert (oe_clr, 1 << 7) in s
