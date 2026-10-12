# tests/stdlib/test_rp_pulse_train.py
#
# PulseTrain on the RP2040 and RP2350 (hal/rp/pulse.py) is a PWM slice at the carrier frequency
# whose pin is handed to it with the PWM function and taken back with SIO. Pinned from the MIR:
# the slice wraps once per carrier period, the compare is the duty, and the pin starts out an
# output held low on SIO (not on the carrier).

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

#            pwm base,   io bank0,   freq
CHIPS = {"rp2040": (0x40050000, 0x40014000, 125_000_000),
         "rp2350": (0x400A8000, 0x40028000, 150_000_000)}
PIN = 12                       # slice 6, channel A


def stores(tmp_path: Path, chip: str, body: str) -> list[tuple[int, int]]:
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.pulse import PulseTrain\n\ndef main():\n" + body)
    mir = tmp_path / "firmware.mir"
    freq = CHIPS[chip][2]
    out = subprocess.run(
        [str(PYMCUC), str(src), "--target", chip, "--freq", str(freq),
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True)
    assert "[BUILD_OK]" in out.stdout, out.stdout + out.stderr
    main = next(f for f in json.loads(mir.read_text())["functions"] if f["name"] == "main")
    res = []
    for i in main["body"]:
        if i["$t"] == "copy" and i["dst"]["$t"] == "mem" and i["src"]["$t"] == "const":
            res.append((i["dst"]["address"] & 0xFFFFFFFF, i["src"]["value"]))
        elif i["$t"] == "sind" and i["dstPtr"]["$t"] == "const" and i["src"]["$t"] == "const":
            res.append((i["dstPtr"]["value"] & 0xFFFFFFFF, i["src"]["value"]))
    return res


@pytest.mark.parametrize("chip", sorted(CHIPS))
class TestPulseTrain:
    def test_the_slice_wraps_once_per_carrier_period(self, tmp_path, chip):
        pwm, _, freq = CHIPS[chip]
        s = stores(tmp_path, chip, f"    t = PulseTrain({PIN}, 38000, 32768)\n")
        assert (pwm + 6 * 0x14 + 0x10, freq // 38000 - 1) in s

    def test_the_compare_is_the_duty(self, tmp_path, chip):
        pwm, _, freq = CHIPS[chip]
        s = stores(tmp_path, chip, f"    t = PulseTrain({PIN}, 38000, 16384)\n")
        cc = [v for a, v in s if a == pwm + 6 * 0x14 + 0x0C]
        assert cc and abs(cc[-1] - (freq // 38000) // 4) <= 3

    def test_the_pin_rests_on_sio_and_the_carrier_is_the_pwm_function(self, tmp_path, chip):
        _, io, _ = CHIPS[chip]
        ctrl = io + 8 * PIN + 4
        s = stores(tmp_path, chip, f"    t = PulseTrain({PIN}, 38000, 32768)\n    t.carrier_on()\n"
                                   "    t.carrier_off()\n")
        assert [v for a, v in s if a == ctrl] == [5, 4, 5]
