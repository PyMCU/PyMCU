"""`from time import struct_time` resolves to the stdlib stub.

Adafruit RTC drivers import it in a try used only for typing. pymcu.time
exists, so that try does not raise ImportError and the name has to be here.
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


def frontend(tmp_path: Path, source: str, py_parser: bool = False):
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
         "--target", "atmega328p", "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    return proc


@BOTH_FRONT_ENDS
def test_from_time_import_struct_time_builds(tmp_path, py_parser):
    proc = frontend(
        tmp_path,
        "from time import struct_time\n"
        "\n"
        "def main():\n"
        "    t = struct_time(2017, 10, 29, 15, 14, 15, 0, -1, -1)\n"
        "    if t.tm_year == 2017:\n"
        "        pass\n",
        py_parser=py_parser,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@BOTH_FRONT_ENDS
def test_try_import_struct_time_like_adafruit_rtc(tmp_path, py_parser):
    proc = frontend(
        tmp_path,
        "try:\n"
        "    import typing\n"
        "    from time import struct_time\n"
        "except ImportError:\n"
        "    pass\n"
        "\n"
        "def main():\n"
        "    pass\n",
        py_parser=py_parser,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
