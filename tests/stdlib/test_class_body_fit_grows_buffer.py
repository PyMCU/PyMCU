"""Class-body _fit must keep a buffer grown past bytearray(1).

adafruit_register starts _BUFFER at one byte. RWBits.__init__ calls _fit(width)
from a class-body constructor. Replaying the declaration used to shrink it.
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
        "_BUFFER = bytearray(1)\n"
        "def _fit(size: uint8) -> None:\n"
        "    if len(_BUFFER) < 1 + size:\n"
        "        _BUFFER.extend(bytes(1 + size - len(_BUFFER)))\n"
        "class Field:\n"
        "    def __init__(self, width: uint8) -> None:\n"
        "        self.width = width\n"
        "        _fit(width)\n"
        "class Dev:\n"
        "    bits = Field(2)\n"
        "d = Dev()\n"
        "def main():\n"
        "    n = _BUFFER[2]\n"
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
def test_class_body_fit_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "out of range" not in combined
