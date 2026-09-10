"""`if x: p.on()` compiled on one front end and was refused by the other (#250).

Ordinary Python, no runtime cost, and the CPython bridge accepted it and lowered it correctly
all along. The hand-written parser answered "Expected newline", which names the token it wanted
rather than the construct it met, in four shapes: `if`, `while`, `for` and `def`.

IT WAS NEVER A LANGUAGE DECISION. Nothing in limitations.md, roadmap.md or LANGUAGE_ROADMAP.md
refuses a one-line suite, and `case X: stmt` ALREADY accepted one, with its own handling in the
match parser. So the form was present in the grammar, in exactly one branch, and every other
clause disagreed with it. All clauses now share `ParseSuite`, which is also how `case` gained
the `;` list it did not have.

THE BRIDGE IS THE ORACLE, NOT A SECOND IMPLEMENTATION. It is CPython's own grammar, so its
answers are Python's. The surface below was measured against it first and matched, including
the case that must still be REFUSED: a compound statement cannot open a one-line body, which is
CPython's rule too.

The equivalence tests are the point of the exercise. A parser change that accepted the syntax
and dropped a statement, or built a different tree, would pass every "does it compile" check in
this file; `test_one_line_and_indented_emit_identical_ir` is what would fail.
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

# A runtime condition on purpose. With a constant the optimizer folds the branch away and the
# one-line and indented forms match trivially, which would make the equivalence test vacuous.
HEADER = (
    "from pymcu.hal.gpio import Pin\n"
    "from pymcu.hal.adc import AnalogPin\n\n"
    'p = Pin("PB5", Pin.OUT)\n'
    "a = AnalogPin(0)\n\n\n"
    "def main() -> None:\n"
    "    v: uint16 = a.read()\n"
)


def compile_(tmp_path: Path, source: str, py_parser: bool, mir: Path | None = None):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir) if mir else "/dev/null"],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


ACCEPTED = [
    pytest.param("    if v > 100: p.value(1)\n", id="if"),
    pytest.param("    while v > 100: v = 0\n", id="while"),
    pytest.param("    for i in range(3): p.value(1)\n", id="for"),
    pytest.param("    if v > 100: v = 1; v = 2\n", id="semicolon-list"),
    pytest.param("    while v > 100: v = 0;\n", id="trailing-semicolon"),
    pytest.param("    if v > 100: v = 1\n    else: v = 2\n", id="one-line-if-and-else"),
    pytest.param("    if v > 100: v = 1\n    else:\n        v = 2\n", id="one-line-if-block-else"),
    pytest.param("    if v > 100: v = 1\n    elif v > 50: v = 2\n", id="one-line-elif"),
]


@pytest.mark.parametrize("suite", ACCEPTED)
@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_one_line_suite_is_accepted_by_both_front_ends(tmp_path, suite, py_parser):
    ok, out = compile_(tmp_path, HEADER + suite, py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_one_line_def_body_is_accepted(tmp_path, py_parser):
    """The fourth shape, which reaches a different parser path than the three statements."""
    ok, out = compile_(
        tmp_path,
        "def f() -> uint8: return 1\n\n\n"
        "def main() -> None:\n"
        "    y: uint8 = f()\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_one_line_class_body_is_accepted(tmp_path, py_parser):
    ok, out = compile_(
        tmp_path,
        "class C: pass\n\n\n"
        "def main() -> None:\n"
        "    pass\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_one_line_try_and_except_are_accepted(tmp_path, py_parser):
    ok, out = compile_(
        tmp_path,
        "def f(x: uint8) -> uint8:\n"
        "    if x > 3: raise ValueError\n"
        "    return x\n\n\n"
        "def main() -> None:\n"
        "    try: v: uint8 = f(9)\n"
        "    except ValueError: pass\n",
        py_parser,
    )
    assert ok, out


# --- what must STILL be refused ---------------------------------------------------------------

@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_compound_statement_cannot_open_a_one_line_body(tmp_path, py_parser):
    """CPython refuses this too, so accepting it would be a divergence in the other direction.

    The verdict is asserted rather than the text: this front end names the construct while the
    bridge reports CPython's own "invalid syntax", raised before the bridge ever sees the file.
    """
    ok, out = compile_(tmp_path, HEADER + "    if v > 100: if v > 200: v = 1\n", py_parser)
    assert not ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_truncated_header_is_still_refused(tmp_path, py_parser):
    """The control against the cheap version of this fix.

    A `:` with nothing after it must still be an error about a missing block, not a message
    about one-line bodies. `StartsInlineSuite` excludes Dedent and EndOfFile for this reason.
    """
    ok, out = compile_(tmp_path, HEADER + "    if v > 100:\n", py_parser)
    assert not ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_indented_suite_still_works(tmp_path, py_parser):
    """The other control: the form that always worked must be untouched."""
    ok, out = compile_(
        tmp_path,
        HEADER + "    if v > 100:\n        p.value(1)\n        v = 0\n",
        py_parser,
    )
    assert ok, out


# --- the equivalence that makes the rest mean something ----------------------------------------

def _main_body(mir: Path):
    d = json.loads(mir.read_text())
    fn = [f for f in d["functions"] if f["name"] == "main"][0]
    # `dbg` carries the source TEXT of each line, which necessarily differs between the two
    # spellings. Everything else must match exactly.
    return json.dumps([i for i in fn["body"] if i["$t"] != "dbg"], sort_keys=True)


@pytest.mark.parametrize("one,many,label", [
    ("    if v > 100: p.value(1)\n",
     "    if v > 100:\n        p.value(1)\n", "if"),
    ("    while v > 100: v = 0\n",
     "    while v > 100:\n        v = 0\n", "while"),
    ("    for i in range(3): p.value(1)\n",
     "    for i in range(3):\n        p.value(1)\n", "for"),
])
@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_one_line_and_indented_emit_identical_ir(tmp_path, one, many, label, py_parser):
    """Same program, two spellings, one lowering.

    This is what a "does it compile" test cannot see: a suite parser that accepted the syntax and
    then dropped the statement, or attached it to the wrong branch, would still build.
    """
    a, b = tmp_path / "a.mir", tmp_path / "b.mir"
    ok1, out1 = compile_(tmp_path, HEADER + one, py_parser, a)
    assert ok1, out1
    ok2, out2 = compile_(tmp_path, HEADER + many, py_parser, b)
    assert ok2, out2
    assert _main_body(a) == _main_body(b), f"{label}: one-line and indented lower differently"


@pytest.mark.parametrize("suite", ACCEPTED)
def test_the_two_front_ends_lower_a_one_line_suite_identically(tmp_path, suite):
    """The divergence this issue is about, closed at the IR rather than at the verdict.

    Both front ends accepting it is necessary and not sufficient: they must also agree on what
    it means.
    """
    a, b = tmp_path / "cs.mir", tmp_path / "py.mir"
    ok1, out1 = compile_(tmp_path, HEADER + suite, False, a)
    assert ok1, out1
    ok2, out2 = compile_(tmp_path, HEADER + suite, True, b)
    assert ok2, out2
    assert _main_body(a) == _main_body(b), "the two front ends lower this differently"
