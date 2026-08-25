"""lcd.set_cursor(col, row) must land on the column it was given.

The HD44780 DDRAM address is a row base plus the column, and the bases of the
bottom two lines of a four-line panel -- 0x14 and 0x54 -- carry bits inside the
column field. Combining them with OR is right for rows 0 and 1 and wrong for
rows 2 and 3: `4 | 0x14` is 0x14, column 0, and `19 | 0x14` is 0x17, the same
address `7 | 0x14` produces. Twelve of the thirty-two positions below were wrong
that way, and four pairs of them collided.

The whole call folds at compile time, so the command byte the driver clocks out
is a single constant in the IR and can be read straight off it.
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

# HD44780 DDRAM row bases, and the Set DDRAM Address opcode.
ROW_BASE = (0x00, 0x40, 0x14, 0x54)
SET_DDRAM = 0x80

COLUMNS = (0, 3, 4, 5, 7, 12, 15, 19)
CASES = [(c, r) for r in range(4) for c in COLUMNS]


def commands(tmp_path: Path, col: int, row: int):
    """Every byte the driver sends with RS low, read out of the folded IR."""
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.drivers.lcd import LCD\n\n\n"
        "def main():\n"
        '    lcd = LCD(rs="PD4", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")\n'
        f"    lcd.set_cursor({col}, {row})\n"
        "    while True:\n"
        "        pass\n"
    )
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    ir = json.loads(mir.read_text())
    main = next(f for f in ir["functions"] if f["name"] == "main")
    return [i["src"]["value"] for i in main["body"]
            if i.get("$t") == "copy"
            and i["src"].get("$t") == "const"
            and "_lcd_send_byte_impl.val" in str(i["dst"].get("name"))]


@pytest.mark.parametrize("col,row", CASES, ids=[f"col{c}_row{r}" for c, r in CASES])
def test_set_cursor_addresses_the_requested_cell(tmp_path, col, row):
    sent = commands(tmp_path, col, row)
    want = SET_DDRAM + ROW_BASE[row] + col
    assert sent == [want], (
        f"set_cursor({col}, {row}) sent {[hex(b) for b in sent]}, expected {hex(want)}")


def test_the_bottom_two_rows_do_not_collide_with_the_first_columns(tmp_path):
    """`4 | 0x14` and `0 | 0x14` are the same byte; `+` keeps them apart."""
    assert commands(tmp_path, 4, 2) != commands(tmp_path, 0, 2)
    assert commands(tmp_path, 19, 2) != commands(tmp_path, 7, 2)
