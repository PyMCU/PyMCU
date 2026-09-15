"""PyMCU#410. `uint64` and `int64` are refused, identically by both front ends.

Both names were in the compiler's own list of built-in type names, so the annotation was
legal, and `StringToDataType` had no branch for either, so it returned `UNKNOWN` -- whose
`SizeOf()` is 1. Measured before the fix across all five annotation positions, both front
ends and both targets: 40 of 40 accepted, exit 0, and the name carried `UNKNOWN` in the IR
wherever a width was observable. A `uint64` field was one byte and nothing said so.

The refusal lives at the one site both front ends reach, so parity here is by construction
rather than by two copies of a sentence. This file is what proves the construction holds:
the message, the line and the exit status have to match character for character, in every
position an annotation can be written in.

`pymcu.types` exports neither name, so a program using one does not import under CPython
either. That is the second reason the names cannot be made to work by widening a case.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

HEADER = re.compile(r"^[^\s:]+:(\d+):(\d+): error: (.*)$", re.MULTILINE)

# The five positions an annotation can be written in. Module-level statements throughout:
# a function appears only where the position under test needs one, and it is called from the
# top level rather than from a `main`.
POSITIONS = {
    "local": "def f() -> int:\n    x: {t} = 1\n    return x\n\n\ny: int = f()\n",
    "parameter": "def f(x: {t}) -> int:\n    return 1\n\n\ny: int = f(1)\n",
    "return": "def f() -> {t}:\n    return 1\n\n\ny: int = 0\n",
    "field": (
        "from pymcu.types import uint16\n\n\n"
        "class C:\n"
        "    def __init__(self, a: uint16) -> None:\n"
        "        self._a: uint16 = a\n"
        "        self._x: {t} = 1\n\n\n"
        "c = C(3)\n"
    ),
    "global": "x: {t} = 1\n",
}

WIDEST = {"uint64": "uint32", "int64": "int32"}


def _compile(src: Path, py_parser: bool, target: str = "atmega328p"):
    """(returncode, stderr) for one front end."""
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(src.parent)],
        capture_output=True, text=True, env=env,
    )
    return proc.returncode, proc.stderr


def _write(tmp_path: Path, body: str) -> Path:
    p = tmp_path / "main.py"
    p.write_text(body)
    return p


@pytest.mark.parametrize("name", ["uint64", "int64"])
@pytest.mark.parametrize("position", sorted(POSITIONS))
def test_a_sixty_four_bit_annotation_is_refused(tmp_path, position, name):
    src = _write(tmp_path, POSITIONS[position].format(t=name))

    code, err = _compile(src, py_parser=False)

    assert code != 0, f"{name} in a {position} position compiled, and it has no width here"
    assert name in err
    assert WIDEST[name] in err, "the refusal names the widest type that does exist"


@pytest.mark.parametrize("name", ["uint64", "int64"])
@pytest.mark.parametrize("position", sorted(POSITIONS))
def test_both_front_ends_refuse_it_the_same_way(tmp_path, position, name):
    src = _write(tmp_path, POSITIONS[position].format(t=name))

    hand_code, hand_err = _compile(src, py_parser=False)
    py_code, py_err = _compile(src, py_parser=True)

    assert hand_code == py_code != 0
    hand = HEADER.search(hand_err)
    py = HEADER.search(py_err)
    assert hand and py, f"expected a located diagnostic from both:\n{hand_err}\n---\n{py_err}"
    assert hand.group(1) == py.group(1), "the LINE must agree"
    assert hand.group(3) == py.group(3), "and the sentence, character for character"


@pytest.mark.parametrize("annotation", ["uint64[4]", "const[uint64]", "ptr[int64]"])
def test_a_bracketed_form_carrying_it_is_refused_too(tmp_path, annotation):
    # The width is inside the brackets, which is still where storage gets decided. A check
    # written only against the bare name would let all three through.
    src = _write(tmp_path, f"x: {annotation} = 1\n")

    code, err = _compile(src, py_parser=False)

    assert code != 0
    assert "64-bit" in err


@pytest.mark.parametrize("annotation", ["uint32", "int32", "uint8", "uint8[4]", "const[uint32]"])
def test_the_widths_that_do_exist_still_compile(tmp_path, annotation):
    # The controls. A refusal that also caught these would be a worse bug than the one it closes.
    src = _write(tmp_path, f"x: {annotation} = 1\n")

    code, err = _compile(src, py_parser=False)

    assert code == 0, err


def test_a_longer_name_containing_it_is_somebody_elses_name(tmp_path):
    # `myuint64` is a class, not a width. The refusal matches whole type names, so a substring
    # cannot drag an unrelated identifier into it.
    src = _write(
        tmp_path,
        "from pymcu.types import uint16\n\n\n"
        "class myuint64:\n"
        "    def __init__(self, a: uint16) -> None:\n"
        "        self._a: uint16 = a\n\n\n"
        "v: myuint64 = myuint64(1)\n",
    )

    code, err = _compile(src, py_parser=False)

    assert code == 0, err
