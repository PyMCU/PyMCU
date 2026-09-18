"""Adafruit TYPE_CHECKING guard must keep PWMOut from the try body.

motor.py / servo.py wrap `from pwmio import PWMOut` in
`except NotImplementedError` inside an outer `except ImportError`.
The inner except used to drop PWMOut (#480) or load the missing stub (#481).
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
    (tmp_path / "pwmio.py").write_text(
        "from pymcu.types import uint8\n"
        "class PWMOut:\n"
        "    def __init__(self) -> None:\n"
        "        self.n: uint8 = 0\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.types import uint8\n"
        "try:\n"
        "    from typing import Optional, Type\n"
        "    try:\n"
        "        from pwmio import PWMOut\n"
        "    except NotImplementedError:\n"
        "        from circuitpython_typing.pwmio import PWMOut\n"
        "except ImportError:\n"
        "    pass\n"
        "def take(h: PWMOut) -> uint8:\n"
        "    return 1\n"
        "def main():\n"
        "    n = 1\n"
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
         "-I", str(STDLIB), "-I", str(tmp_path),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )


@BOTH_FRONT_ENDS
def test_type_checking_guard_keeps_pwmout_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "unknown type" not in combined.lower()
    assert "circuitpython_typing" not in combined
