"""A factory DigitalInOut passed into a large constructor keeps that class.

adafruit_character_lcd: Character_LCD.__init__(reset_dio: digitalio.DigitalInOut, ...)
receives mcp.get_pin(...). Outlining __init__ used to type the parameters from the
annotation, so pin.high() became HAL gpio.
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
    (tmp_path / "digitalio.py").write_text(
        "from pymcu.types import uint8\n"
        "class DigitalInOut:\n"
        "    def __init__(self, pin: uint8):\n"
        "        self.pin = pin\n"
        "        self._a = 0\n"
        "        self._b = 0\n"
        "        self._c = 0\n"
        "    def high(self):\n"
        "        self.pin = 99\n"
    )
    (tmp_path / "mcp.py").write_text(
        "from pymcu.types import uint8\n"
        "import digitalio\n"
        "class DigitalInOut:\n"
        "    def __init__(self, pin: uint8, parent: MCP):\n"
        "        self.pin = pin\n"
        "        self.parent = parent\n"
        "        self._a = 0\n"
        "        self._b = 0\n"
        "    def get(self) -> uint8:\n"
        "        return self.pin\n"
        "    def high(self):\n"
        "        self.pin = self.pin\n"
        "class MCP:\n"
        "    def __init__(self):\n"
        "        self.n = 1\n"
        "        self._a = 0\n"
        "        self._b = 0\n"
        "        self._c = 0\n"
        "        self._d = 0\n"
        "    def get_pin(self, pin: uint8) -> DigitalInOut:\n"
        "        return DigitalInOut(pin, self)\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.types import uint8\n"
        "from digitalio import DigitalInOut\n"
        "from mcp import MCP\n"
        "class Lcd:\n"
        "    def __init__(self, a: DigitalInOut, b: DigitalInOut, columns: uint8, lines: uint8):\n"
        "        self.columns = columns\n"
        "        self.lines = lines\n"
        "        self.reset = a\n"
        "        self.enable = b\n"
        "        self._n = 0\n"
        "        self._m = 0\n"
        "        for pin in (a, b):\n"
        "            pin.high()\n"
        "def main():\n"
        "    m = MCP()\n"
        "    Lcd(m.get_pin(1), m.get_pin(2), 16, 2)\n"
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
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )


@BOTH_FRONT_ENDS
def test_factory_pin_through_annotated_constructor(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
