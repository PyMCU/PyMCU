"""`PI: const[float] = 3.14159` at module level is a constant, not a variable.

A module-level float without `const` is storage: `cm.PI` is a global that the module's
`__module_init` writes at start-up. That is right for a value a program changes and wrong for
`math.pi`: it costs RAM and a store in every program that imports the module, it reads 0.0
anywhere the init has not run (a `--library` build, which is what `pymcu natmod` is, has no
`main` to call it), and a native module may not carry writable data at all.

`const[float]` folds like `const[uint8]` does: every read, from the module itself, through
`mod.PI`, and through `from mod import PI`, is the float, and nothing is allocated.
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

MODULE = (
    "PI: const[float] = 3.14159265\n"
    "NEG: const[float] = -2.5\n"
    "INF: const[float] = float('inf')\n"
    "\n"
    "def area(r: float) -> float:\n"
    "    return PI * r * r\n"
)

MAIN = {
    "member": "import cm\n\ndef F(x: float) -> float:\n    return x * cm.PI\n",
    "from-import": "from cm import PI\n\ndef F(x: float) -> float:\n    return x * PI\n",
    "inside-the-module": "import cm\n\ndef F(x: float) -> float:\n    return cm.area(x)\n",
    "negative": "import cm\n\ndef F(x: float) -> float:\n    return x * cm.NEG\n",
}


def build(tmp_path: Path, main: str, py_parser: bool):
    (tmp_path / "cm.py").write_text(MODULE)
    (tmp_path / "main.py").write_text(main)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.asm"),
         "--arch", "arm", "--target", "rp2040", "--freq", "125000000", "--library",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def float_operands(ir) -> list[float]:
    out = []

    def walk(n):
        if isinstance(n, dict):
            if n.get("$t") == "fconst":
                out.append(n["value"])   # Infinity and NaN are serialised as strings
            for v in n.values():
                walk(v)
        elif isinstance(n, list):
            for v in n:
                walk(v)
    walk(ir["functions"])
    return out


@pytest.mark.parametrize("py_parser", FRONTENDS)
@pytest.mark.parametrize("case, value", [
    ("member", 3.14159265), ("from-import", 3.14159265),
    ("inside-the-module", 3.14159265), ("negative", -2.5),
])
def test_a_const_float_folds_into_every_read_and_allocates_nothing(tmp_path, py_parser, case, value):
    proc, ir = build(tmp_path, MAIN[case], py_parser)

    assert proc.returncode == 0, proc.stderr
    assert ir["globals"] == [], "a constant has no storage"
    assert any(abs(float(v) - value) < 1e-9 for v in float_operands(ir)), \
        f"the float {value} must appear as an operand, not be read from a variable"
    assert not any(f["name"].endswith("__module_init") for f in ir["functions"]), \
        "nothing to initialise"


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_infinity_is_a_constant_too(tmp_path, py_parser):
    proc, ir = build(tmp_path, "import cm\n\ndef F(x: float) -> float:\n    return x + cm.INF\n", py_parser)

    assert proc.returncode == 0, proc.stderr
    assert ir["globals"] == []
    assert any(float(v) == float("inf") for v in float_operands(ir))
