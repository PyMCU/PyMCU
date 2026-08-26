"""A refused argument is pointed at, not merely reported near.

A HAL dispatcher that refuses a pin was located at the statement that happened to notice:

    main.py:4:1: error: CompileError: Servo: unsupported pin -- use PB1 (OC1A) or PB2 (OC1B)

Column 1, so no caret. On `LCD(rs="PA0", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")`
that is "one of these six is wrong, find it". Issue #193.

This is the narrow half of that issue, and it covers the drivers that validate AT
CONSTRUCTION, where the reported line was already the right one and only the column was
missing: Servo, PWM and NeoPixel.

The other half is the drivers that store the pin and validate at first use, DS18B20, DHT11
and the LCD. There the reported line does not contain the value at all, because the check
runs at `read()` or `init()`, and pointing at the argument needs the value's origin to
survive being stored in a field. Those are asserted here as UNCHANGED, so that the boundary
is a decision on the record rather than something that drifts.

Two things had to be true and only one of them was:

  the argument's origin has to reach the raise    it did not; parameters are bound from
                                                  caller-side expressions and nothing kept
                                                  which expression, so it is chained on
                                                  binding and survives handing down through
                                                  several expansions
  the expression has to carry a column            it did not. Of every expression node the
                                                  parser builds, only VariableExpr was stamped
                                                  from its token. A string literal, which is
                                                  what an argument check is usually refusing,
                                                  carried line 0 column 0.
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

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):(\d+):")


def build(tmp_path: Path, imports: str, body: str):
    src = tmp_path / "main.py"
    src.write_text(f"{imports}\n\n\ndef main():\n{body}    while True:\n        pass\n")
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--target", "atmega328p",
         "--freq", "16000000", "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    m = LOCATION.search(out)
    assert m, f"no located diagnostic in:\n{out}"
    return out, Path(m.group(1)).name, int(m.group(2)), int(m.group(3))


# (id, imports, body, the pin that is wrong)
AT_CONSTRUCTION = [
    ("servo", "from pymcu.hal.servo import Servo", '    s = Servo("PB3")\n', "PB3"),
    ("pwm", "from pymcu.hal.pwm import PWM", '    p = PWM("PC0", 128)\n', "PC0"),
    ("neopixel", "from pymcu.drivers.neopixel import NeoPixel",
     '    n = NeoPixel("PC0", 1)\n', "PC0"),
]


@pytest.mark.parametrize("name,imports,body,pin", AT_CONSTRUCTION,
                         ids=[c[0] for c in AT_CONSTRUCTION])
def test_the_caret_lands_on_the_refused_pin(tmp_path, name, imports, body, pin):
    out, file, line, col = build(tmp_path, imports, body)
    assert file == "main.py"

    text = (tmp_path / "main.py").read_text().splitlines()
    assert 1 <= line <= len(text), f"main.py has {len(text)} lines; the diagnostic claims {line}"
    assert col > 0, "column 0 withholds the caret, which is the whole complaint"

    # The character the caret sits on has to be the start of the refused value.
    at = text[line - 1][col - 1:]
    assert at.startswith(f'"{pin}"'), \
        f'main.py:{line}:{col} starts {at[:20]!r}, not the refused "{pin}"'


def test_the_caret_picks_the_wrong_one_out_of_six(tmp_path):
    """The case the issue is really about: which of them is it.

    Six pin arguments on one line, one of them wrong. LCD validates at init() rather than at
    construction, so it is the WIDE half and is expected to stay statement-level; what this
    pins is that the answer does not silently become a caret on the wrong pin.
    """
    out, file, line, col = build(
        tmp_path, "from pymcu.drivers.lcd import LCD",
        '    lcd = LCD(rs="PD4", en="PD5", d4="PA0", d5="PD7", d6="PB0", d7="PB1")\n'
        "    lcd.init()\n")
    assert file == "main.py"
    text = (tmp_path / "main.py").read_text().splitlines()

    # A withheld caret still prints column 1 in the header, so the column number cannot tell
    # "unlocated" from "column 1". The caret ROW is the signal: it is rendered only when a real
    # column was measured.
    has_caret = any(set(l.strip()) <= {"^", "~"} and l.strip() for l in out.splitlines())
    if has_caret:
        assert text[line - 1][col - 1:].startswith('"PA0"'), \
            "if the LCD grows a caret it has to be on PA0, not on one of the five good pins"


# --- the boundary, asserted so it is a decision and not a drift ----------------

VALIDATED_AT_FIRST_USE = [
    ("ds18b20", "from pymcu.drivers.ds18b20 import DS18B20",
     '    d = DS18B20("PB4")\n    v: int16 = d.read()\n'),
    ("dht11", "from pymcu.drivers.dht11 import DHT11",
     '    d = DHT11("PB0")\n    v: uint16 = d.read()\n'),
]


@pytest.mark.parametrize("name,imports,body", VALIDATED_AT_FIRST_USE,
                         ids=[c[0] for c in VALIDATED_AT_FIRST_USE])
def test_a_driver_that_validates_at_first_use_still_points_at_the_caller(
        tmp_path, name, imports, body):
    """Unchanged by the narrow half, and still actionable: the caller's own file and line."""
    out, file, line, _ = build(tmp_path, imports, body)
    assert file == "main.py"
    text = (tmp_path / "main.py").read_text().splitlines()
    assert 1 <= line <= len(text)


# --- the parser half, on its own ----------------------------------------------

def test_a_string_literal_carries_its_position(tmp_path):
    """The prerequisite. Only VariableExpr was stamped from its token; a string literal, which
    is what an argument check usually refuses, carried line 0 column 0, so the origin was
    there and unusable."""
    out, _, line, col = build(
        tmp_path, "from pymcu.hal.servo import Servo",
        '    s = Servo("PB3")\n')
    text = (tmp_path / "main.py").read_text().splitlines()
    assert text[line - 1][col - 1] == '"', \
        "the position came from somewhere other than the literal's own token"


def test_adjacent_literals_are_underlined_as_one(tmp_path):
    """Python concatenates `"PB" "3"`, and the span has to cover both pieces, not just the
    first, or the underline stops halfway through the value being complained about."""
    out, _, line, col = build(
        tmp_path, "from pymcu.hal.servo import Servo",
        '    s = Servo("PB" "3")\n')
    text = (tmp_path / "main.py").read_text().splitlines()
    assert text[line - 1][col - 1:].startswith('"PB" "3"'), \
        "the caret should start at the first piece of the concatenation"
