"""`{{` and `}}` in an f-string are one literal brace each, and both front ends agree.

The escapes were unhandled in every position of the C# front end, and each position failed
differently. The worst was `f"{{{seed}}}"`, where `{seed}` reached the expression sub-lexer and
came back as "Expected '}' after set literal" -- a construct that is not in the line. The other
three reported the single brace as unbalanced, which is exactly what the doubling exists to
prevent.

**The Python front end already got all four right**, because CPython's own parser does the
unescaping before the AST is handed over. That makes it an executable oracle rather than a
second implementation to keep in step: the fix is not "invent a behaviour", it is "make the C#
front end produce the image the correct one already produces", and the tests below assert that
equality directly instead of asserting a string this file made up.

That oracle also decided a detail that no message test would have caught. `f"{{}}"` has to stay
ONE text part: splitting it into a part per brace emits two uart_write_str calls where CPython
emits one, the images stop matching, and the differential axis in pymcu-avr goes red for a
reason that looks nothing like brace handling.
"""

import json
import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

HEAD = (
    "from pymcu.hal.uart import UART as _stdout\n"
    "from pymcu.chips.atmega328p import GPIOR0\n"
    "from pymcu.types import uint8\n"
    "import pymcu.strfmt as _pymcu_strfmt\n\n\n"
    "def main():\n"
    "    _stdout(115200)\n"
    "    seed: uint8 = GPIOR0.value\n"
)


def compile_(tmp_path: Path, line: str, py_parser: bool):
    """(ok, output, main body) for a program whose last statement is `line`."""
    tmp_path.mkdir(exist_ok=True)
    (tmp_path / "main.py").write_text(HEAD + line + "\n")
    mir = tmp_path / "firmware.mir"
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    out = proc.stdout + proc.stderr
    if "[BUILD_OK]" not in proc.stdout:
        return False, out, None
    ir = json.loads(mir.read_text())
    body = next(f["body"] for f in ir["functions"] if f["name"].endswith("main"))
    return True, out, [json.dumps(i) for i in body if i["$t"] != "dbg"]


def emitted(body, ir_text):
    """The texts the program streams, with the trailing newline of print() dropped."""
    out = []
    for i in (json.loads(x) for x in body):
        if i["$t"] != "call":
            continue
        arg = i["args"][0] if i["args"] else {}
        if "write_str" in i["functionName"] and arg.get("$t") == "fstr":
            out.append(ir_text.get(arg["name"], "?"))
        elif "write_decimal" in i["functionName"]:
            out.append("<num>")
    return "".join(t for t in out if t != "\n")


def texts(body):
    return {i["name"]: bytes(i["bytes"]).rstrip(b"\0").decode("latin1")
            for i in (json.loads(x) for x in body) if i["$t"] == "fdata"}


CASES = {
    "a brace on each side of a value": ('    print(f"{{{seed}}}")', "{<num>}"),
    "an empty pair": ('    print(f"{{}}")', "{}"),
    "an opening brace alone": ('    print(f"{{")', "{"),
    "a closing brace after a value": ('    print(f"{seed}}}")', "<num>}"),
    "the JSON line": ('    print(f"{{\\"t\\": {seed}}}")', '{"t": <num>}'),
}


@pytest.mark.parametrize("py_parser", [pytest.param(False, id="csharp"),
                                       pytest.param(True, id="python")])
@pytest.mark.parametrize("name,case", CASES.items(), ids=list(CASES))
def test_a_doubled_brace_emits_one_brace(tmp_path, name, case, py_parser):
    """The discriminator. Each of these was a different SyntaxError in the C# front end, and
    the JSON line is the program the issue was filed for."""
    line, expected = case
    ok, out, body = compile_(tmp_path, line, py_parser)
    assert ok, out
    assert emitted(body, texts(body)) == expected, emitted(body, texts(body))


@pytest.mark.parametrize("name,case", CASES.items(), ids=list(CASES))
def test_both_front_ends_produce_the_same_image(tmp_path, name, case):
    """The real specification, and the reason this fix did not need one written by hand: the
    Python front end reads CPython's parse, which has always done this correctly. Comparing
    main's body is what pins `f"{{}}"` as a single text part rather than two."""
    line, _ = case
    ok_c, out_c, body_c = compile_(tmp_path / "c", line, py_parser=False)
    ok_p, out_p, body_p = compile_(tmp_path / "p", line, py_parser=True)
    assert ok_c and ok_p, (out_c, out_p)
    assert body_c == body_p, (body_c, body_p)


def test_an_empty_pair_is_one_string_not_two(tmp_path):
    """Stated on its own because it is the detail the image comparison is protecting, and a
    reader changing this code will otherwise see two text parts as equivalent to one."""
    ok, out, body = compile_(tmp_path, '    print(f"{{}}")', py_parser=False)
    assert ok, out
    writes = [i for i in (json.loads(x) for x in body)
              if i["$t"] == "call" and "write_str" in i["functionName"]]
    # One for "{}" and one for print's newline.
    assert len(writes) == 2, [w["functionName"] for w in writes]


# --- what must not change --------------------------------------------------------------------

UNCHANGED = {
    "a plain replacement field": ('    print(f"v={seed}")', "v=<num>"),
    "the debug spelling": ('    print(f"{seed=}")', "seed=<num>"),
    "a brace in a plain string": ('    print("{}")', "{}"),
}


@pytest.mark.parametrize("py_parser", [pytest.param(False, id="csharp"),
                                       pytest.param(True, id="python")])
@pytest.mark.parametrize("name,case", UNCHANGED.items(), ids=list(UNCHANGED))
def test_the_rest_of_the_f_string_is_untouched(tmp_path, name, case, py_parser):
    """The invariants. The brace handling sits in the same scanner loop as the replacement
    field and the escape sequences, so the ordinary forms are pinned beside it."""
    line, expected = case
    ok, out, body = compile_(tmp_path, line, py_parser)
    assert ok, out
    assert emitted(body, texts(body)) == expected, emitted(body, texts(body))


@pytest.mark.parametrize("line,ids", [('    print(f"}")', "lone-closing"),
                                      ('    print(f"{seed")', "unterminated")],
                         ids=["lone-closing", "unterminated"])
def test_a_single_unmatched_brace_is_still_an_error(tmp_path, line, ids):
    """The other invariant, and the one that says the fix did not simply stop checking: a
    SINGLE brace is still unbalanced, and each message now points at the doubling that would
    have made it a literal."""
    ok, out, _ = compile_(tmp_path, line, py_parser=False)
    assert not ok, out
    assert "doubled" in out, out
