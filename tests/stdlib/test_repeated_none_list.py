"""[None] * N is a fixed array, including a field sized by len(self).

adafruit_dps310 writes coeffs = [None] * 18 then fills it in a range loop.
adafruit_pca9685 writes self._channels = [None] * len(self) in __init__,
with __len__ defined after that and returning a plain int.
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
        "class Dev:\n"
        "    def __init__(self) -> None:\n"
        "        self.ch = [None] * len(self)\n"
        "        self.ch[2] = 7\n"
        "    def __len__(self) -> int:\n"
        "        return 4\n"
        "    def fill(self) -> uint8:\n"
        "        coeffs = [None] * 18\n"
        "        for offset in range(18):\n"
        "            coeffs[offset] = offset\n"
        "        return coeffs[6]\n"
        "d = Dev()\n"
        "def main():\n"
        "    n = d.fill()\n"
        "    m = d.ch[2]\n"
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
def test_repeated_none_list_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "list literal has no value" not in combined
