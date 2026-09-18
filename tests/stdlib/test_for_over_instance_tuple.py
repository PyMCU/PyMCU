"""A for over a tuple of already-constructed instances unrolls.

adafruit_character_lcd writes `for pin in (reset_dio, enable_dio, ...)`.
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


def frontend(tmp_path: Path, source: str, py_parser: bool = False):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    return subprocess.run(
        [str(PYMCUC), str(src), "-o", os.devnull, "--arch", "avr",
         "--target", "atmega328p", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )


SRC = (
    "from pymcu.types import uint8\n"
    "class Pin:\n"
    "    def __init__(self, n: uint8):\n"
    "        self.n = n\n"
    "    def bump(self):\n"
    "        self.n = self.n + 1\n"
    "\n"
    "class Lcd:\n"
    "    def __init__(self, a: Pin, b: Pin):\n"
    "        for p in (a, b):\n"
    "            p.bump()\n"
    "\n"
    "def main():\n"
    "    x = Pin(0)\n"
    "    y = Pin(0)\n"
    "    Lcd(x, y)\n"
)


@BOTH_FRONT_ENDS
def test_for_over_instance_tuple_builds(tmp_path, py_parser):
    proc = frontend(tmp_path, SRC, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


SETTER = (
    "from pymcu.types import uint8\n"
    "class Pin:\n"
    "    def __init__(self):\n"
    "        self._d = 0\n"
    "    @property\n"
    "    def direction(self):\n"
    "        return self._d\n"
    "    @direction.setter\n"
    "    def direction(self, d: uint8):\n"
    "        self._d = d\n"
    "class Lcd:\n"
    "    def __init__(self, a: Pin, b: Pin):\n"
    "        for p in (a, b):\n"
    "            p.direction = 1\n"
    "def main():\n"
    "    Lcd(Pin(), Pin())\n"
)


@BOTH_FRONT_ENDS
def test_property_setter_through_loop_var_builds(tmp_path, py_parser):
    proc = frontend(tmp_path, SETTER, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
