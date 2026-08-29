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

A third end, added with #227: the window BEFORE the callee's first statement. Argument binding
runs after the file label moved and before any callee line existed, so nine refusals reported
the caller's line under the callee's file name, and four of them drew a caret on it. Those are
about the CALL, so they name the caller, both halves. The one exception is a parameter's
DEFAULT value, which is text in the callee's file; the test that pins it is what separates
repairing the pair from inverting it.
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

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):")


def build(tmp_path: Path, files: dict, py_parser: bool = False):
    """The Python front end is a separate run of the same binary.

    A location is produced by the front end and consumed by IR generation, so a diagnostic
    that is right through one parser and wrong through the other is two bugs wearing one
    message. The window tests below run through both.
    """
    for name, text in files.items():
        (tmp_path / name).write_text(text)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
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


# --- the same family, in the window before the callee's first statement --------

# Long enough that the caller's line numbers are IN RANGE inside the callee too. With a short
# helper the defect showed as a line past the end of the file, which any bounds check catches.
# Padded to overlap the caller it reported helper.py:7, a blank line, while every call below is
# on main.py:7. The same number in the wrong file is the form that reads as correct.
WINDOW_HELPER = (
    "from pymcu.types import uint8, const, inline\n"     # 1
    "\n\n"                                               # 2-3
    "@inline\n"                                           # 4
    "def hold(n: const[uint8]) -> uint8:\n"               # 5
    "    return n\n"                                      # 6
    "\n\n"                                               # 7-8
    "@inline\n"                                           # 9
    "def one(a: uint8) -> uint8:\n"                       # 10
    "    return a\n"                                      # 11
    "\n\n"                                               # 12-13
    "@inline\n"                                           # 14
    "def holdstr(s: const[str]) -> uint8:\n"              # 15
    "    return 1\n"                                      # 16
    "\n\n"                                               # 17-18
    "@inline\n"                                           # 19
    "def holdc(c: const) -> uint8:\n"                     # 20
    "    return 1\n"                                      # 21
    "\n\n"                                               # 22-23
    "@inline\n"                                           # 24
    "def defc(n: const[uint8] = 0) -> uint8:\n"           # 25
    "    return n\n"                                      # 26
)


def window_main(call: str) -> str:
    """Every call sits on line 7, and helper.py:7 is a blank line."""
    return (
        "from pymcu.types import uint8\n"                       # 1
        "from helper import hold, one, holdstr, holdc, defc\n"  # 2
        "\n\n"                                                 # 3-4
        "def main():\n"                                         # 5
        "    v: uint8 = 3\n"                                    # 6
        f"    {call}\n"                                         # 7
        "    while True:\n"                                     # 8
        "        pass\n"                                        # 9
    )


# Every refusal the argument-binding loop can raise. Before the fix all nine reported
# helper.py:7, a blank line in a file the reader did not write, and the four that pass a node
# drew a caret at column 16 of it.
WINDOW_CALLS = [
    ("too many arguments",          "x: uint8 = one(v, v)"),
    ("const[uint8] not constant",   "x: uint8 = hold(v)"),
    ("const[str] not constant",     "x: uint8 = holdstr(v)"),
    ("bare const not constant",     "x: uint8 = holdc(v)"),
    ("keyword const[str]",          "x: uint8 = holdstr(s=v)"),
    ("keyword const",               "x: uint8 = holdc(c=v)"),
    ("unknown keyword",             "x: uint8 = one(zz=v)"),
    ("missing argument",            "x: uint8 = one()"),
]


@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
@pytest.mark.parametrize("call", [c for _, c in WINDOW_CALLS],
                         ids=[i for i, _ in WINDOW_CALLS])
def test_a_refusal_raised_while_binding_names_the_call(tmp_path, call, py_parser):
    """The property `test_the_line_belongs_to_the_file_that_is_named` pins, one step earlier.

    That test covers a diagnostic raised while the callee's BODY is lowered, and by then the
    line and the file both belong to the callee. An argument is bound before any of the body
    is walked, so the pair had not been made coherent yet: the file label had moved and the
    line had not.

    The reported site is the CALL, which is the line the reader can change: they wrote
    `hold(v)` and the fix is to pass a constant. Same reasoning as the `raise` in a HAL
    dispatcher above, and as recorded at `ControlFlow.cs:1677`.
    """
    out = build(tmp_path, {"helper.py": WINDOW_HELPER, "main.py": window_main(call)},
                py_parser)
    name, line = location(out)
    text = (tmp_path / name).read_text().splitlines()
    assert line <= len(text), f"{name} has {len(text)} lines; the diagnostic claims line {line}"
    assert name == "main.py", f"the call to change is in main.py, got {name}"
    assert text[line - 1].strip() == call, \
        f"{name}:{line} is {text[line - 1]!r}, which is not the call"


def test_the_caret_lands_on_the_call_and_not_past_the_end_of_a_line(tmp_path):
    """A bounds check is not enough, which is how this survived without one.

    helper.py:7 is blank, so a caret at column 16 pointed past the end of a line that has no
    columns at all. The caret has to land inside the text of the line that is named.
    """
    out = build(tmp_path, {"helper.py": WINDOW_HELPER,
                           "main.py": window_main("x: uint8 = one(v, v)")})
    m = re.search(r"([A-Za-z0-9_./-]+\.py):(\d+):(\d+):", out)
    assert m, out
    name, line, col = Path(m.group(1)).name, int(m.group(2)), int(m.group(3))
    text = (tmp_path / name).read_text().splitlines()[line - 1]
    assert col <= len(text), f"caret at column {col} of a {len(text)}-character line"
    assert text[col - 1:].startswith("one"), \
        f"the caret is on {text[col - 1:][:12]!r}, not on the call"


# --- the other half: a default value is the CALLEE's code ----------------------

def test_a_refusal_inside_a_default_value_names_the_callee(tmp_path):
    """The invariant that separates repairing the pair from inverting it.

    Argument binding runs under the caller's location because the nodes it holds are the
    caller's. A parameter's default value is the one exception: it is text in the callee's
    file. Deferring the file switch without excepting it moves this diagnostic to
    `main.py:5`, which is `def main():`, so the pair breaks the other way round.

    Measured against a build carrying the deferral WITHOUT the exception: helper.py:5:18
    became main.py:5:18.
    """
    helper = (
        "from pymcu.types import uint8, inline\n"   # 1
        "\n\n"                                      # 2-3
        "@inline\n"                                  # 4
        "def f(n: uint8 = nope) -> uint8:\n"         # 5
        "    return n\n"                             # 6
    )
    main = (
        "from pymcu.types import uint8\n"
        "from helper import f\n"
        "\n\n"
        "def main():\n"
        "    x: uint8 = f()\n"
        "    while True:\n"
        "        pass\n"
    )
    out = build(tmp_path, {"helper.py": helper, "main.py": main})
    name, line = location(out)
    assert name == "helper.py", f"the default value is written in helper.py, got {name}"
    text = (tmp_path / name).read_text().splitlines()
    assert "nope" in text[line - 1], \
        f"{name}:{line} is {text[line - 1]!r}, not the default value the message is about"
