"""math.floor, math.ceil and math.trunc exist, and an integer argument is accepted.

They did not exist, and asking for one produced

    error: CompileError: call to undefined function 'math_floor' (typo, or a missing import?)

which names the compiler's internal symbol rather than `math.floor`, and suggests an import
the program had already made (issue #174).

What this file can check is that the names resolve and that each accepts both an integer and
a float. It deliberately does NOT check the rounding: the arguments have to be run-time values
for the answer to be interesting, and a run-time answer needs a running chip. The nine-value
sweep against CPython lives in the math-rounding fixture in the pymcu-avr repo, which seeds
GPIOR0 and reads the three answers back through the CPU at a break.
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

ROUNDERS = ["floor", "ceil", "trunc"]


def build(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
        "from pymcu.types import uint8, int32, asm\n"
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


@pytest.mark.parametrize("name", ROUNDERS)
def test_a_float_argument_builds(tmp_path, name):
    """Before #174 this was 'call to undefined function math_<name>'."""
    out, ir = build(tmp_path, f"    r: int32 = math.{name}((float(seed) - 4.0) / 2.0)\n"
                              "    GPIOR1.value = uint8(r & 0xFF)\n")
    assert ir is not None, out
    assert "undefined function" not in out


@pytest.mark.parametrize("name", ROUNDERS)
def test_an_integer_argument_builds(tmp_path, name):
    """The shape the issue reported: math.floor of a uint8, which is already whole."""
    out, ir = build(tmp_path, f"    GPIOR1.value = uint8(math.{name}(seed))\n")
    assert ir is not None, out
    assert "undefined function" not in out


@pytest.mark.parametrize("name", ROUNDERS)
def test_the_error_no_longer_names_an_internal_symbol(tmp_path, name):
    """The half of #174 that is about the message rather than the feature."""
    out, _ = build(tmp_path, f"    r: int32 = math.{name}((float(seed) - 4.0) / 2.0)\n"
                             "    GPIOR1.value = uint8(r & 0xFF)\n")
    assert f"math_{name}" not in out, \
        f"the diagnostic still names the internal symbol math_{name}"


def test_a_name_math_really_does_not_have_still_fails(tmp_path):
    """The guard on the above: adding three names must not make every name resolve."""
    out, ir = build(tmp_path, "    r: int32 = math.arctan(float(seed))\n"
                              "    GPIOR1.value = uint8(r & 0xFF)\n")
    assert ir is None, "math.arctan does not exist and must not build"
