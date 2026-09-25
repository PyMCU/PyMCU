"""math.sqrt, exp, log, log10, radians and degrees exist and take a run-time float.

They did not exist, and four unmodified upstream libraries stopped on that alone:

    adafruit_mpu6050.py:42:1: error: ImportError: cannot import 'radians' from 'math'
    adafruit_lsm6ds/__init__.py:59:1: error: ImportError: cannot import 'radians' from 'math'
    adafruit_sgp30.py:29:1: error: ImportError: cannot import 'exp' from 'math'
    adafruit_max31865.py:286:22: error: CompileError: call to undefined function 'math_sqrt'

What this file checks is that the names resolve, that each takes a run-time float, and
that they lower LAZILY: a program that never calls one carries none of the series. The
VALUES are checked where a value can be checked, against CPython running the same
program -- tests/oracle/probes/278_math_sqrt_exp_log_radians.py in the pymcu-avr repo.
"""

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

REAL_VALUED = ["sqrt", "exp", "log", "log10", "radians", "degrees"]


def build(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
        "from pymcu.types import uint8, int32\n"
        "import math\n\n\n"
        "def main():\n"
        "    seed: uint8 = GPIOR0.value\n"
        + body +
        "    while True:\n"
        "        pass\n"
    )
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc.stdout + proc.stderr, (json.loads(mir.read_text()) if mir.exists() else None)


def call_body(name: str) -> str:
    return (f"    r: int32 = int32(math.{name}(float(seed) + 1.0) * 100.0)\n"
            "    GPIOR1.value = uint8(r & 0xFF)\n")


@pytest.mark.parametrize("name", REAL_VALUED)
def test_a_runtime_float_argument_builds(tmp_path, name):
    out, ir = build(tmp_path, call_body(name))
    assert ir is not None, out
    assert "undefined function" not in out


@pytest.mark.parametrize("name", REAL_VALUED)
def test_the_error_no_longer_names_an_internal_symbol(tmp_path, name):
    out, _ = build(tmp_path, call_body(name))
    assert f"math_{name}" not in out, \
        f"the diagnostic still names the internal symbol math_{name}"


@pytest.mark.parametrize("name", ["sqrt", "exp", "log"])
def test_the_from_import_spelling_resolves(tmp_path, name):
    """`from math import sqrt` is how the upstream drivers write it."""
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
        "from pymcu.types import uint8, int32\n"
        f"from math import {name}\n\n\n"
        "def main():\n"
        "    seed: uint8 = GPIOR0.value\n"
        f"    r: int32 = int32({name}(float(seed) + 1.0) * 100.0)\n"
        "    GPIOR1.value = uint8(r & 0xFF)\n"
        "    while True:\n"
        "        pass\n"
    )
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    assert mir.exists(), out


def test_the_series_are_absent_from_a_program_that_does_not_call_them(tmp_path):
    """Zero cost: the helper bodies lower only into the programs that call them."""
    _, ir = build(tmp_path, "    GPIOR1.value = seed\n")
    assert ir is not None
    names = {f["name"] for f in ir["functions"]}
    for helper in ("__pymcu_sqrtf", "__pymcu_expf", "__pymcu_logf"):
        assert helper not in names, f"{helper} lowered into a program that never calls it"


def test_only_the_called_helper_is_lowered(tmp_path):
    """sqrt must not drag exp and log in behind it."""
    _, ir = build(tmp_path, call_body("sqrt"))
    assert ir is not None
    names = {f["name"] for f in ir["functions"]}
    assert "__pymcu_sqrtf" in names
    assert "__pymcu_expf" not in names


def test_a_name_math_really_does_not_have_still_fails(tmp_path):
    """The guard: adding six names must not make every name resolve."""
    out, ir = build(tmp_path, "    r: int32 = int32(math.arctan(float(seed)))\n"
                              "    GPIOR1.value = uint8(r & 0xFF)\n")
    assert ir is None, "math.arctan does not exist and must not build"


def test_pi_is_not_defined_rather_than_reading_back_zero(tmp_path):
    """A module-level FLOAT constant in an imported module becomes storage nothing
    initialises, so `math.pi` would read 0.0 instead of refusing. Until that gap is
    closed the name is deliberately absent, and this test says so out loud."""
    out, ir = build(tmp_path, "    r: int32 = int32(math.pi * 100.0)\n"
                              "    GPIOR1.value = uint8(r & 0xFF)\n")
    assert ir is None, "math.pi must refuse, not silently read back zero"
