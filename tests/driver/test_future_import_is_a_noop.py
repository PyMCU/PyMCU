"""`from __future__ import X` at module level is a no-op (#452).

__future__ is a real CPython module whose only members are compiler pragmas that change how
CPython parses the SOURCE -- it binds no run-time name and enables nothing PyMCU does not
already do (PyMCU reads annotations straight from the source, unconditionally). It used to be
refused as "Module not found: __future__ -- ... install it with `pymcu install __future__`",
which no vendored library import will ever satisfy: no `pymcu install __future__` produces a
`__future__` package. Vendored libraries that support a wide CPython version range open with
this import as a matter of course -- adafruit_irremote.py is one.

Same idiom #417 already solved for a GUARDED optional import: fold the import away before
module resolution ever asks about it. This one needs no guard, so it is unconditionally
dropped rather than one branch of a compile-time choice.

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
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        # The suite runs a second pass with PYMCU_PY_PARSER set in the ambient
        # environment. Asking for the C# front end means clearing it, not merely
        # not setting it, or this case runs the Python front end and says csharp.
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(tmp_path / "out.mir")],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_future_annotations_import_is_accepted(tmp_path, py_parser):
    ok, out = compile_(
        tmp_path,
        "from __future__ import annotations\n\n"
        "def f(x: uint8) -> uint8:\n"
        "    return x + 1\n\n"
        "y: uint8 = f(3)\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_multiple_future_names_and_multiple_future_imports_are_accepted(tmp_path, py_parser):
    ok, out = compile_(
        tmp_path,
        "from __future__ import annotations\n"
        "from __future__ import division, generator_stop\n\n"
        "def f(x: uint8) -> uint8:\n"
        "    return x + 1\n\n"
        "y: uint8 = f(3)\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_old_refusal_text_is_gone(tmp_path, py_parser):
    """The discriminator: what both front ends said before the fix."""
    ok, out = compile_(
        tmp_path,
        "from __future__ import annotations\n\n"
        "y: uint8 = 1\n",
        py_parser,
    )
    assert ok, out
    assert "Module not found: __future__" not in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_unrelated_undefined_module_is_still_refused(tmp_path, py_parser):
    """The control: __future__ becoming a no-op must not make every import one."""
    ok, out = compile_(
        tmp_path,
        "from totally_undefined_module_xyz import something\n\n"
        "y: uint8 = 1\n",
        py_parser,
    )
    assert not ok, out
    assert "Module not found" in out, out
