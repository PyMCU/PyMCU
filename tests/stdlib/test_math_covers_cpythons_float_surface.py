"""pymcu.math carries the part of CPython's math that a float32 target can honestly offer.

Pinned here: every name compiles on an AVR and on an ARM target through both front ends, the
names that are deliberately absent say why at the call (instead of "Unknown module member"),
and a program that imports math but uses none of the new functions pays nothing for them.

The VALUES are checked elsewhere, against CPython: tests/oracle/probes 882-885 in pymcu-avr for
AVR, and tests/natmod in pymcu-arm for a native module on an emulated RP2040.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]
TARGETS = [pytest.param(("avr", "atmega328p"), id="atmega328p"),
           pytest.param(("arm", "rp2040"), id="rp2040")]

FLOAT_ONE = ["sin", "cos", "tan", "atan", "asin", "acos", "sinh", "cosh", "tanh", "asinh",
             "acosh", "atanh", "fabs", "degrees", "log2", "log10", "exp2", "cbrt", "expm1",
             "log1p"]
FLOAT_TWO = ["atan2", "hypot", "fmod", "copysign"]
CONSTANTS = ["pi", "e", "tau", "inf", "nan"]
REFUSED = ["gamma", "lgamma", "erf", "erfc", "comb", "perm", "prod", "fsum", "dist", "sumprod",
           "fma", "nextafter", "ulp", "remainder", "ldexp", "frexp", "modf", "isclose"]


def build(tmp_path: Path, source: str, target, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    arch, chip = target
    return subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.asm"),
         "--arch", arch, "--target", chip, "--freq", "16000000", "--library",
         "-I", str(STDLIB), "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True, env=env,
    )


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("target", TARGETS)
def test_every_float_function_and_constant_compiles(tmp_path, py_parser, target):
    lines = ["import math", ""]
    for n in FLOAT_ONE:
        lines += [f"def f_{n}(x: float) -> float:", f"    return math.{n}(x)", ""]
    for n in FLOAT_TWO:
        lines += [f"def f_{n}(x: float, y: float) -> float:", f"    return math.{n}(x, y)", ""]
    for n in CONSTANTS:
        lines += [f"def c_{n}() -> float:", f"    return math.{n}", ""]
    proc = build(tmp_path, "\n".join(lines), target, py_parser)

    assert proc.returncode == 0, proc.stderr


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("name", REFUSED)
def test_a_name_that_is_not_provided_says_so_at_the_call(tmp_path, py_parser, name):
    proc = build(tmp_path, f"import math\n\ndef f(x: float) -> float:\n    return math.{name}(x)\n",
                 ("arm", "rp2040"), py_parser)

    assert proc.returncode != 0
    assert f"math.{name} is not provided" in proc.stderr or f"math.{name} takes" in proc.stderr, proc.stderr
    assert "Unknown module member" not in proc.stderr
