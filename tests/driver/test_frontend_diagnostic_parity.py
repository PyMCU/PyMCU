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
    """(line, column, underline) -- where the diagnostic points and how much it marks.

    The underline is part of the answer, not decoration. Comparing only line and column let a
    whole class through: the hand-written parser marks a SUB-TOKEN of a node (the `raise`
    keyword, the unary operator, a comprehension's opening bracket) while the bridge carried
    CPython's span for the WHOLE node, so both front ends named the same character and then
    underlined 5 against 19. Every case in this file checks all three for that reason.
    """
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
    caret = next((l.strip() for l in proc.stderr.splitlines() if l.strip().startswith("^")), "")
    return int(m.group(1)), int(m.group(2)), len(caret)


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


# --- the UNDERLINE, not only the character it starts at -------------------------------
#
# These four kinds agreed on the column and disagreed on how much they marked, which is why
# the block above did not catch them: the hand-written parser stamps a sub-token of the node
# and CPython's span is the whole node.
#
#     raise ValueError(x)      keyword 5   against the statement 19
#     a ** -1                  operator 1  against the operand 2
#     [i for i in range(5)]    bracket 1   against the comprehension 21
#     def f() -> uint8:        keyword 3   against nothing at all, since a function spans
#                                          lines and a span across lines carries no length
#
# The last row is the one to keep in mind when reading position_of: dropping the length on a
# multi-line node is right for a SPAN and wrong for a KEYWORD, whose length is known without
# looking at the span.


def _program(tmp_path: Path, source: str) -> Path:
    p = tmp_path / "main.py"
    p.write_text(source)
    return p


@pytest.mark.parametrize("source", [
    # Raise, on one line and across two: the second is where the span-based length vanished
    # rather than being merely too long.
    "from pymcu.types import uint8\ndef main() -> None:\n    x: uint8 = 1\n    raise ValueError(x)\n",
    "from pymcu.types import uint8\ndef main() -> None:\n    x: uint8 = 1\n    raise ValueError(\n        x)\n",
    # Unary: the operator, not the operand it applies to.
    "from pymcu.types import uint8\ndef main() -> None:\n    a: uint8 = 2\n    b: uint8 = a ** -1\n",
    # ListComp: the opening bracket, not the comprehension.
    "from pymcu.types import uint8\ndef main() -> None:\n    xs: uint8[3] = [i for i in range(5)]\n",
    # Function, at module level and as a method. The method reaches the diagnostic through a
    # stand-in FunctionDef that RegisterOutlinedMethod builds, so it is a second path to the
    # same stamp and not a duplicate of the case above it.
    #
    # That stand-in only carries a position since the Scan.cs fix, and this test cannot see
    # that fix: the stand-in is built in the IR generator, DOWNSTREAM of both front ends, so
    # before it the two agreed on the same wrong answer and this case passed while covering
    # nothing. A parity check is blind to any defect the two front ends share, in the same way
    # the differential axis is blind to a refusal.
    "from pymcu.types import uint8\ndef f() -> uint8:\n    x: uint8 = 1\ndef main() -> None:\n    b: uint8 = f()\n",
    "from pymcu.types import uint8\nclass Box:\n    def __init__(self) -> None:\n        self.n: uint8 = 0\n"
    "    def get(self) -> uint8:\n        x: uint8 = self.n\ndef main() -> None:\n    b = Box()\n    v: uint8 = b.get()\n",
])
def test_both_front_ends_underline_the_same_amount(tmp_path, source):
    src = _program(tmp_path, source)

    assert _where(src, py_parser=False) == _where(src, py_parser=True)


