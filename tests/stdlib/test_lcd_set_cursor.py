"""lcd.set_cursor(col, row) must land on the column it was given.

The HD44780 DDRAM address is a row base plus the column, and the bases of the
bottom two lines of a four-line panel -- 0x14 and 0x54 -- carry bits inside the
column field. Combining them with OR is right for rows 0 and 1 and wrong for
rows 2 and 3: `4 | 0x14` is 0x14, column 0, and `19 | 0x14` is 0x17, the same
address `7 | 0x14` produces. Twelve of the thirty-two positions below were wrong
that way, and four pairs of them collided.

The whole call folds at compile time, so the command byte the driver clocks out
is decided in the IR and can be read straight off it. Since PyMCU#327 it is decided
one step further down: the byte no longer passes through a parameter slot, it is the
bit writes to the data pins, which is what the panel receives.
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
    return _bytes_clocked_out(ir)


# PORTD and PORTB on the ATmega328P, and where the source above wires the panel:
# rs PD4, en PD5, d4 PD6, d5 PD7, d6 PB0, d7 PB1.
PORTD, PORTB = 0x2B, 0x25
RS = (PORTD, 4)
EN = (PORTD, 5)
DATA = [(PORTD, 6), (PORTD, 7), (PORTB, 0), (PORTB, 1)]   # d4 d5 d6 d7


def _bytes_clocked_out(ir):
    """Every byte the driver clocks out with RS low, read off the pin writes.

    The command byte used to reach the send routine through a parameter slot, and this test
    read the constant copied into it. With the whole call folded (PyMCU#327) there is no slot
    left: the nibbles are written straight to the pins as bit sets and clears. That is what
    the panel receives, so it is what this reads -- two nibbles per enable pulse, high first.

    Where both nibbles are the same byte (0xCC, 0x99) the two sends are identical and the
    outliner shares one body, so the walk follows a call into it.
    """
    by_name = {f["name"]: f for f in ir["functions"]}

    def flatten(body, depth=0):
        for i in body:
            if (i.get("$t") == "call" and depth < 4
                    and str(i.get("functionName", "")).startswith("__pymcu_outline_")):
                yield from flatten(by_name[i["functionName"]]["body"], depth + 1)
            else:
                yield i

    body = list(flatten(next(f for f in ir["functions"] if f["name"] == "main")["body"]))
    level = {}
    nibbles = []
    rs_of = []
    for i in body:
        kind = i.get("$t")
        if kind not in ("bset", "bclr"):
            continue
        pin = (i["target"]["address"], i["bit"])
        was = level.get(pin, 0)
        level[pin] = 1 if kind == "bset" else 0
        # The enable pulse latches the nibble on its falling edge.
        if pin == EN and was == 1 and level[pin] == 0:
            nibbles.append(sum(level.get(p, 0) << n for n, p in enumerate(DATA)))
            rs_of.append(level.get(RS, 0))

    out = []
    for n in range(0, len(nibbles) - 1, 2):
        if rs_of[n] == 0:                      # RS low: a command, not data
            out.append((nibbles[n] << 4) | nibbles[n + 1])
    return out


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
