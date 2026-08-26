"""max(xs) and min(xs) over a sequence whose length is known.

`max(xs)` is the ordinary Python spelling with a list in hand, and it was refused by a message
about the number of arguments:

    error: CompileError: max() expects at least two arguments

The call has the argument count Python asks for; `max(iterable)` is the one-argument form.
Following the message literally, `max(xs, 0)`, compiles into a comparison against the list
rather than a maximum over it, so the advice was not just unhelpful but actively wrong.
Issue #186.

The elements are known whenever the length is, so this unrolls to exactly the comparisons the
multi-argument form already emits, and is implemented by rewriting into that form rather than
by a lowering of its own.

What this file checks is the IR: that the all-constant case folds to the right number, and
that a list holding a seeded value still decides at run time rather than folding the seed
away. The answers were also run on the simulation while the fix was written, against what
CPython returns for the same elements, over [3, 9, 2, 7] and over [seed, 4, 6] at three
seeds; that is not repeated here because it needs a running chip, and the fold assertion
below is what would catch a wrong constant.
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


def build(tmp_path: Path, body: str):
    src = tmp_path / "main.py"
    src.write_text(
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
        "from pymcu.types import uint8, asm\n\n\n"
        "def main():\n"
        "    seed: uint8 = GPIOR0.value\n"
        + body +
        '    asm("BREAK")\n'
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
    out = proc.stdout + proc.stderr
    return out, (json.loads(mir.read_text()) if "[BUILD_OK]" in proc.stdout and mir.exists() else None)


# --- the form that was refused -------------------------------------------------

@pytest.mark.parametrize("call", ["max(xs)", "min(xs)"])
def test_a_named_array_is_accepted(tmp_path, call):
    out, ir = build(tmp_path, "    xs = [3, 9, 2, 7]\n"
                              f"    GPIOR1.value = uint8({call})\n")
    assert ir is not None, out
    assert "expects at least two arguments" not in out


@pytest.mark.parametrize("call", ["max([5, 9, 2])", "min([5, 9, 2])"])
def test_a_list_literal_is_accepted(tmp_path, call):
    out, ir = build(tmp_path, f"    GPIOR1.value = uint8({call})\n")
    assert ir is not None, out


@pytest.mark.parametrize("call,expected", [("max(xs)", 9), ("min(xs)", 2)])
def test_the_answer_folds_to_the_right_constant(tmp_path, call, expected):
    """Every element is constant here, so the whole call should fold to one number."""
    out, ir = build(tmp_path, "    xs = [3, 9, 2, 7]\n"
                              f"    GPIOR1.value = uint8({call})\n")
    assert ir is not None, out
    main = next(f for f in ir["functions"] if f["name"] == "main")
    stores = [i["src"]["value"] for i in main["body"]
              if i.get("$t") == "copy" and i["src"].get("$t") == "const"
              and i["dst"].get("$t") == "mem"]
    assert expected in stores, \
        f"{call} over [3, 9, 2, 7] should fold to {expected}; stored constants were {stores}"


def test_a_run_time_element_still_compares(tmp_path):
    """A list holding a seeded value cannot fold, and must emit real comparisons."""
    out, ir = build(tmp_path, "    ys = [seed, 4, 6]\n"
                              "    GPIOR1.value = uint8(max(ys))\n")
    assert ir is not None, out
    main = next(f for f in ir["functions"] if f["name"] == "main")
    # The comparison reaches the IR as a conditional jump: the optimizer folds the
    # `Binary(GreaterThan)` and its `JumpIfZero` into one, so `jle` is what survives rather
    # than a `binary`. What is asserted is that the choice is made at run time at all.
    decided_at_runtime = any(str(i.get("$t", "")).startswith("j")
                             and i.get("$t") != "jmp" for i in main["body"])
    assert decided_at_runtime, \
        "max over a list with a run-time element folded away, so nothing compares it"


# --- the form that still cannot work, and has to say so ------------------------

@pytest.mark.parametrize("call", ["max([])", "min([])"])
def test_an_empty_sequence_is_refused_by_name(tmp_path, call):
    out, ir = build(tmp_path, f"    GPIOR1.value = uint8({call})\n")
    assert ir is None, "an empty sequence has no maximum and must not build"
    assert "expects at least two arguments" not in out, \
        "the old message counted arguments; this one has the count Python asks for"
    assert "length known at compile time" in out, f"the refusal does not name the shape:\n{out}"


def test_the_refusal_says_what_to_write_instead(tmp_path):
    out, _ = build(tmp_path, "    GPIOR1.value = uint8(max([]))\n")
    assert "separate arguments" in out and "loop" in out, \
        f"a refusal has to leave the reader something to do:\n{out}"


# --- the multi-argument form is untouched --------------------------------------

@pytest.mark.parametrize("call", ["max(seed, 10)", "min(seed, 10, 20)", "max(3, 9)"])
def test_the_multi_argument_form_still_works(tmp_path, call):
    out, ir = build(tmp_path, f"    GPIOR1.value = uint8({call})\n")
    assert ir is not None, out
