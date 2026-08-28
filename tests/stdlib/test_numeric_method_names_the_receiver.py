"""A method call on an int or float local names the receiver, not a manufactured symbol.

`x = 5` then `x.bit_length()` answered "call to undefined function 'x_bit_length'": a name the
program never wrote, the wrong category, and two suggestions that are both dead ends -- the
spelling is right and no import adds a method to an int.

Fifth internal identifier to reach user text the same way, after math_floor (#174),
KeywordArgExpr (#190), machine_SPI___init__ (#194) and s_add (#197). The habit behind all five:
error construction reaches for the callee's internal identity when the user's spelling is
available at the same point.

The three receivers that already answered properly are the specification, not this file's
invention: str, bytearray and list, joined by dict and set in #197. They are asserted here too,
so a later change that regresses one of them fails beside the two being fixed.

Everything the message offers is checked to compile further down. That is not ceremony: the
generic builtin list names hex(), bin() and pow(), and all three refuse anything but a
compile-time constant, while round() does not exist at all. A refusal that recommends something
is making a claim.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PRINT = "from pymcu.hal.console import print\n"


def compile_(tmp_path: Path, source: str, py_parser: bool = False):
    """(ok, combined output). The Python front end is a separate run of the same binary.

    `--emit-ir` rather than `-o`: pymcuc cannot run the AVR backend itself, so `-o` always
    exits non-zero and an exit-code check would call every program a failure -- including the
    ones this file compiles to prove the advice works.
    """
    (tmp_path / "main.py").write_text(PRINT + source)
    env = {"PYMCU_PY_PARSER": "1"} if py_parser else None
    mir = tmp_path / "out.mir"
    if mir.exists():
        mir.unlink()
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--emit-ir", str(mir),
         "--target", "atmega328p", "-I", str(STDLIB)],
        capture_output=True, text=True,
        env={**__import__("os").environ, **(env or {})})
    return mir.exists(), proc.stdout + proc.stderr


def _diagnostic(output: str) -> list[str]:
    """The error lines only. The full transcript carries per-phase timings in milliseconds,
    which differ between two runs of the SAME front end, let alone two different ones."""
    return [ln for ln in output.splitlines() if "error:" in ln]

INT_METHOD = "def main():\n    x = 5\n    x.bit_length()\n"
FLOAT_METHOD = "def main():\n    f: float = 1.5\n    f.is_integer()\n"


# --- the two being fixed ------------------------------------------------------------------

def test_an_int_method_names_the_receiver_not_a_flattened_symbol(tmp_path):
    ok, out = compile_(tmp_path, INT_METHOD)
    assert not ok, out
    assert "x_bit_length" not in out, \
        "the flattened name is a symbol the program never wrote"
    assert "'x' is an integer" in out, out
    assert "'bit_length()' is not available" in out, out


def test_a_float_method_names_the_receiver_not_a_flattened_symbol(tmp_path):
    ok, out = compile_(tmp_path, FLOAT_METHOD)
    assert not ok, out
    assert "f_is_integer" not in out, out
    assert "'f' is a float" in out, out


def test_neither_is_reported_as_a_missing_import(tmp_path):
    """The discriminator for the CATEGORY, not the wording: no import adds a method to an int,
    so 'typo, or a missing import?' sends the reader to look for something that cannot exist."""
    for src in (INT_METHOD, FLOAT_METHOD):
        _, out = compile_(tmp_path, src)
        assert "missing import" not in out, out
        assert "undefined function" not in out, out


# --- both front ends ----------------------------------------------------------------------

@pytest.mark.parametrize("src", [INT_METHOD, FLOAT_METHOD], ids=["int", "float"])
def test_both_front_ends_answer_the_same(tmp_path, src):
    """A program that fails to compile produces no image, so the differential axis cannot see
    a divergence here. Four have been found by running both."""
    _, cs = compile_(tmp_path, src)
    _, py = compile_(tmp_path, src, py_parser=True)
    assert _diagnostic(cs) == _diagnostic(py), f"C#:\n{cs}\npython:\n{py}"


# --- the neighbours this matches, asserted so a regression in one is visible here -----------

@pytest.mark.parametrize("src,needle", [
    ('def main():\n    t = "ab"\n    t.upper()\n', "not supported on a string"),
    ('def main():\n    d = {1: 2}\n    d.frob()\n', "'d' is a compile-time lookup table"),
    ('def main():\n    s = {1, 2}\n    s.frob()\n', "'s' is a compile-time set literal"),
], ids=["str", "dict", "set"])
def test_the_receivers_that_already_answered_properly_still_do(tmp_path, src, needle):
    ok, out = compile_(tmp_path, src)
    assert not ok, out
    assert needle in out, out


# --- the advice, executed -------------------------------------------------------------------

INT_ADVICE = (
    "from pymcu.types import uint16, int8\n"
    "from pymcu.chips.atmega328p import GPIOR0\n"
    "def main():\n"
    "    x = GPIOR0.value\n"
    "    print(abs(x), min(x, 3), max(x, 9))\n"
    "    q, r = divmod(x, 2)\n"
    "    print(q, r, uint16(x), int8(x))\n"
    "    print(x + 1, x * 2, x >> 1, x & 3)\n"
)

FLOAT_ADVICE = (
    "def main():\n"
    "    f: float = 1.5\n"
    "    print(abs(f), int(f))\n"
    "    if f > 1.0:\n"
    "        print(f + 1.0)\n"
)


@pytest.mark.parametrize("src", [INT_ADVICE, FLOAT_ADVICE], ids=["int", "float"])
def test_everything_the_message_offers_compiles(tmp_path, src):
    """The condition that caught hex(), bin(), pow() and round() before they reached the text."""
    ok, out = compile_(tmp_path, src)
    assert ok, out
