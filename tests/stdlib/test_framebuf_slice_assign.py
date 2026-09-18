"""framebuf buf[i:i+3] = bytes(fill) compiles on both front ends.

Adafruit framebuf writes that after
`fill = (color >> 16) & 255, (color >> 8) & 255, color & 255`.
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
    "from pymcu.types import uint8, uint32\n"
    "buf = bytearray(6)\n"
    "def blit(i: uint8, color: uint32):\n"
    "    fill = (color >> 16) & 255, (color >> 8) & 255, color & 255\n"
    "    buf[i:i+3] = bytes(fill)\n"
    "def main():\n"
    "    blit(0, 0x112233)\n"
    "    blit(3, 0x112233)\n"
    "    n = buf[1]\n"
)


@BOTH_FRONT_ENDS
def test_framebuf_slice_assign_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = compile_(tmp_path, SRC, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "slice assignment" not in combined
