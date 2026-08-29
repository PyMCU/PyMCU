"""Both front ends must locate a diagnostic at the same character.

The differential axis compares GENERATED CODE, so it cannot see this by construction: a
program that fails to compile produces no image to compare. A diagnostic that points at the
callee under the hand-written parser and at column 1 under CPython's is a divergence nothing
else in the suite is looking for.

The bridge carries a position only for the node kinds both front ends locate identically (a
Name, a string literal). ast.BinOp is deliberately excluded, because CPython's col_offset for
it is the start of the whole expression while the hand-written parser stamps the OPERATOR;
carrying it would swap one divergence for another. `test_a_binary_argument_is_the_known_gap`
pins that as a known, deliberate difference rather than letting it pass unnoticed.
"""

import os
import subprocess
import re
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

HEADER = re.compile(r"^[^\s:]+:(\d+):(\d+): error:", re.MULTILINE)


def _where(src: Path, py_parser: bool):
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", "atmega328p",
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(src.parent)],
        capture_output=True, text=True, env=env,
    )
    m = HEADER.search(proc.stderr)
    assert m, f"expected a diagnostic, got:\n{proc.stderr}"
    return int(m.group(1)), int(m.group(2))


def _write(tmp_path: Path, body: str) -> Path:
    p = tmp_path / "main.py"
    # No dedent: the leading four spaces ARE the function body, and stripping them
    # silently turns every case into a module-level statement at a different column.
    p.write_text("from pymcu.types import uint8\ndef main() -> None:\n" + body)
    return p


@pytest.mark.parametrize("body", [
    "    s = hex(1, 2)\n",          # arity, points at the callee
    "    n = len()\n",
    "    v = pow(2)\n",
    "    a: uint8 = 1\n    n = len(a)\n",   # argument, points at a Name
    "    v = abs(\"hello\")\n",             # argument, points at a string
    "    v = ord(\"ab\")\n",
])
def test_both_front_ends_point_at_the_same_character(tmp_path, body):
    src = _write(tmp_path, body)

    assert _where(src, py_parser=False) == _where(src, py_parser=True)


def test_a_binary_argument_is_the_known_gap(tmp_path):
    # `hex(a + 1)` blames the argument, which is a BinOp. The hand-written parser stamps it at
    # the operator; the bridge does not carry a position for it at all, so CPython's side
    # reports no column. Deliberate, documented in POSITIONED_KINDS, and pinned here so that
    # closing it is a decision someone makes rather than a surprise.
    src = _write(tmp_path, "    a: uint8 = 5\n    s = hex(a + 1)\n")

    hand = _where(src, py_parser=False)
    cpython = _where(src, py_parser=True)

    assert hand[0] == cpython[0], "the LINE must agree even where the column does not"
    assert hand[1] > 1, "the hand-written parser locates the operator"
    assert cpython[1] == 1, "the bridge carries no position for a BinOp"

# --- the MESSAGE, not only the position (PyMCU#218) -----------------------------------
#
# Every divergence found before this one was a column: one side pointed, the other did not,
# which degrades a diagnostic. These are divergences in the TEXT, which breaks anything
# matching on it -- a user searching for the sentence, a doc quoting it, a test asserting it.


def _message(src: Path, py_parser: bool) -> str:
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", "atmega328p",
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(src.parent)],
        capture_output=True, text=True, env=env,
    )
    line = next((l for l in proc.stderr.splitlines() if "error:" in l), None)
    assert line, f"expected a diagnostic, got:\n{proc.stderr}"
    return line.split("error: ", 1)[1]


@pytest.mark.parametrize("body", [
    "    x: uint8 = 0xFFFFFFFFFF\n",                 # quoted as WRITTEN, not as a decimal
    "    x: uint8 = 99999999999\n",
    "    x: uint8 = 1\n    del x\n",                 # the written refusal, not a stub
])
def test_both_front_ends_say_the_same_sentence(tmp_path, body):
    src = _write(tmp_path, body)

    assert _message(src, py_parser=False) == _message(src, py_parser=True)


def test_an_oversized_literal_is_quoted_as_it_was_written(tmp_path):
    # CPython hands the bridge the VALUE, so `0xFFFFFFFFFF` would be reported as
    # 1099511627775, a number that appears nowhere in the program being read.
    src = _write(tmp_path, "    x: uint8 = 0xFFFFFFFFFF\n")

    for py in (False, True):
        assert "'0xFFFFFFFFFF'" in _message(src, py_parser=py)
        assert "1099511627775" not in _message(src, py_parser=py)


def test_an_oversized_literal_points_at_the_literal(tmp_path):
    #          1234567890123456
    # line 3: "    x: uint8 = 0xFFFFFFFFFF"  -- the literal starts at column 16
    #
    # It used to point at whatever token FOLLOWED the number, because Parser.Error() reports
    # Peek() and the literal had already been consumed: the caret landed on the `]` closing a
    # comprehension, or on the newline.
    src = _write(tmp_path, "    x: uint8 = 0xFFFFFFFFFF\n")

    assert _where(src, py_parser=False) == (3, 16)
    assert _where(src, py_parser=True) == (3, 16)


# --- a REFUSAL that must exist on both sides (PyMCU#221) ------------------------------
#
# The two blocks above compare a diagnostic both front ends already produce. This one is
# about a diagnostic that did not exist at all: rebinding the name of a module-level `def`
# was accepted, and the program compiled with the name meaning the function where it was
# called and the new value everywhere else. A refusal is exactly what the differential axis
# cannot check, so the parity has to be asserted here or nowhere.


def _write_with_helper(tmp_path: Path, body: str) -> Path:
    p = tmp_path / "main.py"
    p.write_text(
        "from pymcu.types import uint8\n"
        "def helper() -> uint8:\n"
        "    return 1\n"
        "def other() -> uint8:\n"
        "    return 2\n"
        + body
    )
    return p


@pytest.mark.parametrize("body", [
    # through `global`, to a value and to another function
    "def main() -> None:\n    global helper\n    helper = 5\n",
    "def main() -> None:\n    global helper\n    helper = other\n",
    # written at module level, which reaches the same binding without `global`
    "helper = 5\ndef main() -> None:\n    v: uint8 = 0\n",
])
def test_rebinding_a_function_name_is_refused_by_both_front_ends(tmp_path, body):
    src = _write_with_helper(tmp_path, body)

    hand = _message(src, py_parser=False)
    cpython = _message(src, py_parser=True)

    assert "'helper' is bound to a function" in hand
    assert hand == cpython


def test_a_local_of_the_same_name_is_not_the_module_binding(tmp_path):
    # No `global`, so this is an ordinary local shadowing the name, exactly as in CPython,
    # and it must keep compiling. The refusal above is about the MODULE-LEVEL binding; a
    # check that cannot tell the two apart would break every function with a local named
    # after some function elsewhere in the file.
    src = _write_with_helper(
        tmp_path, "def main() -> None:\n    helper = 5\n    v: uint8 = helper\n")

    for py in (False, True):
        env = dict(os.environ)
        if py:
            env["PYMCU_PY_PARSER"] = "1"
            env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
        else:
            env.pop("PYMCU_PY_PARSER", None)
        proc = subprocess.run(
            [str(PYMCUC), str(src), "--target", "atmega328p",
             "--emit-ir", os.devnull, "-o", os.devnull,
             "-I", str(STDLIB), "-I", str(src.parent)],
            capture_output=True, text=True, env=env,
        )
        assert proc.returncode == 0, f"py_parser={py} refused a plain local:\n{proc.stderr}"
