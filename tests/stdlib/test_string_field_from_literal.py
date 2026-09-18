"""A string-literal first store types the field as str, not uint8.

adafruit_character_lcd writes `self._message = ""` in __init__ and then
`self._message = message` in the setter. adafruit_74hc595 writes
`self._gpio = bytearray(n)` then assigns the buffer again in the setter.
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


LCD = (
    "class Lcd:\n"
    "    def __init__(self):\n"
    "        self._message = \"\"\n"
    "    @property\n"
    "    def message(self):\n"
    "        return self._message\n"
    "    @message.setter\n"
    "    def message(self, message: str):\n"
    "        self._message = message\n"
    "\n"
    "def main():\n"
    "    l = Lcd()\n"
    "    l.message = \"Hi\"\n"
)


GPIO = (
    "class Shift:\n"
    "    def __init__(self, n: int):\n"
    "        self._gpio = bytearray(n)\n"
    "    @property\n"
    "    def gpio(self):\n"
    "        return self._gpio\n"
    "    @gpio.setter\n"
    "    def gpio(self, val: bytearray):\n"
    "        self._gpio = val\n"
    "\n"
    "def main():\n"
    "    s = Shift(1)\n"
)


@BOTH_FRONT_ENDS
def test_empty_string_field_then_str_setter_builds(tmp_path, py_parser):
    proc = frontend(tmp_path, LCD, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@BOTH_FRONT_ENDS
def test_bytearray_field_then_buffer_setter_builds(tmp_path, py_parser):
    proc = frontend(tmp_path, GPIO, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


GPIO_FROM_FIELD = (
    "class Shift:\n"
    "    def __init__(self, n: int):\n"
    "        self._number_of_shift_registers = n\n"
    "        self._gpio = bytearray(self._number_of_shift_registers)\n"
    "\n"
    "def main():\n"
    "    s = Shift(1)\n"
)


@BOTH_FRONT_ENDS
def test_bytearray_sized_from_field_builds(tmp_path, py_parser):
    proc = frontend(tmp_path, GPIO_FROM_FIELD, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
