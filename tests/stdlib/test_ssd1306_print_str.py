"""SSD1306.print_str has to compile from the call its own docstring shows.

The parameter was annotated `str`, which the for-in unroller does not accept, so
every call site failed with a message that pointed at the call rather than at the
driver -- and the driver's only way of putting text in the framebuffer was
unreachable from anywhere. The sibling HD44780 driver already used `const[str]`
for the same job.

What the method writes is fully folded, so the framebuffer index and the byte
landing in it are both constants in the IR.
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

WIDTH = 128       # framebuffer columns; index = page * 128 + column


def writes(tmp_path: Path, x: int, y: int, text: str):
    """(index, byte) for every store into the framebuffer, in program order."""
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.hal.i2c import I2C\n"
        "from pymcu.drivers.ssd1306 import SSD1306\n\n\n"
        "def main():\n"
        "    i2c = I2C()\n"
        "    oled = SSD1306(i2c, 0x3C)\n"
        f'    oled.print_str({x}, {y}, "{text}")\n'
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
    return [(i["index"]["value"], i["src"]["value"]) for i in main["body"]
            if i.get("$t") == "ast" and i.get("arrayName") == "_ssd1306_buf"]


def test_the_call_the_docstring_shows_compiles(tmp_path):
    assert writes(tmp_path, 0, 0, "Hi!") == [(0, ord("H")), (1, ord("i")), (2, ord("!"))]


def test_the_text_starts_at_the_requested_page_and_column(tmp_path):
    # y = 16 is page 2, so the first byte lands at 2 * 128 + 5.
    assert writes(tmp_path, 5, 16, "abc") == [(261, ord("a")), (262, ord("b")),
                                              (263, ord("c"))]


def test_characters_past_the_last_column_are_dropped(tmp_path):
    # The third character would be column 128, off the panel and into the next page.
    assert writes(tmp_path, WIDTH - 2, 0, "abc") == [(126, ord("a")), (127, ord("b"))]


def test_an_empty_string_writes_nothing(tmp_path):
    assert writes(tmp_path, 0, 0, "") == []
