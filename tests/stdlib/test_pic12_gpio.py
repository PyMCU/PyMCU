import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

GPIO, TRISGPIO, OPTION = 0x06, 0x86, 0x81

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build_ir(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text("from pymcu.hal.gpio import Pin\n\n\ndef main():\n" + body)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "pic12",
         "--target", "pic10f200", "--freq", "4000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def bit_ops(ir):
    return {(i["target"]["address"], i["bit"], i["$t"])
            for f in ir["functions"] for i in f["body"]
            if i.get("$t") in ("bset", "bclr") and isinstance(i.get("target"), dict)}


BLINK = (
    '    led = Pin("GP1", Pin.OUT)\n'
    "    while True:\n"
    "        led.high()\n"
    "        led.low()\n"
)


def test_the_portable_pin_reaches_this_chip(tmp_path):
    """hal/gpio.py routed avr, pic14, pic18, arm and riscv -- never pic12.

    The whole PIC10F200 GPIO implementation existed and was unreachable through
    the facade every program actually imports.
    """
    proc, ir = build_ir(tmp_path, BLINK)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    ops = bit_ops(ir)
    assert (TRISGPIO, 1, "bclr") in ops, "GP1 as OUT must clear TRISGPIO<1>"
    assert (GPIO, 1, "bset") in ops and (GPIO, 1, "bclr") in ops


def test_a_pin_that_does_not_exist_is_refused(tmp_path):
    """Every helper fell off the end of its if-chain and emitted nothing."""
    proc, _ = build_ir(tmp_path, BLINK.replace('"GP1"', '"GP9"'))
    assert "[BUILD_FAIL]" in proc.stdout, \
        "a misspelled pin compiled to a program that silently does nothing"


def test_gp3_can_be_read(tmp_path):
    """GP3 is a real pin and was missing from all six helpers."""
    proc, ir = build_ir(
        tmp_path,
        '    button = Pin("GP3", Pin.IN)\n'
        '    led = Pin("GP0", Pin.OUT)\n'
        "    while True:\n"
        "        if button.value() == 1:\n"
        "            led.high()\n")
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert (TRISGPIO, 3, "bset") in bit_ops(ir), "GP3 must be left as an input"
    assert any(i.get("$t") == "bchk" and i["source"].get("address") == GPIO
               and i.get("bit") == 3
               for f in ir["functions"] for i in f["body"]), \
        "reading GP3 emitted no test of GPIO<3>"


def test_gp3_cannot_be_an_output(tmp_path):
    proc, _ = build_ir(tmp_path, BLINK.replace('"GP1"', '"GP3"'))
    assert "[BUILD_FAIL]" in proc.stdout, "GP3 is input-only on this part"


def test_a_per_pin_pull_up_is_refused_instead_of_faked(tmp_path):
    """It used to emit a single-bit write to OPTION, which cannot be read back.

    NOT_GPPU covers the whole port, so honouring the call for one pin is not
    possible; the old code produced a read-modify-write of a write-only register.
    """
    proc, _ = build_ir(
        tmp_path,
        '    b = Pin("GP0", Pin.IN, Pin.PULL_UP)\n'
        "    while True:\n"
        "        pass\n")
    assert "[BUILD_FAIL]" in proc.stdout, \
        "a per-pin pull-up must not compile to a bogus bit write on OPTION"


def test_nothing_touches_option_through_the_gpio_path(tmp_path):
    proc, ir = build_ir(tmp_path, BLINK)
    assert not [b for b in bit_ops(ir) if b[0] == OPTION], \
        "GPIO must not poke OPTION: it is write-only and the timer owns it"
