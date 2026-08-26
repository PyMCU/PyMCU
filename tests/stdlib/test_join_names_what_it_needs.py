"""str.join says which part of the call it cannot build, not what shape the statement is in.

`s = o.inner.sep.join(["a", "b"])` was refused with "str.join is supported in assignment form:
... assign the result to a variable before using it" -- advice describing a property line 18
already had. A reader who followed it rewrote the assignment they had and got the same error.
That message is right for one program, `print(sep.join([...]))`, and it was being printed for
every other refusal too, because the assignment lowering declined silently and left the wording
to the bare-expression path.

The condition was not the nesting either. Measured at faafb10e, a ONE-level field separator
(`o.sep`) and a literal separator with one run-time element were refused by the same sentence,
and copying the field into a local first -- what the issue proposed as the fix -- answered
"call to undefined function 'sep_join'", a symbol the program never wrote.

Most of what those sentences refused now compiles: see test_field_string_is_not_a_number.py for
the field read that was losing its text. What is left genuinely cannot be built at compile
time, and each refusal below names the part that cannot.
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

def _flash_strings(mir):
    """The texts the firmware actually holds. `ok` alone cannot tell a right join from a wrong
    one -- both compile."""
    out = []
    for fn in mir.get("functions", []):
        for i in fn.get("body", []):
            if i.get("$t") == "fdata":
                out.append(bytes(i["bytes"][:-1]).decode("latin1"))
    return out


STDOUT_IMPORT = "from pymcu.hal.uart import UART as _stdout\n"
STDOUT_OPEN = "    _stdout(115200)\n"
# `pymcu build` injects both of these; pymcuc on its own has neither a console nor the
# f-string helpers, and refuses with a message about invoking the compiler by hand.
STRFMT_IMPORT = "import pymcu.strfmt as _pymcu_strfmt\n"


def compile_(tmp_path: Path, source: str):
    """(ok, output, mir). `mir` is None when the build was refused."""
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


def texts(mir):
    """The flash string constants main() lays down, in order."""
    body = next(f["body"] for f in mir["functions"] if f["name"].endswith("main"))
    return [bytes(i["bytes"]).rstrip(b"\0").decode("latin1")
            for i in body if i["$t"] == "fdata"]


# --- the program the issue reported ------------------------------------------------------

NESTED_SEPARATOR = (
    STDOUT_IMPORT
    + "from pymcu.types import inline\n\n\n"
    + "class Inner:\n"
    + "    @inline\n"
    + "    def __init__(self, s: str):\n"
    + "        self.sep: str = s\n\n\n"
    + "class Outer:\n"
    + "    @inline\n"
    + "    def __init__(self):\n"
    + '        self.inner: Inner = Inner(",")\n\n\n'
    + "def main():\n"
    + STDOUT_OPEN
    + "    o = Outer()\n"
    + '    s = o.inner.sep.join(["a", "b"])\n'
    + "    print(s)\n"
)


def test_a_separator_that_is_a_nested_field_joins(tmp_path):
    """The discriminator. Not just that it builds: that it built the right text, since a
    separator read as a number would have joined with something else and still compiled."""
    ok, out, mir = compile_(tmp_path, NESTED_SEPARATOR)
    assert ok, out
    assert "a,b" in texts(mir), texts(mir)


# --- what is left that cannot be built ---------------------------------------------------

RUNTIME_ELEMENT = (
    STDOUT_IMPORT
    + STRFMT_IMPORT
    + "from pymcu.chips.atmega328p import GPIOR0\n"
    + "from pymcu.types import uint8\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    seed: uint8 = GPIOR0.value\n"
    + '    a = f"{seed}"\n'
    + '    s = ",".join([a, "b"])\n'
    + "    print(s)\n"
)


def test_a_run_time_element_is_named_instead_of_the_statement_shape(tmp_path):
    """The discriminator: this program IS in assignment form, and used to be told to put it
    in assignment form. The element that cannot be laid out is what the message must name."""
    ok, out, _ = compile_(tmp_path, RUNTIME_ELEMENT)
    assert not ok, "the run-time element compiled; this test has stopped discriminating"
    assert "element 0" in out and "'a'" in out, out
    assert "assign the result to a variable" not in out, out


def test_the_advice_the_refusal_gives_compiles(tmp_path):
    """The f-string the message offers instead has to be a fix the reader can apply."""
    ok, out, _ = compile_(tmp_path, RUNTIME_ELEMENT.replace(
        '    s = ",".join([a, "b"])\n', '    s = f"{a},b"\n'))
    assert ok, out


COPIED_SEPARATOR = (
    STDOUT_IMPORT
    + "from pymcu.types import inline\n\n\n"
    + "class Cfg:\n"
    + "    @inline\n"
    + "    def __init__(self):\n"
    + '        self.sep: str = "-"\n\n\n'
    + "def main():\n"
    + STDOUT_OPEN
    + "    c = Cfg()\n"
    + "    sep = c.sep\n"
    + '    s = sep.join(["a", "b"])\n'
    + "    print(s)\n"
)


def test_the_copied_separator_now_compiles(tmp_path):
    """It used to be refused, and this test used to pin that refusal. #209 gave a name bound
    from another name its text back, so `sep = c.sep` followed by `sep.join([...])` is now an
    ordinary compile-time join and there is nothing left to refuse.

    Kept rather than deleted because the program is the one the ISSUE proposed as the
    workaround, and it went from "answers with a symbol the program never wrote" to "refused
    with an accurate sentence" to "compiles". Asserting the result, not just the exit code:
    compiling with the wrong separator would pass an `ok` check.
    """
    ok, out, mir = compile_(tmp_path, COPIED_SEPARATOR)
    assert ok, out
    assert "a-b" in _flash_strings(mir), _flash_strings(mir)


# The discriminator this file was built around, aimed at a program that is still refused: a
# separator bound to something that is not a string at all. It must name `sep` and say what it
# needs, and it must never name `sep_join`, a symbol the reader's program does not contain.
TEXTLESS_SEPARATOR = (
    STDOUT_IMPORT
    + "from pymcu.types import uint8\n\n\n"
    + "def pick(n: uint8) -> uint8:\n"
    + "    return n\n\n\n"
    + "def main():\n"
    + STDOUT_OPEN
    + "    sep = pick(3)\n"
    + '    s = sep.join(["a", "b"])\n'
    + "    print(s)\n"
)


def test_a_separator_with_no_text_does_not_name_a_symbol_the_program_never_wrote(tmp_path):
    """The discriminator: this answered "call to undefined function 'sep_join'", sending the
    reader to look for a typo in a name their program does not contain."""
    ok, out, _ = compile_(tmp_path, TEXTLESS_SEPARATOR)
    assert not ok, "the textless separator compiled; this test has stopped discriminating"
    assert "sep_join" not in out, out
    assert "'sep'" in out and "compile-time string" in out, out


# --- what must not change -----------------------------------------------------------------

BARE_EXPRESSION = (
    STDOUT_IMPORT
    + "def main():\n"
    + STDOUT_OPEN
    + '    print(",".join(["a", "b"]))\n'
)


def test_a_join_that_really_is_a_bare_expression_still_asks_for_the_assignment(tmp_path):
    """The invariant. The old sentence is the right one for exactly this program, and the
    point of the fix is that it stops being printed for the others -- not that it goes away."""
    ok, out, _ = compile_(tmp_path, BARE_EXPRESSION)
    assert not ok, out
    assert "assignment form" in out, out


OWN_JOIN_METHOD = (
    "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
    "from pymcu.types import inline, uint8\n\n\n"
    "class Adder:\n"
    "    @inline\n"
    "    def __init__(self, base: uint8):\n"
    "        self.base: uint8 = base\n\n"
    "    @inline\n"
    "    def join(self, x: uint8) -> uint8:\n"
    "        return self.base + x\n\n\n"
    "def main():\n"
    "    a = Adder(3)\n"
    "    n: uint8 = GPIOR0.value\n"
    "    r = a.join(n)\n"
    "    GPIOR1.value = r\n"
)


def test_a_class_of_ones_own_may_still_have_a_join(tmp_path):
    """The invariant that pays for the refusals above. They are reached by asking whether the
    receiver is a string FIRST; a `join` on anything else is somebody's own method and belongs
    to the ordinary call lowering, which is where it still goes."""
    ok, out, _ = compile_(tmp_path, OWN_JOIN_METHOD)
    assert ok, out
