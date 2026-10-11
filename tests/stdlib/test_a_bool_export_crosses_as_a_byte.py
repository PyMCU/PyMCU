"""A `bool` parameter or return on a library export (`pymcu natmod`) crossed as nothing.

`bool` has no entry in the width table, so a function with a `bool` parameter is classed
with the ones that take a class instance and is expanded at each call site instead of being
compiled as a subroutine. That is right for a program (the argument folds away) and wrong
for a library export, which has no call site: the compiler stopped with "its parameter
'a: bool' is read as a class instance", a sentence about something the user never wrote.

An export is a symbol with a fixed ABI, so its `bool` is a byte: 0 or 1 in, 0 or 1 out. The
adapter that boxes the result normalises it (mp_obj_is_true / mp_obj_new_bool).
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
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("source", [
    pytest.param("def F(a: bool) -> bool:\n    return not a\n", id="param-and-return"),
    pytest.param("def F(a: bool) -> int:\n    if a:\n        return 1\n    return 0\n", id="param-only"),
    pytest.param("def F(a: int) -> bool:\n    return a > 3\n", id="return-only"),
])
def test_a_bool_on_an_export_is_a_byte(tmp_path, py_parser, source):
    proc, ir = build(tmp_path, source, py_parser)

    assert proc.returncode == 0, proc.stderr
    assert any(f["name"] == "F" for f in ir["functions"]), "F must be a subroutine of its own"
