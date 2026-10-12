"""`pymcuc --library` exports every top-level function, and used to lose some of them in silence.

A function with no subroutine ABI (one that returns a tuple, takes a class instance, ...) is
registered for expansion at its call sites. A library has no call site, so the function was
expanded nowhere and emitted nowhere, and the compiler said BUILD_OK with the function simply
absent from the IR (PyMCU#365 was one form of it, a `bytes` parameter; a tuple return is
another). Anything that loads the library found out much later, as an undefined symbol from the
linker that had been told the symbol would be there.

Every non-inline top-level function of the entry file must now come out as a symbol, or the
compile stops at its `def` and says why. `@inline` is the way to say "a template, not a symbol".
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

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]


def build(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.asm"),
         "--arch", "arm", "--target", "rp2040", "--freq", "125000000", "--library",
         "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    names = []
    if mir.exists():
        names = [f["name"] for f in json.loads(mir.read_text())["functions"]]
    return proc, names


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("source, line, reason", [
    pytest.param("def ok(a: int) -> int:\n    return a\n\n\n"
                 "def pair(a: int) -> tuple[int, int]:\n    return (a, a)\n",
                 5, "returns a tuple", id="tuple-return"),
    pytest.param("class P:\n    def __init__(self, x: int):\n        self.x = x\n\n\n"
                 "def get(p: P) -> int:\n    return p.x\n",
                 6, "class instance", id="class-instance-parameter"),
])
def test_an_export_with_no_subroutine_form_is_refused_at_its_def(tmp_path, py_parser, source, line, reason):
    proc, _ = build(tmp_path, source, py_parser)

    assert proc.returncode != 0, "BUILD_OK with the function missing is the bug"
    assert f"main.py:{line}:" in proc.stderr
    assert reason in proc.stderr
    assert "@inline" in proc.stderr or "Drop @used" in proc.stderr, "the message names the way out"


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_inline_template_is_not_expected_to_be_a_symbol(tmp_path, py_parser):
    proc, names = build(tmp_path, (
        "@inline\ndef pair(a: int) -> tuple[int, int]:\n    return (a, a)\n\n\n"
        "def lo(a: int) -> int:\n    x, y = pair(a)\n    return x\n"), py_parser)

    assert proc.returncode == 0, proc.stderr
    assert names == ["lo"]


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_ordinary_library_still_exports_every_function(tmp_path, py_parser):
    proc, names = build(tmp_path, (
        "def h(a: int) -> int:\n    return a * 2\n\n\n"
        "def F(a: int) -> int:\n    return h(a)\n"), py_parser)

    assert proc.returncode == 0, proc.stderr
    assert sorted(names) == ["F", "h"]


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_tuple_helper_that_another_function_calls_is_expanded_into_it(tmp_path, py_parser):
    """The guard is about functions that VANISHED. A helper that returns a tuple and is used by
    another function of the library has a use: it is expanded at the call, as it always was, and
    needs no symbol."""
    proc, names = build(tmp_path, (
        "def pair(a: int) -> tuple[int, int]:\n    return (a, a + 1)\n\n\n"
        "def lo(a: int) -> int:\n    x, y = pair(a)\n    return x + y\n"), py_parser)

    assert proc.returncode == 0, proc.stderr
    assert names == ["lo"]


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_tuple_function_that_nothing_calls_is_still_refused(tmp_path, py_parser):
    proc, _ = build(tmp_path, (
        "def lo(a: int) -> int:\n    return a\n\n\n"
        "def pair(a: int) -> tuple[int, int]:\n    return (a, a + 1)\n"), py_parser)

    assert proc.returncode != 0
    assert "main.py:5:" in proc.stderr and "returns a tuple" in proc.stderr


# A call is what exempts a tuple function from being a symbol, and the call is the one the
# compiler resolved and expanded -- not a mention. Each source below mentions `lo` in a way
# that is not a call, so `lo` has vanished and has to be refused, at its own `def` (line 1).
TUPLE_LO = "def lo(a: int) -> tuple[int, int]:\n    return (a, a + 1)\n\n\n"


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("rest", [
    pytest.param('def use(a: int) -> int:\n    return len("lo") + a\n',
                 id="the-name-inside-a-string"),
    pytest.param("def lo2(a: int) -> int:\n    return a\n\n\n"
                 "def use(a: int) -> int:\n    return lo2(a)\n",
                 id="a-longer-name-that-starts-with-it"),
    pytest.param("class B:\n    def __init__(self):\n        self.lo = 1\n\n\n"
                 "def use(a: int) -> int:\n    b = B()\n    return b.lo + a\n",
                 id="an-attribute-of-that-name"),
])
def test_a_mention_that_is_not_a_call_does_not_save_a_tuple_function(tmp_path, py_parser, rest):
    proc, _ = build(tmp_path, TUPLE_LO + rest, py_parser)

    assert proc.returncode != 0, "BUILD_OK with `lo` missing is the bug"
    assert "main.py:1:" in proc.stderr and "returns a tuple" in proc.stderr
