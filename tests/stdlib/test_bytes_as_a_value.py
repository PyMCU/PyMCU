"""A bytes or list object crossing a boundary as a value is refused, not crashed or guessed.

PyMCU lays a bytes object out as a fixed array: element storage under a name, with no handle
and no length travelling with it. So it has no single value for a comparison, a return or a
parameter default to carry, and every one of those positions was answering anyway.

Four of them crashed with `Unknown Expression type: ListExpr` (#195): a compiler class name,
for something the reader spelled `b"ab"`, and a phase name for a program that is simply not
supported. That is the reported half.

**Two more compiled**, and those are why this file asks about names as well as literals:

    a == b  over two bytes names   ->  a one-byte `jne` between the two array NAMES
    return x  where x is bytes     ->  the array came back as a scalar; the caller's y[0]
                                       lowered to a bit test on it

`test_a_comparison_of_two_names_does_not_answer_without_reading_them` is the one that pins the
first, and it is written as a differential rather than as a message check: before the fix,
`b"ab" == b"ab"` and `b"ab" == b"ax"` compiled to byte-identical IR, which is the proof that
the comparison never read either side.
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

HEAD = "from pymcu.hal.uart import UART as _stdout\nfrom pymcu.types import uint8\n\n\n"


def compile_(tmp_path: Path, source: str):
    """(ok, output, mir)."""
    (tmp_path / "main.py").write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    ok = "[BUILD_OK]" in proc.stdout
    return ok, out, json.loads(mir.read_text()) if ok and mir.exists() else None


def main_body(mir):
    """main()'s real instructions, with the debug records dropped."""
    body = next(f["body"] for f in mir["functions"] if f["name"].endswith("main"))
    return [json.dumps(i) for i in body if i["$t"] != "dbg"]


def prog(body: str, extra: str = ""):
    return HEAD + extra + "def main():\n    _stdout(115200)\n" + body


# --- the four positions that crashed -------------------------------------------------------

CRASHED = {
    "comparison of two literals": '    if b"ab" == b"ab":\n        print(77)\n',
    "comparison of a name and a literal": '    a = b"ab"\n    if a == b"ab":\n        print(77)\n',
    "membership": '    if b"a" in b"abc":\n        print(77)\n',
}


@pytest.mark.parametrize("name,body", CRASHED.items(), ids=list(CRASHED))
def test_a_literal_in_a_value_position_is_refused_by_name(tmp_path, name, body):
    """The discriminator: each of these answered `Unknown Expression type: ListExpr`."""
    ok, out, _ = compile_(tmp_path, prog(body))
    assert not ok, f"{name} compiled; this test has stopped discriminating"
    assert "Unknown Expression type" not in out, out
    assert "bytes or list" in out, out


@pytest.mark.parametrize("default", ["list = []", 'bytes = b"ab"'], ids=["list", "bytes"])
def test_a_parameter_default_is_refused_by_name(tmp_path, default):
    """A list or bytes literal as a parameter default produces the same crash, which is what
    showed the defect is about the position rather than about bytes."""
    ok, out, _ = compile_(
        tmp_path,
        prog("    print(f(77))\n",
             extra=f"def f(a: uint8, buf: {default}) -> uint8:\n    return a\n\n\n"))
    assert not ok, "the literal default compiled"
    assert "Unknown Expression type" not in out, out
    assert "parameter default" in out, out


def test_a_return_of_a_literal_is_refused_by_name(tmp_path):
    ok, out, _ = compile_(
        tmp_path,
        prog("    x = f()\n    print(x[0])\n",
             extra='def f() -> bytes:\n    return b"ab"\n\n\n'))
    assert not ok, "the returned literal compiled"
    assert "Unknown Expression type" not in out, out
    assert "cannot be returned" in out, out


# --- the two that did not crash, which is worse ---------------------------------------------

def compare(first, second):
    return (f"    a = {first}\n    b = {second}\n    if a == b:\n"
            "        print(77)\n    else:\n        print(11)\n")


# Both array kinds, because the defect is not about bytes: it is about a name that stands for
# fixed array storage, and a list literal bound to a name has exactly the same shape. Measured
# on the unpatched compiler, `[1, 2] == [1, 2]` and `[1, 2] == [1, 9]` were identical too.
ARRAY_PAIRS = {
    "bytes": ('b"ab"', 'b"ab"', 'b"ax"'),
    "list": ("[1, 2]", "[1, 2]", "[1, 9]"),
}


