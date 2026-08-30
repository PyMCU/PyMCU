"""A refusal about the caller's argument names the caller's FILE, which is not always main.py.

The twin of the "stays on the call" tests in `test_driver_pin_validation`,
`test_refused_argument_carets`, `test_diagnostic_names_the_callee` and
`test_module_diagnostic_file`. Every one of those writes the call in the entry file, so none of
them can tell "the caller" from "the entry file", and the two were the same answer by
coincidence.

Move the call one module out and they separate. Measured before the fix, with the pin written
on mid.py:5:

    main.py:5:1: error: CompileError: Servo: unsupported pin ...
    5 |     spin()

`main.py:5` is `spin()`, a line that mentions no pin, in a file that contains none. Both files
have a line 5, which is what made it read as correct.

The behaviour those tests pin is right and stays: an author-written `raise CompileError` in a
driver is about the caller's argument, and the reader has to be sent to the line holding it.
What was missing is the other half. The line came from the caller and the file came from the
fallback, which is the same two-halves defect #227 removed for the `UserError` path, in the one
place that reports the caller on purpose. Issue #230.

WHAT DISCRIMINATES: every test in this file. Against the unfixed compiler each names main.py.
The entry-file spellings are NOT duplicated here; they are already covered, and they have to
keep passing unchanged, which is what says the fix did not simply move the mistake.
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

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):(\d+)")

# Each body goes into mid.py under `def go():`, and main.py calls go(). Both files are laid out
# so the call in mid.py and the call in main.py sit on DIFFERENT lines: with the same line
# number in both, a fix that names the wrong file still reads as correct.
MID = (
    "{imports}"                    # 1
    "\n\n"                         # 2-3
    "def go():\n"                  # 4
    "{body}"                       # 5 onwards
)

MAIN = (
    "from mid import go\n"         # 1
    "\n\n"                         # 2-3
    "def main():\n"                # 4
    "    go()\n"                   # 5
    "    while True:\n"            # 6
    "        pass\n"               # 7
)

# (id, imports for mid.py, body of go(), a pin the driver does not drive)
DRIVERS = [
    ("ds18b20", "from pymcu.types import int16\nfrom pymcu.drivers.ds18b20 import DS18B20\n",
     '    s = DS18B20("PB4")\n    v: int16 = s.read()\n'),
    ("dht11", "from pymcu.types import uint16\nfrom pymcu.drivers.dht11 import DHT11\n",
     '    s = DHT11("PB0")\n    v: uint16 = s.read()\n'),
    ("servo", "from pymcu.hal.servo import Servo\n",
     '    s = Servo("PB3")\n    s.write(90)\n'),
    ("lcd", "from pymcu.drivers.lcd import LCD\n",
     '    lcd = LCD(rs="PA0", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")\n'
     "    lcd.init()\n"),
    ("pwm", "from pymcu.hal.pwm import PWM\n",
     '    p = PWM("PC0", 128)\n    p.set_duty(64)\n'),
    ("neopixel", "from pymcu.drivers.neopixel import NeoPixel\n",
     '    np = NeoPixel("PC0", 1)\n    np.set_pixel(1, 2, 3)\n'),
]


def build(tmp_path: Path, files: dict, py_parser: bool = False):
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
    m = LOCATION.search(out)
    assert m, f"no located diagnostic in:\n{out}"
    return Path(m.group(1)).name, int(m.group(2)), int(m.group(3))


@pytest.mark.parametrize("name,imports,body", DRIVERS, ids=[d[0] for d in DRIVERS])
def test_a_pin_guard_names_the_module_the_call_is_written_in(tmp_path, name, imports, body):
    out = build(tmp_path, {"mid.py": MID.format(imports=imports, body=body), "main.py": MAIN})
    file, line, _ = location(out)
    assert file == "mid.py", \
        f"the pin is written in mid.py and the report names {file}"
    text = (tmp_path / file).read_text().splitlines()
    assert line <= len(text), f"mid.py has {len(text)} lines; the diagnostic claims line {line}"
    # One of the two statements of `go()`. Which one depends on the driver: the sensors
    # validate the pin where it is first DRIVEN, which is the read rather than the constructor,
    # and the original entry-file tests record the same thing.
    assert text[line - 1] in body.splitlines(), \
        f"mid.py:{line} is {text[line - 1]!r}, which is not a line of the call"


def test_the_python_front_end_agrees(tmp_path):
    name, imports, body = DRIVERS[2]        # servo
    out = build(tmp_path, {"mid.py": MID.format(imports=imports, body=body), "main.py": MAIN},
                py_parser=True)
    file, _, _ = location(out)
    assert file == "mid.py", f"the Python front end names {file}"


def test_a_const_parameter_refused_two_modules_out_names_the_middle_one(tmp_path):
    """Not a driver: the same shape with an @inline of one's own, so it is the mechanism being
    tested rather than one library's guard."""
    helper = (
        "from pymcu.types import uint8, const, inline\n"     # 1
        "from pymcu.exceptions import CompileError\n"        # 2
        "\n\n"                                               # 3-4
        "@inline\n"                                          # 5
        "def only_seven(n: const[uint8]) -> uint8:\n"        # 6
        "    if n != 7:\n"                                   # 7
        '        raise CompileError("only_seven: pass 7")\n' # 8
        "    return n\n"                                     # 9
    )
    mid = (
        "from pymcu.types import uint8\n"                    # 1
        "from helper import only_seven\n"                    # 2
        "\n\n"                                               # 3-4
        "def go():\n"                                        # 5
        "    x: uint8 = only_seven(3)\n"                     # 6
    )
    out = build(tmp_path, {"helper.py": helper, "mid.py": mid, "main.py": MAIN})
    file, line, _ = location(out)
    assert file == "mid.py", f"the call to change is in mid.py, got {file}"
    text = (tmp_path / file).read_text().splitlines()
    assert "only_seven(3)" in text[line - 1], \
        f"mid.py:{line} is {text[line - 1]!r}, not the call"
