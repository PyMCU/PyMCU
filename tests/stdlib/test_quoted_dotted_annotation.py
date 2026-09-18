"""Quoted dotted annotations compile on both front ends.

Adafruit si7021 writes `obj: "adafruit_si7021.SI7021"`.
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


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    mir = tmp_path / "firmware.mir"
    return subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", os.devnull, "--arch", "avr",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )


SRC = (
    "from pymcu.types import uint8\n"
    "class Pack:\n"
    "    class Dev:\n"
    "        def __init__(self):\n"
    "            self.a: uint8 = 17\n"
    "def read(obj: \"Pack.Dev\") -> uint8:\n"
    "    return obj.a\n"
    "def main():\n"
    "    n = read(Pack.Dev())\n"
)


@BOTH_FRONT_ENDS
def test_quoted_dotted_annotation_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = compile_(tmp_path, SRC, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "forward reference" not in combined