@pytest.mark.parametrize("kind", list(ARRAY_PAIRS), ids=list(ARRAY_PAIRS))
def test_a_comparison_of_two_names_does_not_answer_without_reading_them(tmp_path, kind):
    """The discriminator, written as a differential rather than as a message check.

    Before the fix both programs compiled AND produced byte-identical IR, which is the proof
    that the comparison read neither operand: one of the two answers it gave had to be wrong.
    CPython says True for the first and False for the second.

    Asserting only "it is refused" would pass on a fix that refused for some unrelated reason,
    so this asserts the pair and the reason together.
    """
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    first, same, other = ARRAY_PAIRS[kind]
    ok_equal, out_equal, mir_equal = compile_(tmp_path / "a", prog(compare(first, same)))
    ok_differ, out_differ, mir_differ = compile_(tmp_path / "b", prog(compare(first, other)))

    if ok_equal and ok_differ:
        assert main_body(mir_equal) != main_body(mir_differ), (
            "both compiled and to the same instructions: the comparison is not reading "
            "its operands, which is the defect this test exists for")
        pytest.fail("both compiled; a bytes comparison is not implemented, so it must refuse")

    assert not ok_equal and not ok_differ, (ok_equal, ok_differ)
    for out in (out_equal, out_differ):
        assert "cannot be compared" in out, out


def test_a_return_through_a_name_is_refused_like_the_literal(tmp_path):
    """This compiled and returned the array as a scalar, after which the caller's `y[0]`
    lowered to a `bchk` -- a bit test -- instead of an array load. Where the literal form
    crashed, this one gave an answer."""
    ok, out, _ = compile_(
        tmp_path,
        prog("    y = f()\n    print(y[0])\n",
             extra='def f() -> bytes:\n    x = b"ab"\n    return x\n\n\n'))
    assert not ok, "returning a bytes name compiled"
    assert "cannot be returned" in out, out


# --- the positions that work, and must go on working ----------------------------------------

WORKS = {
    "bind and index": '    a = b"ab"\n    print(a[0])\n',
    "len": '    a = b"abc"\n    print(len(a))\n',
    "iterate": '    a = b"\\x01\\x02"\n    n: uint8 = 0\n    for x in a:\n        n = n + x\n    print(n)\n',
    "slice": '    a = b"\\x01\\x02\\x03\\x04"\n    b = a[1:3]\n    print(b[0])\n',
    "element comparison": '    a = b"ab"\n    if a[0] == 0x61:\n        print(77)\n',
    "scalar in a list literal": '    n: uint8 = 2\n    if n in [1, 2, 3]:\n        print(77)\n',
    "scalar in a bytes name": '    a = b"abc"\n    n: uint8 = 0x61\n    if n in a:\n        print(77)\n',
    "string literal comparison": '    if "ab" == "ab":\n        print(77)\n',
    "string name comparison": '    a = "ab"\n    b = "ab"\n    if a == b:\n        print(77)\n',
    "one-character string name comparison":
        '    a = "a"\n    b = "a"\n    if a == b:\n        print(77)\n',
    "scalar comparison": '    n: uint8 = 2\n    if n == 2:\n        print(77)\n',
}

# The same four positions the refusals cover, spelled with a STRING instead of an array. A
# string interns, so its id stands for its text and a comparison of two ids is a comparison of
# two texts: measured on the unpatched compiler, `"ab" == "ab"` and `"ab" == "ax"` compile to
# DIFFERENT instructions, which is what says the string path reads its operands and the array
# path did not. These pin that the refusal is about array storage and not about literals.
STRING_IN_THE_SAME_POSITIONS = {
    "return a string literal": ("    x = f()\n    print(x)\n",
                                'def f() -> str:\n    return "ab"\n\n\n'),
    "return a string name": ("    x = f()\n    print(x)\n",
                             'def f() -> str:\n    s = "ab"\n    return s\n\n\n'),
    "a string parameter default": ("    print(f(77))\n",
                                   'def f(a: uint8, s: str = "x") -> uint8:\n    return a\n\n\n'),
    "an integer parameter default": ("    print(f(77))\n",
                                     "def f(a: uint8, n: uint8 = 3) -> uint8:\n    return a\n\n\n"),
}


@pytest.mark.parametrize("name,pair", STRING_IN_THE_SAME_POSITIONS.items(),
                         ids=list(STRING_IN_THE_SAME_POSITIONS))
def test_a_string_in_the_same_positions_is_untouched(tmp_path, name, pair):
    body, extra = pair
    ok, out, _ = compile_(tmp_path, prog(body, extra=extra))
    assert ok, out


@pytest.mark.parametrize("name,body", WORKS.items(), ids=list(WORKS))
def test_the_positions_that_work_are_untouched(tmp_path, name, body):
    """The invariants, and the last four are the ones that pay for the refusals above: the
    check asks whether an OPERAND is a sequence object, so a string comparison, a scalar
    comparison and both working forms of `in` must be unaffected by it."""
    ok, out, _ = compile_(tmp_path, prog(body))
    assert ok, out
