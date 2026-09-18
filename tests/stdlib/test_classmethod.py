"""@classmethod compiles on both front ends.

Adafruit sht4x/tmp117 write Mode.add_values((...)) to populate class
attributes: setattr(cls, name, value) and cls.string[k] = v.
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


FACTORY = (
    "from pymcu.types import uint8\n"
    "class A:\n"
    "    @classmethod\n"
    "    def make(cls) -> uint8:\n"
    "        return 77\n"
    "def main() -> uint8:\n"
    "    return A.make()\n"
)

CV = (
    "from pymcu.types import uint8\n"
    "class CV:\n"
    "    @classmethod\n"
    "    def add_values(cls, value_tuples):\n"
    "        cls.string = {}\n"
    "        for value_tuple in value_tuples:\n"
    "            name, value, string, delay = value_tuple\n"
    "            setattr(cls, name, value)\n"
    "            cls.string[value] = string\n"
    "    @classmethod\n"
    "    def is_valid(cls, value: uint8) -> uint8:\n"
    "        return 1 if value in cls.string else 0\n"
    "class Mode(CV):\n"
    "    pass\n"
    "Mode.add_values(((\"NOHEAT_HIGHPRECISION\", 0xFD, \"hi\", 0.01),))\n"
    "def main() -> uint8:\n"
    "    return Mode.NOHEAT_HIGHPRECISION if Mode.is_valid(0xFD) else 0\n"
)


@BOTH_FRONT_ENDS
def test_classmethod_factory_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = compile_(tmp_path, FACTORY, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "@classmethod is not supported" not in combined


@BOTH_FRONT_ENDS
def test_classmethod_cv_add_values_compiles_on_both_front_ends(tmp_path, py_parser):
    proc = compile_(tmp_path, CV, py_parser)
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    assert "runtime reflection" not in combined
    assert "elements must be compile-time integer constants" not in combined
