"""The baud selector is the rate in hundreds, so it does not fit a uint8.

`pic16f18877_uart.uart_init` declared `baud: uint8` while selecting on 384, 576
and 1152 -- 38400, 57600 and 115200. A uint8 holds none of them, so those three
arms were unreachable by the declaration and taken in practice only while an
@inline argument arrived unnarrowed. Narrowed, 1152 becomes 128, no arm matches,
and SP1BRGL/SP1BRGH are never written: the UART keeps whatever the reset value
left in the generator and talks at the wrong rate, silently.

The slow rates were never at risk (96 and 192 fit a uint8); they are here so a
future change to the selector cannot quietly drop them either.
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

SP1BRGL = 0x011B
SP1BRGH = 0x011C

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

PROGRAM = (
    "from pymcu.hal.pic14.pic16f18877_uart import uart_init, uart_write\n"
    "\n"
    "uart_init({baud})\n"
    "uart_write(65)\n"
)

# selector, and the SP1BRGL divisor that rate must program
RATES = [(96, 103), (192, 51), (384, 25), (576, 16), (1152, 8)]


def const_stores(tmp_path, baud):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(baud=baud))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", "pic14e",
         "--target", "pic16f18877", "--freq", "32000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    found = {}
    for func in json.loads(mir.read_text())["functions"]:
        for ins in func["body"]:
            dst, s = ins.get("dst"), ins.get("src")
            if (isinstance(dst, dict) and dst.get("$t") == "mem"
                    and isinstance(s, dict) and s.get("$t") == "const"):
                found.setdefault(dst["address"], set()).add(s["value"])
    return found


@pytest.mark.parametrize("baud,divisor", RATES, ids=[str(r[0]) for r in RATES])
def test_the_rate_programs_the_baud_generator(tmp_path, baud, divisor):
    written = const_stores(tmp_path, baud)
    assert SP1BRGL in written, \
        f"uart_init({baud}) wrote nothing to SP1BRGL; no arm of the selector matched"
    assert divisor in written[SP1BRGL], \
        f"uart_init({baud}) programmed {sorted(written[SP1BRGL])}, not {divisor}"
    assert SP1BRGH in written, f"uart_init({baud}) left SP1BRGH unwritten"
