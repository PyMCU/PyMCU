"""PyMCU#466. `import os` / `from os import uname` resolve to the stdlib stub.

uname() is a compile-time five-field record of __CHIP__, not an operating
system. listdir / getenv stay undefined -- there is still no filesystem.
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


UNAME_PROGRAM = (
    "from os import uname\n"
    "\n"
    "def main():\n"
    "    u = uname()\n"
    "    if \"Linux\" not in uname():\n"
    "        pass\n"
    "    if \"RP2350\" in uname().machine:\n"
    "        pass\n"
    "    if u.sysname == \"PyMCU\":\n"
    "        pass\n"
)


@BOTH_FRONT_ENDS
def test_from_os_import_uname_builds(tmp_path, py_parser):
    proc, _ = frontend(tmp_path, UNAME_PROGRAM, py_parser=py_parser)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@BOTH_FRONT_ENDS
def test_import_os_uname_builds(tmp_path, py_parser):
    proc, _ = frontend(
        tmp_path,
        "import os\n"
        "\n"
        "def main():\n"
        "    if \"Linux\" not in os.uname():\n"
        "        pass\n"
        "    if os.name == \"posix\":\n"
        "        pass\n",
        py_parser=py_parser,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


def test_uname_machine_on_atmega_is_the_chip_name(tmp_path):
    proc, ir = frontend(
        tmp_path,
        "from os import uname\n"
        "\n"
        "def main():\n"
        "    if uname().machine == \"atmega328p\":\n"
        "        pass\n",
        chip="atmega328p",
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert "atmega328p" in ir


def test_listdir_is_still_undefined(tmp_path):
    proc, _ = frontend(
        tmp_path,
        "from os import listdir\n"
        "\n"
        "def main():\n"
        "    listdir()\n",
    )
    combined = proc.stdout + proc.stderr
    assert "[BUILD_FAIL]" in proc.stdout, combined
    assert "listdir" in combined.lower()
