"""A diagnostic about an inlined body names the file and line of THAT body.

A `for`-in over a parameter annotated `str` rather than `const[str]` was reported against the
caller. Two shapes, both wrong in different ways:

    @inline METHOD in another module    main.py:6    the call site, correct code, and the file
                                                     that needs the edit never named
    plain or @inline FUNCTION           main.py:11   in a seven-line main.py: the callee's line
                                                     against the caller's name, a location that
                                                     does not exist

This is issue #164, and the entry-file half of the same family as #178. The machinery was
already there: `CompilerError.File` and `UserError` attaching it. What was missing is that
`currentSourcePath` is set once per top-level function and never switched while an @inline
body from another module is expanded.

The other direction matters as much and is easy to break while fixing this one. An
author-written `raise CompileError` in a HAL dispatcher is ABOUT THE CALLER: `LCD(rs="PA0")`
is a pin name the caller has to change, and pointing at the `raise` inside the driver sends
the reader to a file they cannot fix. Those must stay on the call. An intermediate version of
the fix moved them to the driver's line while leaving the caller's file name attached, which
is the same non-existent location this issue is about, relocated: line 151 of a ten-line file.
The tests below pin both ends.
"""

import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):")


def build(tmp_path: Path, files: dict):
    for name, text in files.items():
        (tmp_path / name).write_text(text)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
    )
    return proc.stdout + proc.stderr


def location(out: str):
    """(file, line) of the first diagnostic, as the reader is shown it."""
    m = LOCATION.search(out)
    assert m, f"no located diagnostic in:\n{out}"
    return Path(m.group(1)).name, int(m.group(2))


# --- the compiler's own diagnostic: it belongs to the callee -------------------

PANEL_METHOD = (
    "from pymcu.types import uint8, inline\n\n\n"      # 1-3
    "class Panel:\n\n"                                 # 4-5
    "    @inline\n"                                    # 6
    "    def __init__(self, addr: uint8):\n"           # 7
    "        self._addr = addr\n\n"                    # 8-9
    "    @inline\n"                                    # 10
    "    def print_str(self, s: str):\n"               # 11
    "        for c in s:\n"                            # 12
    "            pass\n"                               # 13
)

MAIN_METHOD = (
    "from panel import Panel\n\n\n"
    "def main():\n"
    "    p = Panel(0x3C)\n"
    '    p.print_str("Hi!")\n'
    "    while True:\n"
    "        pass\n"
)

PANEL_FUNCTION = (
    "from pymcu.types import inline\n\n\n"             # 1-3
    "@inline\n"                                        # 4
    "def shout(s: str):\n"                             # 5
    "    for c in s:\n"                                # 6
    "        pass\n"                                   # 7
)

MAIN_FUNCTION = (
    "from panel import shout\n\n\n"
    "def main():\n"
    '    shout("Hi!")\n'
    "    while True:\n"
    "        pass\n"
)


def test_a_method_in_another_module_is_named_by_its_own_file(tmp_path):
    out = build(tmp_path, {"panel.py": PANEL_METHOD, "main.py": MAIN_METHOD})
    assert location(out) == ("panel.py", 12), \
        "the annotation to change is panel.py:12; main.py:6 is a correct line"


def test_a_function_in_another_module_is_named_by_its_own_file(tmp_path):
    out = build(tmp_path, {"panel.py": PANEL_FUNCTION, "main.py": MAIN_FUNCTION})
    assert location(out) == ("panel.py", 6)


@pytest.mark.parametrize("panel,main", [(PANEL_METHOD, MAIN_METHOD),
                                        (PANEL_FUNCTION, MAIN_FUNCTION)],
                         ids=["method", "function"])
def test_the_line_belongs_to_the_file_that_is_named(tmp_path, panel, main):
    """The property underneath both: a location has to exist.

    Reporting a line of one file against the name of another is what #178 removed for
    imported modules, and it is what the function shape did here.
    """
    out = build(tmp_path, {"panel.py": panel, "main.py": main})
    name, line = location(out)
    text = (tmp_path / name).read_text().splitlines()
    assert line <= len(text), f"{name} has {len(text)} lines; the diagnostic claims line {line}"
    assert "for c in s" in text[line - 1], \
        f"{name}:{line} is {text[line - 1]!r}, not the loop the message is about"


# --- an author's own guard: it belongs to the caller ---------------------------

def test_a_raise_in_a_hal_dispatcher_stays_on_the_call(tmp_path):
    """`LCD(rs="PA0")` is the caller's mistake, in the caller's file.

    The driver's `raise CompileError` is deliberate and its message is about the argument, so
    the reader has to be sent to the line holding the argument. Moving these to the driver
    would name a file the reader cannot fix.
    """
    out = build(tmp_path, {"main.py":
        "from pymcu.drivers.lcd import LCD\n\n\n"
        "def main():\n"
        '    lcd = LCD(rs="PA0", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")\n'
        "    lcd.init()\n"
        "    while True:\n"
        "        pass\n"})
    name, line = location(out)
    assert name == "main.py", f"the pin to change is in main.py, not {name}"
    # init() is where the pin is first driven, so that is the line the guard fires on.
    # Either of the caller's two lines is right; a line inside the driver is not.
    assert line in (5, 6), f"expected a line of main.py, got {line}"


@pytest.mark.parametrize("driver,call,use", [
    ("pymcu.drivers.ds18b20", 'DS18B20("PB4")', "    v: int16 = d.read()\n"),
    ("pymcu.drivers.dht11", 'DHT11("PB0")', "    v: uint16 = d.read()\n"),
    ("pymcu.hal.servo", 'Servo("PB3")', "    d.write(90)\n"),
], ids=["ds18b20", "dht11", "servo"])
def test_every_pin_guard_stays_on_the_call(tmp_path, driver, call, use):
    """The guard fires where the pin is first DRIVEN, which for the sensors is the read."""
    cls = call.split("(")[0]
    out = build(tmp_path, {"main.py":
        f"from {driver} import {cls}\n\n\n"
        "def main():\n"
        f"    d = {call}\n"
        + use +
        "    while True:\n"
        "        pass\n"})
    name, _ = location(out)
    assert name == "main.py", f"{cls}'s pin guard is about the caller's argument, got {name}"
