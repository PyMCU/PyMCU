"""`raise X() from e` (#277): the same program was refused by one front end and BUILT by the other.

The hand-written parser stopped at the `from` and reported "Expected newline or end of block",
which names the punctuation it wanted rather than the construct it met. The CPython bridge never
read `node.cause` at all, so it built the program and dropped the clause.

The silent half is the worse one, and it is worse than "a clause was discarded": because nothing
ever read the cause, the expression after `from` was never NAME-RESOLVED. An undefined name in
that slot compiled clean, while the identical name in any ordinary read position is caught:

    raise RuntimeError from totally_undefined_name_xyz   ->  built
    x: uint8 = totally_undefined_name_xyz                ->  "name '...' is not defined"

WHY REFUSED RATHER THAN ACCEPTED-AND-DISCARDED. PyMCU discards a raise MESSAGE, so discarding a
cause looks consistent at first. It is not: the parser already refuses a CALL in the message, for
the reason that CPython evaluates it when the raise fires and discarding it would drop that
evaluation silently. `from <expr>` is that same expression position. Accepting it would have
fixed the divergence and kept the swallow.

Every case runs through BOTH front ends. Running one would have called this fixed while the
other still answered its own way, which is the divergence #196 is about.
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

# The three spellings take three different paths through both front ends: `raise X from e` and
# `raise X() from e` are separate branches, and in the bridge a bare `raise` returns before
# either. A check placed on one branch covers one third of the construct.
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
def test_both_front_ends_refuse_raise_from_and_name_the_construct(tmp_path, clause, py_parser):
    """The message, not merely the refusal.

    Replacing a punctuation message with a construct message is half the point of the fix, so
    asserting only `not ok` would pass on the old C# behaviour, which did refuse it.
    """
    ok, out = compile_(tmp_path, _program(clause), py_parser)
    assert not ok, out
    assert "'raise ... from ...' is not supported" in out, out
    assert "no traceback for a cause to attach to" in out, out
    # The discriminator: what the C# front end said before the fix.
    assert "Expected newline or end of block" not in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_undefined_name_after_from_is_not_swallowed(tmp_path, py_parser):
    """The test that fails on HEAD, and the reason this is filed as silent rather than noisy.

    On the old code the Python front end BUILT this. A reader who followed the `except E as e:`
    advice literally, deleting the binding and leaving `from e` behind, got a clean build of a
    program referencing a name that no longer existed.
    """
    ok, out = compile_(
        tmp_path,
        "def main() -> None:\n"
        "    raise RuntimeError from totally_undefined_name_xyz\n",
        py_parser,
    )
    assert not ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_control_an_undefined_name_in_an_ordinary_read_was_always_caught(tmp_path, py_parser):
    """Proves the name checker works, so the swallow above was that slot and not a general gap.

    Without this, "the cause slot is unchecked" and "names are not checked here at all" look the
    same, and only one of them is the bug that was filed.
    """
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
    """The refusal must be the `from` clause and nothing else."""
    ok, out = compile_(tmp_path, _program('raise TypeError("wrapped")'), py_parser)
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_suggestion_the_message_makes_actually_compiles(tmp_path, py_parser):
    """A refusal that recommends something is making a claim; compile it rather than assume it.

    The message says to write the raise on its own. This is that program. Measured across the
    compiler's refusals, roughly one suggestion in three does not survive being followed
    literally, so the ones written from now on carry the check with them.
    """
    ok, out = compile_(
        tmp_path,
        RAISER
        + "def main() -> None:\n"
        "    try:\n"
        "        v: uint8 = f(9)\n"
        "    except ValueError:\n"
        '        raise TypeError("wrapped")\n',
        py_parser,
    )
    assert ok, out
