"""A keyword argument a builtin does not have is refused, not dropped.

`print(1, 2, foo=3)` built clean and emitted exactly `print(1, 2)`. CPython raises TypeError.
The keywords print really has are `sep` and `end`, so the shape this hides in is a near miss on
a real one: someone who writes `sep2=` or `end_=` gets the default separator and no indication
that the argument they wrote was ignored, and the output looks like a formatting mistake of
their own.

Every other call already answers this. A user function, an @inline and a constructor all go
through the parameter binder, which says `unknown keyword argument 'x' in call to 'f'`. `print`
and `input` lower their own keywords and neither loop had an else, so they never reached it.
`input` is the same defect with a price attached: `maxlenn=8` took the default 64-byte buffer
instead of 8, which is 56 bytes of SRAM nobody asked for on a part that has two thousand.

Both front ends are checked. The refusal lives in the IR generator, which they share, and a
test that only ran the C# parser would not have said so.
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

STDOUT = "from pymcu.hal.uart import UART as _stdout\n"
FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]


def compile_(tmp_path: Path, body: str, py_parser: bool = False):
    """(ok, output) for a program whose main() is `body`."""
    (tmp_path / "main.py").write_text(
        STDOUT + "\n\ndef main():\n    _stdout(115200)\n" + body)
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


# --- print ---------------------------------------------------------------------------------

@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_print_refuses_a_keyword_it_does_not_have(tmp_path, py_parser):
    """The discriminator: this built and emitted `print(1, 2)`, with the 3 nowhere in the IR."""
    ok, out = compile_(tmp_path, "    print(1, 2, foo=3)\n", py_parser)
    assert not ok, "the unknown keyword compiled; this test has stopped discriminating"
    assert "'foo'" in out and "sep" in out and "end" in out, out


@pytest.mark.parametrize("wrong,meant", [("sep2", "sep"), ("end_", "end")])
def test_a_near_miss_is_named(tmp_path, wrong, meant):
    """The failure mode the issue is about is a misspelling of a keyword that exists, so the
    suggestion is the part that turns the diagnostic into the fix."""
    ok, out = compile_(tmp_path, f'    print(1, 2, {wrong}=",")\n')
    assert not ok, out
    assert f"Did you mean '{meant}'?" in out, out


def test_a_keyword_that_is_not_a_near_miss_gets_no_suggestion(tmp_path):
    """A suggestion has to be worth making. `foo` is not a misspelling of `sep` or `end`, and
    offering one anyway is the noise that makes readers stop trusting them."""
    ok, out = compile_(tmp_path, "    print(1, 2, foo=3)\n")
    assert not ok, out
    assert "Did you mean" not in out, out


def test_the_keywords_print_does_have_still_work(tmp_path):
    """The invariant. `sep` and `end` were already correct and the refusal is next to them."""
    ok, out = compile_(tmp_path, '    print(1, 2, sep=",", end="")\n')
    assert ok, out


def test_a_keyword_of_the_right_name_and_the_wrong_kind_is_still_about_the_value(tmp_path):
    """Two ways to fall out of the accepted set and they are different questions. A key print
    HAS, carrying something that is not a literal, must not be reported as unknown."""
    ok, out = compile_(tmp_path, "    x = 1\n    print(1, sep=x)\n")
    assert not ok, out
    assert "unknown keyword" not in out, out
    assert "compile-time string literal" in out, out


# --- input ---------------------------------------------------------------------------------

@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_input_refuses_a_keyword_it_does_not_have(tmp_path, py_parser):
    """The second site, found by looking for the first one's shape rather than by report.

    Measured before the fix: `maxlenn=8` compiled and allocated the default 64-byte buffer
    (indices 0..63 in the IR) where `maxlen=8` allocates 8.
    """
    ok, out = compile_(
        tmp_path, '    s: bytearray = input(prompt="n? ", maxlenn=8)\n    print(s)\n', py_parser)
    assert not ok, "the unknown keyword compiled; this test has stopped discriminating"
    assert "'maxlenn'" in out and "Did you mean 'maxlen'?" in out, out


def test_the_keywords_input_does_have_still_work(tmp_path):
    """The invariant, and the one that pins the buffer is the point: this must keep sizing the
    buffer from maxlen rather than silently taking the default."""
    ok, out = compile_(
        tmp_path, '    s: bytearray = input(prompt="n? ", maxlen=8)\n    print(s)\n')
    assert ok, out


def test_an_input_keyword_of_the_wrong_kind_is_about_the_value(tmp_path):
    """`prompt=5` was dropped as silently as an unknown key, by the same missing else, and it
    is the other question again: the key exists, the value is not a literal of its kind."""
    ok, out = compile_(tmp_path, '    s: bytearray = input(prompt=5)\n    print(s)\n')
    assert not ok, out
    assert "unknown keyword" not in out, out
    assert "compile-time string literal" in out, out


# --- the rule this joins -------------------------------------------------------------------

USER_FUNCTION = (
    "from pymcu.chips.atmega328p import GPIOR1\n"
    "from pymcu.types import uint8\n\n\n"
    "def f(a: uint8, b: uint8) -> uint8:\n"
    "    return a + b\n\n\n"
    "def main():\n"
    "    GPIOR1.value = f(1, bogus=2)\n"
)


def test_a_user_function_already_answered_and_still_does(tmp_path):
    """The rule the two builtins were the exception to. Pinned here so the shared helper cannot
    be changed in a way that regresses the binder's own message."""
    (tmp_path / "main.py").write_text(USER_FUNCTION)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", "/dev/null"],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    assert "[BUILD_OK]" not in proc.stdout, out
    assert "unknown keyword argument 'bogus'" in out, out
