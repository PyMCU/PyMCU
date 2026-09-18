"""A local class named DigitalInOut is not digitalio's, even when main imported it.

adafruit_74hc595 writes `return DigitalInOut(pin, self)` next to `import digitalio`.
The entry file typically does `from digitalio import DigitalInOut` for the latch.
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
    )
    (tmp_path / "sr.py").write_text(
        "from pymcu.types import uint8\n"
        "import digitalio\n"
        "class DigitalInOut:\n"
        "    def __init__(self, pin: uint8, parent: ShiftRegister):\n"
        "        self.pin = pin\n"
        "        self.parent = parent\n"
        "    def get(self) -> uint8:\n"
        "        return self.pin\n"
        "class ShiftRegister:\n"
        "    def __init__(self):\n"
        "        self.n = 1\n"
        "    def get_pin(self, pin: uint8):\n"
        "        return DigitalInOut(pin, self)\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from digitalio import DigitalInOut\n"
        "from sr import ShiftRegister\n"
        "def main():\n"
        "    latch = DigitalInOut(0)\n"
        "    s = ShiftRegister()\n"
        "    p = s.get_pin(6)\n"
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
def test_local_digitalinout_is_not_the_imported_one(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
