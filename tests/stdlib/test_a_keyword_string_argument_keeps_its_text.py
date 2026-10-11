"""A string passed BY KEYWORD to a parameter the callee rebinds kept its text only as a literal.

Adafruit_CircuitPython_NeoPixel's `NeoPixel(pin, n, pixel_order=ORDER)` with
`ORDER = neopixel.GRB` in the user's file died in adafruit_pixelbuf with "len() argument must
be a fixed-size array or list literal". The positional binder asks a variable for its text;
the keyword binder aliased the parameter to the variable, and when the callee WRITES the
parameter (`if not pixel_order: pixel_order = GRB`) it dropped the alias and copied the
variable's slot, which for a string is its interned id. The parameter became an integer, and
`len()` of it had nothing to measure.

A keyword-only parameter (`*, pixel_order=None`) always takes the keyword path, so the shape
below is the one the library has. Three spellings of the argument are pinned, because they
reach the binder as three different values: a literal (text in a side table), a name bound to
an imported module's string (a Variable holding the id), and a name bound to a folded
expression (a Constant holding the id with no text).
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]

MODULE = 'GRB = "GRB"\n'

PROGRAM = """\
import mod
from pymcu.chips.atmega328p import GPIOR0

GRB = mod.GRB
ORDER = {order}


class P:
    def __init__(self, size, *, byteorder: str = "BGR"):
        self.bpp = len(byteorder)


class N(P):
    def __init__(self, n, *, pixel_order: str = None):
        if not pixel_order:
            pixel_order = GRB
        super().__init__(n, byteorder=pixel_order)


def main():
    p = N(8, pixel_order={arg})
    GPIOR0.value = p.bpp
"""


def build(tmp_path: Path, order: str, arg: str, py_parser: bool):
    (tmp_path / "mod.py").write_text(MODULE)
    (tmp_path / "main.py").write_text(PROGRAM.format(order=order, arg=arg))
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    return subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--arch", "avr", "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True, env=env,
    )


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("order,arg", [
    pytest.param('"GRB"', "ORDER", id="name-bound-to-a-literal"),
    pytest.param("mod.GRB", "ORDER", id="name-bound-to-a-module-string"),
    pytest.param('"GR" + "B"', "ORDER", id="name-bound-to-a-folded-string"),
    pytest.param('"GRB"', '"GRB"', id="literal"),
])
def test_the_text_of_a_keyword_string_argument_reaches_the_callee(tmp_path, py_parser, order, arg):
    proc = build(tmp_path, order, arg, py_parser)

    assert proc.returncode == 0, proc.stderr
