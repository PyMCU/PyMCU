import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
BACKEND = REPO.parent / "pymcu-pic" / "build" / "bin" / "pymcuc-pic"
STDLIB = REPO / "lib" / "src"

PORTA, PORTB = 0x05, 0x06
TRISA, TRISB = 0x85, 0x86
CMCON, OPTION_REG = 0x1F, 0x81
TXSTA, SPBRG, RCSTA, TXREG = 0x98, 0x99, 0x18, 0x19

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build_ir(tmp_path: Path, source: str, freq: int = 4_000_000):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic14",
         "--target", "pic16f628a", "--freq", str(freq), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def touched(ir):
    """Every SFR address the program writes, with the constants written to it."""
    found = {}
    for func in ir["functions"]:
        for ins in func["body"]:
            for slot in ("dst", "target"):
                operand = ins.get(slot)
                if isinstance(operand, dict) and operand.get("$t") == "mem":
                    src = ins.get("src")
                    value = src.get("value") if isinstance(src, dict) and src.get("$t") == "const" else None
                    found.setdefault(operand["address"], set()).add(value)
    return found


BLINK_UART = (
    "from pymcu.types import uint8\n"
    "from pymcu.hal.gpio import Pin\n"
    "from pymcu.hal.pic14.pic14_uart import uart_init, uart_write\n"
    "\n"
    "def main():\n"
    "    led = Pin(\"RB3\", Pin.OUT)\n"
    "    uart_init(9600)\n"
    "    while True:\n"
    "        led.high()\n"
    "        uart_write(0x41)\n"
    "        led.low()\n"
)


def test_the_chip_builds_at_all(tmp_path):
    proc, _ = build_ir(tmp_path, BLINK_UART)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


def test_gpio_turns_the_comparators_off(tmp_path):
    _, ir = build_ir(tmp_path, BLINK_UART)
    assert 0x07 in touched(ir).get(CMCON, set()), \
        "CMCON never set to 0x07: RA0-RA3 stay analog and read 0 for ever"


def test_uart_pins_are_configured_as_inputs(tmp_path):
    """The PIC16 family trap: the EUSART only reaches RB1/RB2 while TRISB says input."""
    _, ir = build_ir(tmp_path, BLINK_UART)
    bits = {(i["target"]["address"], i["bit"], i["$t"])
            for f in ir["functions"] for i in f["body"]
            if i.get("$t") in ("bset", "bclr") and isinstance(i.get("target"), dict)}
    assert (TRISB, 1, "bset") in bits, "TRISB<1> (RX) must be SET, not cleared"
    assert (TRISB, 2, "bset") in bits, "TRISB<2> (TX) must be SET, not cleared"


@pytest.mark.parametrize("baud,spbrg", [(2400, 103), (4800, 51), (9600, 25), (19200, 12)])
def test_baud_table_matches_a_4mhz_brgh_clock(tmp_path, baud, spbrg):
    source = BLINK_UART.replace("uart_init(9600)", f"uart_init({baud})")
    _, ir = build_ir(tmp_path, source)
    assert spbrg in touched(ir).get(SPBRG, set())


def test_a_baud_rate_that_cannot_be_hit_is_refused(tmp_path):
    source = BLINK_UART.replace("uart_init(9600)", "uart_init(38400)")
    proc, _ = build_ir(tmp_path, source)
    assert "[BUILD_FAIL]" in proc.stdout, "38400 at 4 MHz lands 7% off and must not compile silently"


def test_ra5_cannot_be_an_output(tmp_path):
    source = BLINK_UART.replace('Pin("RB3", Pin.OUT)', 'Pin("RA5", Pin.OUT)')
    proc, _ = build_ir(tmp_path, source)
    assert "[BUILD_FAIL]" in proc.stdout, "RA5 is input-only (MCLR/VPP) and must be refused as an output"


def test_ra4_cannot_be_driven_high(tmp_path):
    source = (
        "from pymcu.hal.pic14.pic16f628a_gpio import pin_set_mode, pin_high\n"
        "\n"
        "def main():\n"
        "    pin_set_mode(\"RA4\", 0)\n"
        "    while True:\n"
        "        pin_high(\"RA4\")\n"
    )
    proc, _ = build_ir(tmp_path, source)
    assert "[BUILD_FAIL]" in proc.stdout, "RA4 is open-drain and cannot source current"


def test_portb_pull_ups_go_through_option_reg(tmp_path):
    source = BLINK_UART.replace('Pin("RB3", Pin.OUT)', 'Pin("RB0", Pin.IN, Pin.PULL_UP)')
    _, ir = build_ir(tmp_path, source)
    bits = {(i["target"]["address"], i["bit"], i["$t"])
            for f in ir["functions"] for i in f["body"]
            if i.get("$t") in ("bset", "bclr") and isinstance(i.get("target"), dict)}
    assert (OPTION_REG, 7, "bclr") in bits, \
        "PORTB pull-ups are gated by OPTION_REG<7> (NOT_RBPU) on this part, not by a WPUB register"


@pytest.mark.skipif(not BACKEND.exists(), reason="PIC backend not built")
def test_the_build_emits_a_config_word(tmp_path):
    src = tmp_path / "main.py"
    src.write_text(BLINK_UART)
    mir = tmp_path / "firmware.mir"
    subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic14",
         "--target", "pic16f628a", "--freq", "4000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)], capture_output=True, text=True, check=False)
    asm = tmp_path / "firmware.asm"
    subprocess.run(
        [str(BACKEND), str(mir), "-o", str(asm), "--target", "pic16f628a", "--arch", "pic14"],
        capture_output=True, text=True, check=False)
    assert "__CONFIG" in asm.read_text()
