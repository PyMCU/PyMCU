"""A keyword argument to an overloaded @inline is refused, at the call, by name.

Overload selection is POSITIONAL. The suffix that picks a candidate is built from the
positional arguments only, and the arity used by the fallback counts only those, so a call
written with keywords presents as a call with no arguments. What happened next depended on the
overload set, and none of the three said so:

    no zero-argument candidate   nothing matched, the UNMANGLED name reached the linker:
                                 `avr-ld: undefined reference to 'w'`, from a tool the reader
                                 did not invoke, naming no file and no line of their program
    a zero-argument candidate    that one was selected and then refused the keyword, so the
                                 message was about a candidate the caller did not mean
    a method                     `missing argument 'self' in call to 'A_w'`, which names a
                                 mangled symbol the program never contained and blames the
                                 reader for omitting an argument they cannot write

Issue #232, and it predates #226: a compiler built from `1b2a1882^` produces the same IR.

WHAT DISCRIMINATES: the three refusal tests. Against the unfixed compiler the first produces no
located diagnostic at all, and the other two produce a located diagnostic that is wrong.

WHAT IS INVARIANT, and here on purpose: the positional spellings, a keyword argument to a
function that is NOT overloaded, and a keyword argument to a builtin. The refusal is written
wide, every keyword argument to an overloaded name, so what keeps it from being too wide is
the second of those: keyword arguments are ordinary and must keep working everywhere else.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):(\d+):")

HEAD = ("from pymcu.types import uint8, int32, inline\n"
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n")
TAIL = ("    GPIOR1.value = uint8(r & 0xFF)\n"
        "    while True:\n"
        "        pass\n")


def build(tmp_path: Path, source: str, py_parser: bool = False):
    (tmp_path / "main.py").write_text(source)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--emit-ir", str(tmp_path / "f.mir"),
         "--target", "atmega328p", "--freq", "16000000", "-I", str(STDLIB)],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
    )
    return proc.stdout + proc.stderr


TWO_FUNCTIONS = (HEAD + "\n\n@inline\ndef w(x: uint8) -> int32:\n    return x + 7\n"
                 "\n\n@inline\ndef w(x: int32) -> int32:\n    return x + 100\n")

WITH_ZERO_ARG = (HEAD + "\n\n@inline\ndef w() -> int32:\n    return 42\n"
                 "\n\n@inline\ndef w(x: uint8) -> int32:\n    return x + 7\n")

TWO_METHODS = (HEAD + "\n\nclass A:\n"
               "    @inline\n    def __init__(self):\n        self.k = 0\n\n"
               "    @inline\n    def w(self, x: uint8) -> int32:\n        return x + 7\n\n"
               "    @inline\n    def w(self, x: int32) -> int32:\n        return x + 100\n")


def call(defs: str, expr: str, receiver: str = "") -> str:
    return (defs + "\n\ndef main():\n" + receiver +
            "    s: uint8 = GPIOR0.value\n"
            f"    r: int32 = {expr}\n" + TAIL)


# --- what discriminates -------------------------------------------------------

@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
@pytest.mark.parametrize("defs,expr,receiver", [
    (TWO_FUNCTIONS, "w(x=s)", ""),
    (WITH_ZERO_ARG, "w(x=s)", ""),
    (TWO_METHODS, "a.w(x=s)", "    a = A()\n"),
], ids=["function", "zero-arg-candidate", "method"])
def test_a_keyword_argument_to_an_overload_is_refused_at_the_call(
        tmp_path, defs, expr, receiver, py_parser):
    out = build(tmp_path, call(defs, expr, receiver), py_parser)

    m = LOCATION.search(out)
    assert m, f"no located diagnostic; the unfixed compiler reaches the linker:\n{out}"
    name, line, col = Path(m.group(1)).name, int(m.group(2)), int(m.group(3))
    assert name == "main.py", f"the call to change is in main.py, got {name}"

    text = (tmp_path / "main.py").read_text().splitlines()[line - 1]
    assert expr in text, f"main.py:{line} is {text!r}, which is not the call"
    assert col <= len(text), f"caret at column {col} of a {len(text)}-character line"

    assert "'w'" in out, "the refusal has to name the function the reader wrote"
    assert "A_w" not in out, "a mangled symbol is not a name the program contains"
    assert "positional" in out.lower(), "the refusal has to say why a keyword cannot select"


# --- invariants ---------------------------------------------------------------

@pytest.mark.parametrize("defs,expr,receiver", [
    (TWO_FUNCTIONS, "w(s)", ""),
    (TWO_METHODS, "a.w(s)", "    a = A()\n"),
], ids=["function", "method"])
def test_the_positional_spelling_still_selects(tmp_path, defs, expr, receiver):
    out = build(tmp_path, call(defs, expr, receiver))
    assert "error" not in out.lower(), out


def test_a_keyword_argument_to_a_function_that_is_not_overloaded_still_works(tmp_path):
    """The control that keeps the refusal from being too wide.

    It is written to catch every keyword argument to an overloaded name, so what says it is not
    catching keyword arguments in general is that an ordinary one still compiles.
    """
    solo = HEAD + "\n\n@inline\ndef solo(x: uint8) -> int32:\n    return x + 55\n"
    out = build(tmp_path, call(solo, "solo(x=s)"))
    assert "error" not in out.lower(), out


def test_a_keyword_argument_to_a_builtin_keeps_its_own_answer(tmp_path):
    """PyMCU#226's message, which is the neighbouring behaviour and must not move."""
    out = build(tmp_path, HEAD + "\n\ndef main():\n"
                "    r: int32 = abs(x=1)\n" + TAIL)
    assert "keyword argument" in out, out
    assert "positional" not in out.lower(), \
        "a builtin gets #226's answer, not the overload refusal"
