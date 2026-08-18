import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

ADCON2 = 0xFC0
ADCON1 = 0xFC1
ADCON0 = 0xFC2
ANSELA = 0xF5B
TRISA = 0xF92

ADCON0_FOR = {"RA0": 0x01, "RA1": 0x05, "RA2": 0x09, "RA3": 0x0D, "RA5": 0x11}

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)


def program(channel: str) -> str:
    return (
        "from pymcu.types import uint8\n"
        "from pymcu.hal.pic18.pic18f45k50_adc import adc_init, adc_start, adc_busy\n"
        "from pymcu.chips.pic18f45k50 import ADRESL, LATD, TRISD\n"
        "\n"
        "def main():\n"
        "    TRISD.value = 0\n"
        f"    adc_init(\"{channel}\")\n"
        "    while True:\n"
        f"        adc_start(\"{channel}\")\n"
        "        while adc_busy() == 1:\n"
        "            pass\n"
        "        LATD.value = ADRESL.value\n"
    )


def build_ir(tmp_path: Path, source: str):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic18",
         "--target", "pic18f45k50", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    return json.loads(mir.read_text())


def stores(ir) -> dict:
    found = {}
    for func in ir["functions"]:
        for ins in func["body"]:
            for slot in ("dst", "target"):
                operand = ins.get(slot)
                if isinstance(operand, dict) and operand.get("$t") == "mem":
                    src = ins.get("src")
                    value = src.get("value") if isinstance(src, dict) and src.get("$t") == "const" else None
                    found.setdefault(operand["address"], []).append(value)
    return found


def test_adc_init_writes_every_control_register(tmp_path):
    written = stores(build_ir(tmp_path, program("RA0")))
    for reg in (ADCON0, ADCON1, ADCON2):
        assert reg in written, f"0x{reg:03X} never written"


def test_adc_init_makes_the_pin_an_analog_input(tmp_path):
    written = stores(build_ir(tmp_path, program("RA0")))
    assert ANSELA in written
    assert TRISA in written


def test_adcon1_selects_the_supply_rails_as_reference(tmp_path):
    written = stores(build_ir(tmp_path, program("RA0")))
    assert 0x00 in written[ADCON1]


def test_adcon2_keeps_tad_inside_the_datasheet_window_at_16mhz(tmp_path):
    written = stores(build_ir(tmp_path, program("RA0")))
    assert 0xAD in written[ADCON2]


@pytest.mark.parametrize("channel,expected", sorted(ADCON0_FOR.items()))
def test_each_channel_selects_its_own_adcon0(tmp_path, channel, expected):
    written = stores(build_ir(tmp_path, program(channel)))
    assert expected in written[ADCON0]
