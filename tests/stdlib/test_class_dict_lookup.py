"""A class-level dict indexed through self compiles on both front ends.

adafruit_veml7700.VEML7700.gain_value does return self.gain_values[gain]
with gain_values a class-body dict. That was refused as a bit index.
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
    (tmp_path / "sensor.py").write_text(
        "from pymcu.types import uint8\n"
        "class Dev:\n"
        "    ALS_GAIN_1 = 0\n"
        "    ALS_GAIN_2 = 1\n"
        "    ALS_GAIN_X = 2\n"
        "    vals = {ALS_GAIN_2: 2, ALS_GAIN_1: 1, ALS_GAIN_X: 0.25}\n"
        "    def scaled(self, n: uint8) -> uint8:\n"
        "        return uint8(self.vals[n] * 100)\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from sensor import Dev\n"
        "d = Dev()\n"
        "def main():\n"
        "    n = d.scaled(2)\n"
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
def test_class_dict_lookup_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "Bit index" not in combined
