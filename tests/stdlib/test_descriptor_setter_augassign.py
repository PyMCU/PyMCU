"""Descriptor __set__ that shifts its value parameter still compiles.

adafruit_register.RWBits.__set__ does:

    value <<= self.lowest_bit
    reg |= value

The descriptor rewrite hands value in as a compile-time constant. After the
shift the name must still resolve, or ina219 / veml7700 stop there.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

BOTH_FRONT_ENDS = pytest.mark.parametrize("py_parser", [False, True], ids=["cs", "bridge"])


def frontend(tmp_path: Path, py_parser: bool = False):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.types import uint8\n"
        "class Field:\n"
        "    def __init__(self, shift: uint8) -> None:\n"
        "        self.shift = shift\n"
        "        self.mask: uint8 = 240\n"
        "    def __set__(self, obj, value: uint8) -> None:\n"
        "        value <<= self.shift\n"
        "        obj.reg &= ~self.mask\n"
        "        obj.reg |= value\n"
        "class Dev:\n"
        "    bits = Field(4)\n"
        "    def __init__(self) -> None:\n"
        "        self.reg: uint8 = 0\n"
        "d = Dev()\n"
        "def main():\n"
        "    d.bits = 3\n"
        "    n = d.reg\n"
    )
    mir = tmp_path / "firmware.mir"
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    return subprocess.run(
        [str(PYMCUC), str(src), "-o", os.devnull, "--arch", "avr",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )


@BOTH_FRONT_ENDS
def test_descriptor_setter_keeps_value_after_shift(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "is not defined" not in combined
