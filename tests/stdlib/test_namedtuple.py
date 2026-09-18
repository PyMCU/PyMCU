"""namedtuple is a compile-time class factory.

`from collections import namedtuple` binds the stub in pymcu/collections.py,
and a module-level `Name = namedtuple(...)` becomes a ZCA class. That is the
shape adafruit_irremote writes for IRMessage / UnparseableIRMessage.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

BOTH_FRONT_ENDS = pytest.mark.parametrize("py_parser", [False, True], ids=["cs", "bridge"])


def frontend(tmp_path: Path, source: str, py_parser: bool = False, chip: str = "atmega328p"):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", os.devnull, "--arch", "avr",
         "--target", chip, "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    ir = mir.read_text() if mir.exists() else ""
    return proc, ir


POINT_PROGRAM = (
    "from collections import namedtuple\n"
    "\n"
    "Point = namedtuple(\"Point\", (\"x\", \"y\"))\n"
    "\n"
    "def main():\n"
    "    p = Point(3, 5)\n"
    "    q = Point(x=7, y=9)\n"
    "    if isinstance(p, Point):\n"
    "        pass\n"
    "    if p.x == 3:\n"
    "        pass\n"
    "    if q.y == 9:\n"
    "        pass\n"
)


@BOTH_FRONT_ENDS
def test_from_collections_import_namedtuple_builds(tmp_path, py_parser):
    proc, _ = frontend(tmp_path, POINT_PROGRAM, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@BOTH_FRONT_ENDS
def test_import_collections_namedtuple_builds(tmp_path, py_parser):
    proc, _ = frontend(
        tmp_path,
        "import collections\n"
        "\n"
        "Point = collections.namedtuple(\"Point\", (\"x\", \"y\"))\n"
        "\n"
        "def main():\n"
        "    p = Point(3, 5)\n"
        "    if p.x == 3:\n"
        "        pass\n",
        py_parser=py_parser,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@BOTH_FRONT_ENDS
def test_adafruit_irremote_shapes_build(tmp_path, py_parser):
    proc, _ = frontend(
        tmp_path,
        "from collections import namedtuple\n"
        "\n"
        "IRMessage = namedtuple(\"IRMessage\", (\"pulses\", \"code\"))\n"
        "UnparseableIRMessage = namedtuple(\"IRMessage\", (\"pulses\", \"reason\"))\n"
        "NECRepeatIRMessage = namedtuple(\"NECRepeatIRMessage\", (\"pulses\",))\n"
        "\n"
        "def main():\n"
        "    n = NECRepeatIRMessage(1)\n"
        "    u = UnparseableIRMessage(1, reason=2)\n"
        "    m = IRMessage(1, code=3)\n"
        "    if isinstance(m, IRMessage):\n"
        "        pass\n"
        "    if u.reason == 2:\n"
        "        pass\n"
        "    if n.pulses == 1:\n"
        "        pass\n",
        py_parser=py_parser,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
