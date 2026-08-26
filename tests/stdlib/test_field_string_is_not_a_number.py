"""A string held in a field stays a string, whatever its length and however deep it lives.

`VisitExpression` turns a ONE-CHARACTER string literal into the character's own code, which is
what makes `c == 'x'` and `uart.write('A')` work. Longer strings are interned and can be turned
back into text from their id; a character code cannot, because 44 is also the number 44. So a
class holding `self.sep: str = ","` kept only the number, and every later read of the field was
a number: `print(o.sep)` emitted a decimal write of 44 where "," was meant. Clean build, no
diagnostic, wrong output.

The second half is the same mistake one level deeper. A field read was matched as
`<name>.<field>` only, so `o.inner.sep` -- filed as `main.o_inner_sep`, text and all -- fell
through to the same numeric writer and printed the interned id. One level of nesting printed
the string; two printed 256.

Both are read off the IR rather than off the debug listing: what is pinned here is which call
the text reaches, and the listing's wording is no part of that.
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

GPIOR1 = 0x4A

# print() reaches the UART through the console the DRIVER wires up; pymcuc on its own has no
# stdout and answers "call to undefined function 'uart_write_str'". These programs open it
# themselves, with the same import and call the driver injects.
STDOUT_IMPORT = "from pymcu.hal.uart import UART as _stdout\n"
STDOUT_OPEN = "    _stdout(115200)\n"


def build(tmp_path: Path, source: str):
    """Compile a one-file program and return its .mir, or fail with the diagnostics."""
    (tmp_path / "main.py").write_text(source)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    return json.loads(mir.read_text())


def printed(mir):
    """What main() streams, in order: each text as the flash constant its write reaches for,
    and each number as the number it writes. The trailing newline of print() is dropped."""
    body = next(f["body"] for f in mir["functions"] if f["name"].endswith("main"))
    texts = {i["name"]: bytes(i["bytes"]).rstrip(b"\0").decode("latin1")
             for i in body if i["$t"] == "fdata"}
    out = []
    for i in body:
        if i["$t"] != "call":
            continue
        arg = i["args"][0] if i["args"] else {"$t": "none"}
        if "write_str" in i["functionName"]:
            out.append(texts.get(arg.get("name"), "<runtime>") if arg["$t"] == "fstr"
                       else "<runtime>")
        elif "write_decimal" in i["functionName"]:
            out.append(f"<number {arg['value']}>" if arg["$t"] == "const" else "<number>")
    return [t for t in out if t != "\n"]


ONE_CHAR_FIELD = (
    STDOUT_IMPORT
    + "from pymcu.types import inline\n\n\n"
    + "class Cfg:\n"
    + "    @inline\n"
    + "    def __init__(self):\n"
    + '        self.one: str = ","\n'
    + '        self.two: str = ",;"\n\n\n'
    + "def main():\n"
    + STDOUT_OPEN
    + "    c = Cfg()\n"
    + '    sep = ","\n'
    + "    print(sep)\n"
    + "    print(c.one)\n"
    + "    print(c.two)\n"
)


def test_a_one_character_field_prints_its_character_not_its_code(tmp_path):
    """The discriminator: `c.one` used to arrive as `<number 44>`.

    The plain local and the two-character field are in the same program because both already
    worked: a fix that carried the text by dropping the character convention, or one that only
    looked at the field it was written for, would show up here rather than in a later build.
    """
    assert printed(build(tmp_path, ONE_CHAR_FIELD)) == [",", ",", ",;"]


NESTED_FIELD = (
    STDOUT_IMPORT
    + "from pymcu.types import inline\n\n\n"
    + "class Inner:\n"
    + "    @inline\n"
    + "    def __init__(self, s: str):\n"
    + "        self.sep: str = s\n\n\n"
    + "class Outer:\n"
    + "    @inline\n"
    + "    def __init__(self):\n"
    + '        self.inner: Inner = Inner("XYZ")\n\n\n'
    + "class Flat:\n"
    + "    @inline\n"
    + "    def __init__(self):\n"
    + '        self.sep: str = "ABC"\n\n\n'
    + "def main():\n"
    + STDOUT_OPEN
    + "    o = Outer()\n"
    + "    p = Flat()\n"
    + "    print(o.inner.sep)\n"
    + "    print(p.sep)\n"
)


def test_a_field_two_levels_deep_prints_its_text_not_its_id(tmp_path):
    """The discriminator: the nested read used to arrive as `<number 256>`, the interned id.

    `Flat` shares the program for the same reason: one level already worked, and a fix that
    broke it while making two levels work would pass a test that read only the nested one.
    """
    assert printed(build(tmp_path, NESTED_FIELD)) == ["XYZ", "ABC"]


CHAR_COMPARISON = (
    "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n"
    "from pymcu.types import inline, uint8\n\n\n"
    "class Cfg:\n"
    "    @inline\n"
    "    def __init__(self):\n"
    '        self.sep: str = ","\n\n\n'
    "def main():\n"
    "    c = Cfg()\n"
    "    n: uint8 = GPIOR0.value\n"
    '    if c.sep == ",":\n'
    "        GPIOR1.value = n\n"
)


def test_a_one_character_field_is_still_a_character_where_one_is_wanted(tmp_path):
    """The invariant. Carrying the text must not cost the numeric identity: the character code
    is what `== ","` folds against, and what passing the field to a byte writer needs.

    A field that had stopped comparing equal would leave the store behind a run-time branch,
    or drop it entirely; a folded comparison leaves it as the only store to GPIOR1.
    """
    body = next(f["body"] for f in build(tmp_path, CHAR_COMPARISON)["functions"]
                if f["name"].endswith("main"))
    ops = [i for i in body if i["$t"] not in ("dbg", "ret")]
    # Two copies and nothing else: the read of GPIOR0 and the store to GPIOR1. A comparison
    # that stopped folding leaves a `jne` and the label it jumps to (measured: the same
    # program written against a run-time value emits dbg, copy, dbg, jne, dbg, copy, lbl,
    # ret), so this fails on the branch as well as on a store that went missing.
    assert [i["$t"] for i in ops] == ["copy", "copy"], \
        f"the character comparison stopped folding: {ops}"
    assert ops[1]["dst"].get("address") == GPIOR1, \
        f"the guarded store is not the one that survived: {ops}"
