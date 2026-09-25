"""A call to a name an imported module does not define says so, and names the module.

Issue #475 called it "modules that import but do not link": `import utime` resolves, and
`utime.localtime()` came out as

    call to undefined function 'utime_localtime' (typo, or a missing import?)

Every part of that sentence sends the reader somewhere there is nothing to find. The symbol
named is one the program never wrote; the category is wrong, because nothing is undefined that
was meant to be defined; and both suggestions are dead ends, since the spelling is right and
the import is already there and worked.

What is true is narrower and more useful: the module is here, and this name is not part of
what it provides on this chip. `utime.localtime` is absent because the part has no RTC, and
`micropython.mem_info` because there is no interpreter to report on -- decisions the layer
records in its parity allowlist, not omissions. The message that points at them has to say the
module resolved.

The list of what the module DOES define is the other half, and it is where the message can go
wrong quietly. A module's functions and its classes' methods are both filed under
`<module>_<name>`, so a filter that drops every name holding an underscore drops `sleep_ms`,
`ticks_ms` and `ticks_diff` and advertises one name out of eight. A method is recognised by
the class in front of it.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def build(tmp_path: Path, files: dict, py_parser: bool = False) -> str:
    for name, text in files.items():
        (tmp_path / name).write_text(text)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
    )
    return proc.stdout + proc.stderr


CLOCK = (
    "from pymcu.types import uint8\n\n\n"
    "PERIODIC: uint8 = 1\n\n\n"
    "def sleep_ms(ms: uint8) -> uint8:\n"
    "    return ms\n\n\n"
    "def ticks_diff(a: uint8, b: uint8) -> uint8:\n"
    "    return a - b\n\n\n"
    "class Timer:\n"
    "    def start(self) -> uint8:\n"
    "        return 1\n"
)

MAIN = (
    "import clock\n"
    "X: uint8 = 0\n\n\n"
    "def main() -> None:\n"
    "    global X\n"
    "    X = clock.localtime()\n"
)


@pytest.mark.parametrize("py_parser", [False, True], ids=["own-parser", "py-parser"])
def test_the_message_names_the_module_and_the_member(tmp_path, py_parser):
    out = build(tmp_path, {"clock.py": CLOCK, "main.py": MAIN}, py_parser)

    assert "module 'clock' does not define 'localtime'" in out, out
    assert "clock_localtime" not in out, (
        "the mangled symbol is not a name the program wrote:\n" + out)
    assert "typo, or a missing import?" not in out, (
        "the import resolved and the spelling is right, so neither suggestion applies:\n" + out)


@pytest.mark.parametrize("py_parser", [False, True], ids=["own-parser", "py-parser"])
def test_it_lists_the_module_functions_underscores_and_all(tmp_path, py_parser):
    out = build(tmp_path, {"clock.py": CLOCK, "main.py": MAIN}, py_parser)

    assert "sleep_ms" in out, "a name with an underscore is still a module function:\n" + out
    assert "ticks_diff" in out, out


@pytest.mark.parametrize("py_parser", [False, True], ids=["own-parser", "py-parser"])
def test_it_lists_the_classes_and_constants_too(tmp_path, py_parser):
    # A module can offer nothing but classes and constants -- the MicroPython layer's
    # `framebuf` is exactly that -- and a list built from functions alone comes out empty
    # on it, telling a reader who misremembered a name nothing about FrameBuffer.
    out = build(tmp_path, {"clock.py": CLOCK, "main.py": MAIN}, py_parser)

    assert "Timer" in out, "a class the module defines is something to write after it:\n" + out
    assert "PERIODIC" in out, "so is a module-level constant:\n" + out


@pytest.mark.parametrize("py_parser", [False, True], ids=["own-parser", "py-parser"])
def test_the_callables_come_before_the_constants(tmp_path, py_parser):
    # The refusal is about a CALL, so what can be called is what the reader is reaching for.
    # Sorting everything together put the ALL-CAPS constants first, and the cut at ten hid
    # the classes behind them on a module with many format constants.
    out = build(tmp_path, {"clock.py": CLOCK, "main.py": MAIN}, py_parser)

    listed = re.search(r"It does define ([^.]+)", out)
    assert listed, out
    names = [n.strip() for n in listed.group(1).split(",")]
    assert names.index("Timer") < names.index("PERIODIC"), names
    assert names.index("sleep_ms") < names.index("PERIODIC"), names


@pytest.mark.parametrize("py_parser", [False, True], ids=["own-parser", "py-parser"])
def test_it_does_not_advertise_a_class_method_as_a_module_function(tmp_path, py_parser):
    out = build(tmp_path, {"clock.py": CLOCK, "main.py": MAIN}, py_parser)

    assert "Timer_start" not in out, out
    assert re.search(r"define[^.]*\bstart\b", out) is None, (
        "Timer.start is not something to write after `clock.`:\n" + out)


def test_a_plain_undefined_function_keeps_its_own_message(tmp_path):
    # The module branch must not swallow the ordinary case, where a typo and a missing
    # import really are the two things to check.
    out = build(tmp_path, {"main.py":
                           "X: uint8 = 0\n\n\n"
                           "def main() -> None:\n"
                           "    global X\n"
                           "    X = nowhere()\n"})

    assert "call to undefined function 'nowhere'" in out, out
