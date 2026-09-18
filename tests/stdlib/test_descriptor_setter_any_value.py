"""Descriptor __set__ with value: Any still compiles when the body reads value.

adafruit_register.UnaryStruct.__set__ annotates value: Any and then
struct.pack_into(..., value). Any means whatever the caller wrote.
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
        "    def __init__(self) -> None:\n"
        "        pass\n"
        "    def __set__(self, obj, value: Any) -> None:\n"
        "        obj.reg = value\n"
        "class Dev:\n"
        "    bits = Field()\n"
        "    def __init__(self) -> None:\n"
        "        self.reg: uint8 = 0\n"
        "d = Dev()\n"
        "def main():\n"
        "    d.bits = 7\n"
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
def test_descriptor_set_with_any_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "no width" not in combined
    assert "annotated 'Any'" not in combined
