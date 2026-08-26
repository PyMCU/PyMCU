"""`for a, b in [(1, 2), (3, 4)]` unpacks each pair, and the refusals name the real condition.

The form was rejected with

    error: CompileError: for-in list/tuple iterable elements must be compile-time integer
    constants.

for a program in which every element is a compile-time integer constant. What was missing
was not constness, it was the unpacking: the unrolling bound one loop name and a pair has
two values. The message named the one property the program already had. Issue #188.

The AST already carried the second name (`ForStmt.Var2Name`, used by `enumerate` and `zip`),
so the loop binds it alongside the first from the element's second component and unrolls
exactly as the single-name form does.

WHAT DISCRIMINATES. The acceptance tests and the three refusal-wording tests fail against
the unfixed compiler: the accepting ones because it refuses, and the wording ones because
the sentence it produced said "must be compile-time integer constants" in all cases.

WHAT IS INVARIANT, and here on purpose. The single-name form over a flat list, which worked
before and must keep working, and the run-time-value refusal, which was correct before and
whose wording is the one case that still legitimately talks about constants.
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
        "from pymcu.types import uint8\n\n\n"
        "def main():\n"
        "    seed: uint8 = GPIOR0.value\n"
        "    total: uint8 = 0\n"
        + body +
        "    GPIOR1.value = total + seed\n"
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


def folded_constants(ir):
    """Every constant operand of a binary op in main, in order."""
    main = next(f for f in ir["functions"] if f["name"] == "main")
    return [i["src1"]["value"] for i in main["body"]
            if i.get("$t") == "binary" and i.get("src1", {}).get("$t") == "const"]


# --- the form that was refused -------------------------------------------------

def test_a_list_of_pairs_unpacks_both_names(tmp_path):
    out, ir = build(tmp_path, "    for a, b in [(1, 2), (3, 4)]:\n"
                              "        total = total + a * b\n")
    assert ir is not None, out


def test_the_unrolled_value_is_right(tmp_path):
    """1*2 + 3*4 is 14, and the seed stays out of the fold.

    This is the assertion that would catch the second name being bound to the wrong
    component, or to the first one twice: binding `b` to `a` gives 1 + 9 = 10, and
    swapping the pair gives the same 14, so the operands are chosen to make only the
    correct pairing produce it.
    """
    out, ir = build(tmp_path, "    for a, b in [(1, 2), (3, 4)]:\n"
                              "        total = total + a * b\n")
    assert ir is not None, out
    assert 14 in folded_constants(ir), folded_constants(ir)


def test_a_tuple_of_pairs_works_too(tmp_path):
    out, ir = build(tmp_path, "    for a, b in ((1, 2), (3, 4)):\n"
                              "        total = total + a * b\n")
    assert ir is not None, out
    assert 14 in folded_constants(ir), folded_constants(ir)


def test_the_body_runs_once_per_pair(tmp_path):
    """Three pairs whose products are distinct powers of two, so a dropped or repeated
    iteration changes the sum rather than leaving it plausible: 1 + 2 + 4 is 7."""
    out, ir = build(tmp_path, "    for a, b in [(1, 1), (1, 2), (2, 2)]:\n"
                              "        total = total + a * b\n")
    assert ir is not None, out
    assert 7 in folded_constants(ir), folded_constants(ir)


# --- what the refusals say now -------------------------------------------------

def test_one_name_over_pairs_says_the_second_value_has_nowhere_to_go(tmp_path):
    out, ir = build(tmp_path, "    for p in [(1, 2), (3, 4)]:\n"
                              "        total = total + 1\n")
    assert ir is None
    assert "nowhere to put the second value" in out, out
    assert "must be compile-time integer constants" not in out, out


def test_two_names_over_flat_elements_says_the_element_is_not_a_pair(tmp_path):
    out, ir = build(tmp_path, "    for a, b in [1, 2]:\n"
                              "        total = total + a\n")
    assert ir is None
    assert "has to be a pair" in out, out
    assert "must be compile-time integer constants" not in out, out


def test_two_names_over_a_triple_says_how_many_it_found(tmp_path):
    out, ir = build(tmp_path, "    for a, b in [(1, 2, 3)]:\n"
                              "        total = total + a\n")
    assert ir is None
    assert "this element has 3" in out, out


# --- invariants: these held before and must keep holding -----------------------

def test_the_single_name_form_over_a_flat_list_still_works(tmp_path):
    out, ir = build(tmp_path, "    for a in [1, 2, 4]:\n"
                              "        total = total + a\n")
    assert ir is not None, out
    assert 7 in folded_constants(ir), folded_constants(ir)


def test_a_run_time_value_in_a_pair_is_still_refused_as_non_constant(tmp_path):
    """The one refusal that legitimately talks about constants, because here the element
    really does hold something that is not one."""
    out, ir = build(tmp_path, "    for a, b in [(1, seed)]:\n"
                              "        total = total + a\n")
    assert ir is None
    assert "integer constants" in out, out
