# tests/stdlib/test_rp_pin_pull.py
#
# Pin(n, mode, pull=...) used to drop `pull` on the floor on the RP2040 and RP2350: the pad was
# written with input-enable only, so Pin(n, Pin.IN, pull=Pin.PULL_UP) left the line floating,
# silently. The pad register holds PUE at bit 3 and PDE at bit 2 beside IE at bit 6.

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PADS = {"rp2040": 0x4001C000, "rp2350": 0x40038000}   # PADS_BANK0 base
IE, PUE, PDE = 0x40, 0x08, 0x04


def pad_writes(tmp_path: Path, chip: str, ctor: str) -> list[int]:
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.gpio import Pin\n\ndef main():\n    p = " + ctor + "\n")
    mir = tmp_path / "firmware.mir"
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", "125000000",
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True)
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
    pad = PADS[chip] + 4 + 4 * 7
    main = next(f for f in json.loads(mir.read_text())["functions"] if f["name"] == "main")
    vals = []
    for i in main["body"]:
        if i["$t"] == "copy" and i["dst"]["$t"] == "mem" and i["src"]["$t"] == "const":
            if i["dst"]["address"] & 0xFFFFFFFF == pad:
                vals.append(i["src"]["value"])
        elif i["$t"] == "sind" and i["dstPtr"]["$t"] == "const" and i["src"]["$t"] == "const":
            if i["dstPtr"]["value"] & 0xFFFFFFFF == pad:
                vals.append(i["src"]["value"])
    return vals


@pytest.mark.parametrize("chip", sorted(PADS))
class TestPull:
    def test_pull_up_turns_PUE_on(self, tmp_path, chip):
        assert pad_writes(tmp_path, chip, "Pin(7, Pin.IN, pull=Pin.PULL_UP)")[-1] == IE | PUE

    def test_pull_down_turns_PDE_on(self, tmp_path, chip):
        assert pad_writes(tmp_path, chip, "Pin(7, Pin.IN, pull=Pin.PULL_DOWN)")[-1] == IE | PDE

    def test_no_pull_keeps_both_off(self, tmp_path, chip):
        assert pad_writes(tmp_path, chip, "Pin(7, Pin.IN)")[-1] == IE

    def test_pull_none_keeps_both_off(self, tmp_path, chip):
        assert pad_writes(tmp_path, chip, "Pin(7, Pin.IN, pull=None)")[-1] == IE

    def test_positional_pull_is_the_third_argument(self, tmp_path, chip):
        assert pad_writes(tmp_path, chip, "Pin(7, Pin.IN, Pin.PULL_UP)")[-1] == IE | PUE