@pytest.mark.parametrize("source,expected", [
    # `async def` marks BOTH words. It was a POSITION divergence before this: CPython puts an
    # AsyncFunctionDef at its `async` and the hand-written parser stamped the `def` six
    # characters later, so the parser is the side that moved. What is marked is the INTRODUCER
    # of the construct, and a coroutine's introducer is two words -- marking only the second
    # starts the underline in the middle of a compound keyword.
    ("from pymcu.types import uint8\nasync def get() -> uint8:\n    return 1\n"
     "def main() -> None:\n    v: uint8 = get()\n", (2, 1, 9)),
    # The same thing spaced out, which is valid Python and compiles here. This is the case a
    # constant 9 gets wrong: it would underline `async   d`, one character into `def` and
    # stopping short of it. The length is measured from the two words, on both sides.
    ("from pymcu.types import uint8\nasync   def get() -> uint8:\n    return 1\n"
     "def main() -> None:\n    v: uint8 = get()\n", (2, 1, 11)),
    # As a method, which is refused for a different reason and through a different site.
    ("from pymcu.types import uint8\nclass Box:\n    def __init__(self) -> None:\n"
     "        self.n: uint8 = 0\n    async def get(self) -> uint8:\n        return self.n\n"
     "def main() -> None:\n    b = Box()\n    v: uint8 = b.get()\n", (5, 5, 9)),
])
def test_an_async_def_marks_both_words_on_both_front_ends(tmp_path, source, expected):
    src = _program(tmp_path, source)

    assert _where(src, py_parser=False) == expected
    assert _where(src, py_parser=True) == expected

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

    assert _where(src, py_parser=False) == (3, 16, 12)
    assert _where(src, py_parser=True) == (3, 16, 12)


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


# --- assert: the refusal, and the warning that replaces silence (PyMCU#225) -----------
#
# `assert` fired only for the literal `0`; `assert False` and `assert 1 == 2` lowered to
# nothing. Both halves of the fix are checked here rather than only in the unit suite: the
# refusal because a refusal is invisible to the differential axis, and the warning because
# reading it from a subprocess beats swapping Console.Error under a shared test host.


def _assert_src(tmp_path: Path, body: str) -> Path:
    p = tmp_path / "main.py"
    p.write_text("from pymcu.types import uint8\ndef main() -> None:\n" + body)
    return p


def _stderr(src: Path, py_parser: bool) -> tuple[int, str]:
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
    return proc.returncode, proc.stderr


@pytest.mark.parametrize("body", [
    "    assert 0\n",
    "    assert False\n",
    "    assert 1 == 2\n",
    "    assert not True\n",
    "    assert 0 and 1\n",
])
def test_a_false_assert_is_refused_by_both_front_ends(tmp_path, body):
    src = _assert_src(tmp_path, body)

    hand = _message(src, py_parser=False)
    cpython = _message(src, py_parser=True)

    assert "AssertionError" in hand
    assert hand == cpython


# The condition the compiler cannot resolve keeps compiling to nothing, which is what
# `python -O` does. What changed is that it says so: silence is what made a dropped
# assertion indistinguishable from a checked one.
@pytest.mark.parametrize("body", [
    "    x: uint8 = 1\n    assert x == 2\n",
    "    x: uint8 = 1\n    assert x\n",
])
def test_an_unresolvable_assert_warns_instead_of_going_quiet(tmp_path, body):
    src = _assert_src(tmp_path, body)

    for py in (False, True):
        rc, err = _stderr(src, py_parser=py)
        assert rc == 0, f"py_parser={py} refused a run-time assert:\n{err}"
        assert "assert on line" in err and "is not checked" in err, \
            f"py_parser={py} compiled it out in silence:\n{err}"


@pytest.mark.parametrize("body", ["    assert True\n", "    assert 2 == 2\n"])
def test_a_true_assert_is_neither_refused_nor_warned_about(tmp_path, body):
    # A warning on an assertion that IS resolved, and resolved true, would be noise on every
    # correct program.
    for py in (False, True):
        rc, err = _stderr(_assert_src(tmp_path, body), py_parser=py)
        assert rc == 0, err
        assert "is not checked" not in err, f"py_parser={py} warned about a true assert:\n{err}"
