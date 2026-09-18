"""PyMCU#467. Rebinding an imported module name to an instance shadows the alias.

from adafruit_motor import servo
servo = servo.Servo(pwm)
print(servo.fraction)

The assignment rebinds servo; later reads and calls must see the instance,
not Unknown module member: adafruit_motor_servo_fraction.
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
    (tmp_path / "thing.py").write_text(
        "from pymcu.types import uint8\n"
        "class Thing:\n"
        "    def __init__(self) -> None:\n"
        "        self.x: uint8 = 0\n"
        "    def get(self) -> uint8:\n"
        "        return self.x\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "import thing\n"
        "def main():\n"
        "    thing = thing.Thing()\n"
        "    thing.x = 7\n"
        "    n = thing.x\n"
        "    m = thing.get()\n"
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
def test_rebound_import_alias_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert "Unknown module member" not in proc.stdout + proc.stderr
    assert "undefined function" not in proc.stdout + proc.stderr
