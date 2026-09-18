"""Subscript unpack compiles on both front ends.

Adafruit sht31d writes
`word[i*2], crc[i*2], word[(i*2)+1], crc[(i*2)+1] = struct.unpack(...)`.
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
    "from pymcu.types import uint8, uint16\n"
    "import struct\n"
    "def main() -> uint8:\n"
    "    data: uint8[6] = [0x12, 0x34, 0xAB, 0x56, 0x78, 0xCD]\n"
    "    word: uint16[4] = [0, 0, 0, 0]\n"
    "    crc: uint8[4] = [0, 0, 0, 0]\n"
    "    i: uint8 = 0\n"
    "    word[i * 2], crc[i * 2], word[(i * 2) + 1], crc[(i * 2) + 1] = struct.unpack(\n"
    "        \">HBHB\", data[i * 6 : (i * 6) + 6])\n"
    "    return crc[0]\n"
)


@BOTH_FRONT_ENDS
def test_indexed_unpack_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = compile_(tmp_path, SRC, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "Expected newline or end of block" not in combined
