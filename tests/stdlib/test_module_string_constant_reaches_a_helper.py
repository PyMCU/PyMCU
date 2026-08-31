# PyMCU -- a module-level string constant written WITHOUT an annotation was visible to `main`,
# to module level and to an @inline helper, and invisible to a plain function.
#
#     MSG = "no radio"
#
#     def f() -> None:
#         raise CompileError(MSG)      <- refused: "'MSG' is not a string constant"
#
# Rename `f` to `main` and it compiles. Nothing else changes. #239.
#
# WHY IT HAPPENED, which is what the test pins: ScanGlobals had two registration sites for a
# module-level string and BOTH required the annotation, so the bare form was never in
# strConstantVariables at scan time. It arrived only while the module-level statement was
# LOWERED, and a plain function is lowered before that. An @inline helper is expanded during
# main's lowering, which is why it was the boundary rather than "main versus helper".
#
# WHAT DISCRIMINATES: bare_in_a_plain_helper. Against the unfixed compiler it is the only row
# that is refused, and every other row here already passed.
#
# WHAT IS INVARIANT, and here on purpose: the last case. The fix registers a module-level
# string LITERAL and nothing else, so a name that genuinely is not a compile-time constant has
# to stay refused. Without that row a fix that made every name resolve would look correct.
import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

REFUSAL = "is not a string constant known at compile time"


def build(tmp_path: Path, source: str, py_parser: bool = False) -> str:
    (tmp_path / "main.py").write_text(source)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB)],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
    )
    return proc.stdout + proc.stderr


HEADER = "from pymcu.exceptions import CompileError\nfrom pymcu.types import inline, uint8\n"
TAIL = "\n\ndef main():\n    f()\n    while True:\n        pass\n"

BARE_IN_A_PLAIN_HELPER = (
    HEADER + '\nMSG = "no radio"\n\n\ndef f() -> None:\n    raise CompileError(MSG)\n' + TAIL)

BARE_IN_AN_INLINE_HELPER = (
    HEADER + '\nMSG = "no radio"\n\n\n@inline\ndef f() -> None:\n'
    "    raise CompileError(MSG)\n" + TAIL)

ANNOTATED_IN_A_PLAIN_HELPER = (
    HEADER + '\nMSG: str = "no radio"\n\n\ndef f() -> None:\n'
    "    raise CompileError(MSG)\n" + TAIL)

BARE_IN_MAIN = (
    HEADER + '\nMSG = "no radio"\n\n\ndef main():\n    raise CompileError(MSG)\n')

BARE_AT_MODULE_LEVEL = (
    HEADER + '\nMSG = "no radio"\n\nraise CompileError(MSG)\n')


@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
@pytest.mark.parametrize("source,label", [
    pytest.param(BARE_IN_A_PLAIN_HELPER, "bare in a plain helper", id="bare-plain-helper"),
    pytest.param(BARE_IN_AN_INLINE_HELPER, "bare in an @inline helper", id="bare-inline-helper"),
    pytest.param(ANNOTATED_IN_A_PLAIN_HELPER, "annotated in a plain helper", id="annotated-helper"),
    pytest.param(BARE_IN_MAIN, "bare in main", id="bare-main"),
    pytest.param(BARE_AT_MODULE_LEVEL, "bare at module level", id="bare-module-level"),
])
def test_the_constant_resolves_wherever_the_raise_is(tmp_path, source, label, py_parser):
    # A resolved constant makes the program's own CompileError fire with its text, which is
    # what "works" looks like here: the build never reaches Flash either way.
    out = build(tmp_path, source, py_parser)
    assert REFUSAL not in out, f"{label}: the constant did not resolve\n{out}"
    assert "no radio" in out, f"{label}: the message never came out\n{out}"


@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
def test_a_name_that_is_not_a_compile_time_string_is_still_refused(tmp_path, py_parser):
    # The value comes from a register, so no scan and no lowering can know it. Registering
    # module-level literals must not turn this into a resolvable name.
    out = build(tmp_path, HEADER + (
        "from pymcu.chips.atmega328p import GPIOR0\n\n\n"
        "def main():\n"
        "    s: uint8 = GPIOR0.value\n"
        "    raise CompileError(s)\n"), py_parser)

    assert REFUSAL in out, f"a runtime value was accepted as a message\n{out}"
