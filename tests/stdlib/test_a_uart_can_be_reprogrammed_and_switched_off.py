"""The AVR UART can be reprogrammed, switched off, and asked whether its buffer drained.

pymcu.hal.avr.uart.UART had a constructor and nothing else of a UART's lifecycle, so the
MicroPython layer could not offer init(), deinit() or flush() (PyMCU#451). Pinned here, on
each of the three USART register maps the HAL covers: reinit() writes the frame register
the constructor writes, deinit() clears the enable register, and tx_empty() reads the
data-register-empty flag (UDRE, bit 5 of UCSRnA).
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

# chip -> (UCSRnA, UCSRnB, UCSRnC) data-space addresses
REGISTERS = {
    "atmega328p": (0xC0, 0xC1, 0xC2),
    "atmega32u4": (0xC8, 0xC9, 0xCA),
    "attiny2313": (0x2B, 0x2A, 0x43),
}

PROGRAM = """\
from pymcu.hal.uart import UART
from pymcu.chips.{chip} import PORTB

u = UART(9600)
u.reinit(9600, 7, 2, 2)
if u.tx_empty():
    PORTB[0] = 1
u.deinit()
"""


def build(tmp_path, chip):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(chip=chip))
    mir = tmp_path / "firmware.mir"
    freq = "8000000" if chip == "attiny2313" else "16000000"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "avr",
         "--target", chip, "--freq", freq, "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def writes(ir, body, address):
    """Values written to one register, in order: stored in place, or passed to an outlined
    copy of the init sequence (the two UART inits share one, and the frame value is its
    argument)."""
    writers = {fn["name"] for fn in ir["functions"]
               if any(i.get("$t") == "copy" and isinstance(i.get("dst"), dict)
                      and i["dst"].get("address") == address
                      and i["src"].get("$t") != "const" for i in fn["body"])}
    out = []
    for i in body:
        if i.get("$t") == "copy" and isinstance(i.get("dst"), dict) \
                and i["dst"].get("address") == address:
            out.append(i["src"].get("value"))
        elif i.get("$t") == "call" and i.get("functionName") in writers:
            out.extend(a.get("value") for a in i["args"])
    return out


@pytest.mark.parametrize("chip", sorted(REGISTERS))
def test_reinit_deinit_and_tx_empty(tmp_path, chip):
    proc, ir = build(tmp_path, chip)
    assert proc.returncode == 0, proc.stderr
    ucsra, ucsrb, ucsrc = REGISTERS[chip]
    body = next(fn for fn in ir["functions"] if fn["name"] == "main")["body"]

    # 8N1 from the constructor, then 7 data bits, odd parity, 2 stop bits from reinit():
    # UPM = 11, USBS = 1, UCSZ = 10 -> 0x3C.
    assert writes(ir, body, ucsrc) == [0x06, 0x3C]
    # deinit() switches TX and RX off: the last write to UCSRnB in main is 0.
    assert [i["src"].get("value") for i in body
            if i.get("$t") == "copy" and isinstance(i.get("dst"), dict)
            and i["dst"].get("address") == ucsrb][-1] == 0
    # tx_empty() tests UDRE, bit 5 of UCSRnA.
    assert any(i.get("$t") in ("jbs", "jbc") and i["source"].get("address") == ucsra
               and i["bit"] == 5 for i in body), "tx_empty() has to test UCSRnA bit 5"
