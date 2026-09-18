"""A tuple of constants assigned to a field compiles on both front ends.

adafruit_dps310.DPS310.__init__ does self._oversample_scalefactor = (524288, ...)
and later indexes it. That was refused as a runtime tuple.
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
        "from pymcu.types import uint8, uint32\n"
        "class Dev:\n"
        "    def __init__(self):\n"
        "        self.scale = (\n"
        "            524288, 1572864, 3670016, 7864320,\n"
        "            253952, 516096, 1040384, 2088960,\n"
        "        )\n"
        "    def at(self, n: uint8) -> uint32:\n"
        "        return self.scale[n]\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from sensor import Dev\n"
        "d = Dev()\n"
        "def main():\n"
        "    n = d.at(6)\n"
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
def test_field_tuple_literal_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "runtime values" not in combined
