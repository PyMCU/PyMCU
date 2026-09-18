"""A descriptor read of self.attr from a method of an imported class compiles.

adafruit_ina219.INA219.bus_voltage does `return self.raw_bus_voltage * 0.004`.
The rewrite named the mangled class key, which is not a bound variable.
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
    (tmp_path / "pack.py").write_text(
        "from pymcu.types import uint8\n"
        "class Field:\n"
        "    def __init__(self, addr: uint8) -> None:\n"
        "        self.addr = addr\n"
        "    def __get__(self, obj, objtype=None) -> uint8:\n"
        "        return self.addr + obj.base\n"
        "    def __set__(self, obj, value: uint8) -> None:\n"
        "        obj.base = value\n"
    )
    (tmp_path / "sensor.py").write_text(
        "from pymcu.types import uint8\n"
        "from pack import Field\n"
        "class Dev:\n"
        "    raw = Field(9)\n"
        "    def __init__(self, base: uint8) -> None:\n"
        "        self.base = base\n"
        "    @property\n"
        "    def volts(self) -> uint8:\n"
        "        return self.raw * 2\n"
    )
    src = tmp_path / "main.py"
    src.write_text(
        "from sensor import Dev\n"
        "d = Dev(2)\n"
        "def main():\n"
        "    n = d.volts\n"
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
def test_descriptor_self_on_imported_class_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = frontend(tmp_path, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "is not defined" not in combined
