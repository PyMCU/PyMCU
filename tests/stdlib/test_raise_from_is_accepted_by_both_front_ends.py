"""`raise X() from e` (#434): accepted in both front ends, compiled as `raise X()`.

#277 refused the construct so a discarded Y could not swallow an undefined name silently
and unevenly between front ends. #434 reverses that: there is no traceback and no
`__cause__` on this target, so `from Y` is indistinguishable from omitting it once
compiled. Y is parsed (syntax errors still surface) and discarded, matching the
non-call raise MESSAGE (#262). Both front ends discard the same way.

Every case runs through BOTH front ends.
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


def compile_(tmp_path: Path, source: str, py_parser: bool, ir_path: Path | None = None):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        # The suite runs a second pass with PYMCU_PY_PARSER set in the ambient
        # environment. Asking for the C# front end means clearing it, not merely
        # not setting it, or this case runs the Python front end and says csharp.
        env.pop("PYMCU_PY_PARSER", None)
    ir_out = str(ir_path) if ir_path is not None else str(tmp_path / "out.mir")
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", ir_out],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


def _strip_debug_text(ir_doc: dict) -> dict:
    """The `dbg` opcode's `text` field echoes the exact source line, which differs by
    construction between a raise with `from Y` and one without -- that is cosmetic, not
    generated code. Every other field, in every other opcode, is the claim under test."""
    for fn in ir_doc.get("functions", []):
        for instr in fn.get("body", []):
            if instr.get("$t") == "dbg":
                instr["text"] = ""
    return ir_doc


RAISER = (
    "def f(x: uint8) -> uint8:\n"
    "    if x > 3:\n"
    "        raise ValueError\n"
    "    return x\n\n\n"
)


SHAPES = [
    pytest.param('raise TypeError("wrapped") from None', id="from-None"),
    pytest.param('raise TypeError("wrapped") from prev', id="from-a-name"),
    pytest.param("raise TypeError from prev", id="no-message"),
]


def _program(clause: str) -> str:
    return (
        RAISER
        + "def main() -> None:\n"
        "    prev: uint8 = 0\n"
        "    try:\n"
        "        v: uint8 = f(9)\n"
        "    except ValueError:\n"
        f"        {clause}\n"
    )


@pytest.mark.parametrize("clause", SHAPES)
@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_both_front_ends_accept_raise_from(tmp_path, clause, py_parser):
    ok, out = compile_(tmp_path, _program(clause), py_parser)
    assert ok, out
    assert "Expected newline or end of block" not in out, out
    assert "'raise ... from ...' is not supported" not in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_raise_from_ir_matches_raise_without_from(tmp_path, py_parser):
    """The whole point of #434: `raise X(...) from Y` is compiled EXACTLY as `raise X(...)`.

    Not "close" or "equivalent behaviour" -- the same IR, opcode for opcode, once the one
    field that echoes the source text for a debugger (not generated code) is normalised out.
    """
    dir_plain = tmp_path / "plain"
    dir_from = tmp_path / "from"
    dir_plain.mkdir()
    dir_from.mkdir()
    ir_plain = dir_plain / "out.mir"
    ir_from = dir_from / "out.mir"

    ok_plain, out_plain = compile_(
        dir_plain, _program('raise TypeError("wrapped")'), py_parser, ir_plain)
    assert ok_plain, out_plain
    ok_from, out_from = compile_(
        dir_from, _program('raise TypeError("wrapped") from prev'), py_parser, ir_from)
    assert ok_from, out_from

    doc_plain = _strip_debug_text(json.loads(ir_plain.read_text()))
    doc_from = _strip_debug_text(json.loads(ir_from.read_text()))
    assert doc_plain == doc_from


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_undefined_name_after_from_is_accepted(tmp_path, py_parser):
    """#434: Y is discarded, so an undefined name after `from` is not a refusal.

    The old #277 test demanded the opposite, to catch the Python front end building
    while the C# one refused. Both now build.
    """
    ok, out = compile_(
        tmp_path,
        "def main() -> None:\n"
        "    raise RuntimeError from totally_undefined_name_xyz\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_call_after_from_is_also_accepted(tmp_path, py_parser):
    """Unlike the raise MESSAGE position, `from` does not carve out a call refusal (#434).

    The decision is "compiled exactly as `raise X(...)`" with no exception named for a call,
    so a call after `from` is discarded exactly like every other expression there.
    """
    ok, out = compile_(
        tmp_path,
        RAISER
        + "def helper() -> uint8:\n"
        "    return 1\n\n"
        "def main() -> None:\n"
        "    try:\n"
        "        v: uint8 = f(9)\n"
        "    except ValueError:\n"
        "        raise TypeError(\"wrapped\") from helper()\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_control_an_undefined_name_in_an_ordinary_read_is_still_caught(tmp_path, py_parser):
    """Proves the name checker still works outside the discarded `from` slot."""
    ok, out = compile_(
        tmp_path,
        "def main() -> None:\n"
        "    x: uint8 = totally_undefined_name_xyz\n",
        py_parser,
    )
    assert not ok, out
    assert "is not defined" in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_raise_without_from_still_builds(tmp_path, py_parser):
    ok, out = compile_(tmp_path, _program('raise TypeError("wrapped")'), py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_cause_and_context_on_a_bound_exception_are_refused(tmp_path, py_parser):
    src = (
        RAISER
        + "def main() -> None:\n"
        "    try:\n"
        "        v: uint8 = f(9)\n"
        "    except ValueError as e:\n"
        "        x = e.__cause__\n"
    )
    ok, out = compile_(tmp_path, src, py_parser)
    assert not ok, out
    assert "__cause__" in out, out
    assert "chain" in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_context_on_a_bound_exception_is_refused_naming_the_attribute(tmp_path, py_parser):
    """`e.__context__` is refused the same way: the chain is not recorded, so there is
    nothing for either attribute to point at."""
    src = (
        RAISER
        + "def main() -> None:\n"
        "    try:\n"
        "        v: uint8 = f(9)\n"
        "    except ValueError as e:\n"
        "        x = e.__context__\n"
    )
    ok, out = compile_(tmp_path, src, py_parser)
    assert not ok, out
    assert "__context__" in out, out
    assert "not kept" in out, out
