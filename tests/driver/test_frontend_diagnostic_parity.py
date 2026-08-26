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
