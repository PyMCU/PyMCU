"""Two parser diagnostics that named the compiler instead of the program, in both front ends.

**A diagnostic wrapped inside another diagnostic.** That wrapper used to bury
`@classmethod` (now a compile-time class-namespace expansion) and still must
not wrap other refusals. The ellipsis cases below pin that the inner message
is the whole diagnostic.

**`...` as a body.** The ordinary Python placeholder answered "Expected expression" in the C#
front end and "literal of type ellipsis" in the Python one, and `pass` in the same position
works. It is now accepted as the `pass` it means, in the statement position only.

Every case is run through BOTH front ends.
"""

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
STDOUT = "from pymcu.hal.uart import UART as _stdout\n"


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", "/dev/null"],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


# --- 1. the specific diagnostic is the whole diagnostic --------------------------------------

CLASSMETHOD = (
    "from pymcu.types import uint8\n\n\n"
    "class A:\n"
    "    @classmethod\n"
    "    def make(cls) -> uint8:\n"
    "        return 77\n\n\n"
    "def main() -> uint8:\n"
    "    return A.make()\n"
)


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_classmethod_compiles_on_both_front_ends(tmp_path, py_parser):
    """@classmethod is compile-time class-namespace population. Adafruit sht4x
    writes Mode.add_values; A.make() returning a constant is the same construct."""
    ok, out = compile_(tmp_path, CLASSMETHOD, py_parser)
    assert ok, out
    assert "Original error:" not in out, out
    assert "@classmethod is not supported" not in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_alternative_the_old_message_offered_still_compiles(tmp_path, py_parser):
    """A module-level factory was the workaround before @classmethod compiled.
    It still builds, so existing programs that took that advice keep working."""
    ok, out = compile_(
        tmp_path,
        STDOUT
        + "from pymcu.types import uint8, inline\n\n\n"
        + "class A:\n"
        + "    @inline\n"
        + "    def __init__(self, n: uint8):\n"
        + "        self.n: uint8 = n\n\n\n"
        + "def make() -> uint8:\n"
        + "    a = A(77)\n"
        + "    return a.n\n\n\n"
        + "def main():\n"
        + "    _stdout(115200)\n"
        + "    print(make())\n",
        py_parser)
    assert ok, out


# --- 2. `...` is the pass it means, where it means it ----------------------------------------

def body(text, ret="uint8"):
    return (STDOUT + "from pymcu.types import uint8\n\n\n"
            + f"def f() -> {ret}:\n{text}\n\n\ndef main():\n    _stdout(115200)\n    print(77)\n")


ELLIPSIS_CASES = {
    "a function body": body("    ...\n", ret="None"),
    "a method body": (STDOUT + "from pymcu.types import uint8\n\n\n"
                      + "class A:\n    def m(self) -> None:\n        ...\n\n\n"
                      + "def main():\n    _stdout(115200)\n    print(77)\n"),
    "a top-level statement": (STDOUT + "\n...\n\n\ndef main():\n"
                              + "    _stdout(115200)\n    print(77)\n"),
}


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("name,source", ELLIPSIS_CASES.items(), ids=list(ELLIPSIS_CASES))
def test_ellipsis_as_a_body_is_accepted(tmp_path, name, source, py_parser):
    """The discriminator: "Expected expression" in one front end, "literal of type ellipsis" in
    the other, for the most ordinary way there is to sketch a function."""
    ok, out = compile_(tmp_path, source, py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_ellipsis_reaches_the_same_diagnostic_as_pass(tmp_path, py_parser):
    """Accepted as `pass` means accepted as `pass`, including what follows from it: a body that
    returns nothing in a function declared to return uint8 is still an error, and it is the
    error `pass` gets rather than a parser position report."""
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    ok_dots, out_dots = compile_(tmp_path / "a", body("    ...\n"), py_parser)
    ok_pass, out_pass = compile_(tmp_path / "b", body("    pass\n"), py_parser)
    assert not ok_dots and not ok_pass, (out_dots, out_pass)
    assert "can reach the end of its body without a return" in out_dots, out_dots
    assert "can reach the end of its body without a return" in out_pass, out_pass


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_ellipsis_in_an_expression_is_named(tmp_path, py_parser):
    """The invariant that keeps the acceptance narrow. There is no PyMCU value for Ellipsis, so
    only the statement position can take it; anywhere else it is refused BY NAME rather than
    left to "Expected expression"."""
    ok, out = compile_(
        tmp_path,
        "from pymcu.types import uint8\n\n\ndef main():\n    x = ...\n    print(77)\n",
        py_parser)
    assert not ok, out
    assert "'...'" in out and "Ellipsis" in out, out
    assert "Expected expression" not in out, out
    assert "literal of type" not in out, out


# --- both front ends have to answer the same ------------------------------------------------

@pytest.mark.parametrize("source,ids", [
                                        ("def main():\n    x = ...\n", "ellipsis-expression")],
                         ids=["ellipsis-expression"])
def test_the_two_front_ends_give_the_same_message(tmp_path, source, ids):
    """Both of these had a text per front end, which is the #196 shape: the same program
    answered differently depending on which parser ran, and neither answer was wrong enough to
    be noticed. Pinned as the equality it is, so a later edit to one has to touch the other."""
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    _, csharp = compile_(tmp_path / "a", source, py_parser=False)
    _, python = compile_(tmp_path / "b", source, py_parser=True)

    def message(out):
        line = next(l for l in out.splitlines() if "error:" in l)
        return line.split("error:", 1)[1].split(":", 1)[1].strip()

    assert message(csharp) == message(python), (message(csharp), message(python))
