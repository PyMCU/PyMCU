"""PyMCU#466. `import os` / `from os import uname` resolve to the stdlib stub.

uname() is a compile-time five-field record of __CHIP__, not an operating
system. getenv stays undefined -- there is still no process environment. stat()
and listdir() exist over the romfs table only (RFC 0008): the files the driver
embedded at build time, answered by the compiler as a compile-time tuple.
"""

import json
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


def frontend(tmp_path: Path, source: str, py_parser: bool = False, chip: str = "atmega328p",
             embeds: dict[str, Path] | None = None):
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    cmd = [str(PYMCUC), str(src), "-o", os.devnull, "--arch", "avr",
           "--target", chip, "--freq", "16000000", "-I", str(STDLIB),
           "--emit-ir", str(mir)]
    for name, path in (embeds or {}).items():
        cmd += ["--embed", f"{name}={path}"]
    proc = subprocess.run(cmd, capture_output=True, text=True, env=env)
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


def test_listdir_reads_the_romfs_table(tmp_path):
    """RFC 0008: listdir() is a compile-time tuple of the names embedded at build time.

    There is still no filesystem -- the compiler answers from the --embed table.
    With "boot.txt" and "docs/other.txt" embedded, listdir("/") is
    ("boot.txt", "docs"), so its len() folds to 2 and the membership test folds
    to true, leaving the constant 2 as the only store into GPIOR1 (0x4A).
    """
    boot = tmp_path / "boot.txt"
    boot.write_text("x")
    other = tmp_path / "other.txt"
    other.write_text("y")
    proc, ir = frontend(
        tmp_path,
        "import os\n"
        "from pymcu.chips.atmega328p import GPIOR1\n"
        "\n"
        "def main():\n"
        "    names = os.listdir(\"/\")\n"
        "    n: int = len(names)\n"
        "    if \"boot.txt\" in names:\n"
        "        GPIOR1.value = n\n"
        "    while True:\n"
        "        pass\n",
        embeds={"boot.txt": boot, "docs/other.txt": other},
    )
    combined = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, combined
    stores = json.loads(ir)["functions"]
    main = next(f for f in stores if f["name"] == "main")
    writes = [i["src"]["value"] for i in main["body"]
              if i.get("$t") == "copy" and i["dst"].get("$t") == "mem"
              and i["dst"].get("address") == 0x4A and i["src"].get("$t") == "const"]
    assert writes == [2], f"expected the folded count 2 stored into GPIOR1, got {writes}"


def test_getenv_is_still_undefined(tmp_path):
    proc, _ = frontend(
        tmp_path,
        "from os import getenv\n"
        "\n"
        "def main():\n"
        '    getenv("X")\n',
    )
    combined = proc.stdout + proc.stderr
    assert "[BUILD_FAIL]" in proc.stdout, combined
    assert "getenv" in combined.lower()
