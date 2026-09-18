"""`raise X() from e` (#434): accepted in both front ends, compiled as `raise X()`.

#277 refused the construct so a discarded Y could not swallow an undefined name silently
and unevenly between front ends. #434 reverses that: there is no traceback and no
`__cause__` on this target, so `from Y` is indistinguishable from omitting it once
compiled. Y is parsed (syntax errors still surface) and discarded, matching the
non-call raise MESSAGE (#262). Both front ends discard the same way.

Every case runs through BOTH front ends.
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
