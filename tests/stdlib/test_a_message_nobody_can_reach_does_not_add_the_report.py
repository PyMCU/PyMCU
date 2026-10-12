"""A `raise` with a message in a function the program never calls must not make the program
carry the unhandled-exception report.

pymcu.math has many small functions that raise ValueError("math domain error"). The report
(`__pymcu_exn_tail` and the printer behind it, with the UART string writer they call) costs 102
bytes on an AVR, and a program that wanted only math.sqrt paid for it because the raises of
asin, acosh and the rest had LOWERED, whether or not anything could reach them. The flag that
asks "did a message store lower?" is now narrowed to the stores of functions the program can
reach.
"""

import json
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

REPORT = {"__pymcu_exn_tail", "__pymcu_print_exn_msg"}


def functions(tmp_path: Path, call: str, py_parser: bool) -> set[str]:
    (tmp_path / "main.py").write_text(
        "import math\n"
        "from pymcu.chips.atmega328p import GPIOR0\n"
        "from pymcu.hal.uart_text import uart_write_str\n\n"
        f"GPIOR0.value = int({call})\n"
        'uart_write_str("hi")\n')
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.asm"),
         "--arch", "avr", "--target", "atmega328p", "--freq", "16000000",
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env)
    assert proc.returncode == 0, proc.stderr
    return {f["name"] for f in json.loads(mir.read_text())["functions"]}


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("call", ["math.sqrt(float(GPIOR0.value))", "math.exp(float(GPIOR0.value))",
                                  "math.fabs(float(GPIOR0.value))"])
def test_uncalled_math_functions_do_not_bring_the_report(tmp_path, py_parser, call):
    assert not (functions(tmp_path, call, py_parser) & REPORT)


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_reachable_message_still_does(tmp_path, py_parser):
    """The control: asin is called, and its domain error carries a message."""
    assert REPORT <= functions(tmp_path, "math.asin(float(GPIOR0.value) * 0.001)", py_parser)
