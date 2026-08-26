"""Two parser diagnostics that named the compiler instead of the program, in both front ends.

**A diagnostic wrapped inside another diagnostic.** `@classmethod` raises a message written to
be read -- it names the construct, says why, and offers a way forward -- and the top-level
statement loop caught it and re-threw it as "Expected function definition, import, or valid
statement. Original error: " + that message. The reader met a generic sentence that is also
wrong (a function definition IS what follows), then the phrase "Original error:", which says
they are looking at compiler internals. Many stop at the first sentence. The wrapper also lost
the inner error's position, because Error() re-reads the CURRENT token.

**`...` as a body.** The ordinary Python placeholder answered "Expected expression" in the C#
front end and "literal of type ellipsis" in the Python one, and `pass` in the same position
works. It is now accepted as the `pass` it means, in the statement position only.

Every case is run through BOTH front ends. They disagreed on both defects before the fix --
different text for `@classmethod`, different text for `...` -- which is the divergence #196 is
about, and a test that ran one parser would have called either of them fixed while the other
still answered its own way.
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
    "def main():\n"
    "    print(A.make())\n"
)


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_specific_diagnostic_is_not_wrapped_in_a_generic_one(tmp_path, py_parser):
    """The discriminator: the C# front end prefixed this with "Expected function definition,
    import, or valid statement. Original error:"."""
    ok, out = compile_(tmp_path, CLASSMETHOD, py_parser)
    assert not ok, out
    assert "Original error:" not in out, out
    assert "Expected function definition" not in out, out
    assert "@classmethod is not supported" in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_reason_and_the_way_forward_survive(tmp_path, py_parser):
    """The message has to keep doing its job. The Python front end had a shorter text of its
    own -- "no runtime class object", with neither -- so this is a discriminator there and a
    guard in the C# one."""
    ok, out = compile_(tmp_path, CLASSMETHOD, py_parser)
    assert not ok, out
    assert "no runtime class object" in out, out
    assert "factory function" in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_alternative_the_message_offers_compiles(tmp_path, py_parser):
    """The rule this file exists to keep: check every position a message sends the reader to.

    The message used to offer a @staticmethod that calls the constructor as well. Measured,
    that answers "Function 'A_make' expects 1 arguments, but 0 were provided" -- the method
    still carries self -- so it was sending readers to a program that does not build. It is
    gone, and what is left is checked here rather than asserted.
    """
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


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_alternative_that_does_not_work_is_not_offered(tmp_path, py_parser):
    """The other half: the @staticmethod shape still fails, so the message must not name it."""
    ok, out = compile_(tmp_path, CLASSMETHOD, py_parser)
    assert not ok, out
    assert "staticmethod" not in out, out


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

@pytest.mark.parametrize("source,ids", [(CLASSMETHOD, "classmethod"),
                                        ("def main():\n    x = ...\n", "ellipsis-expression")],
                         ids=["classmethod", "ellipsis-expression"])
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
