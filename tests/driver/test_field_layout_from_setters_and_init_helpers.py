"""PyMCU#441: a field first assigned outside __init__ is a real field on both front ends.

DeriveFieldLayout (Scan.cs) used to derive a class's field layout from __init__ alone, so a
field first assigned from a property setter (adafruit_tcs34725's integration_time.setter sets
self._integration_time) or from a plain method __init__ calls directly (adafruit_motor.servo's
__init__ calls set_pulse_width_range, which sets self._min_duty) was refused as "not a field".

Measured against CPython 3.12.12, real MicroPython v1.21.0 and real CircuitPython 9.2.1 (issue
body): a field set only from such a method is exactly as real as one set in __init__. PyMCU
still diverges from all three in two ways, by design:

  * a field READ somewhere and WRITTEN nowhere in the class is a compile-time error shaped like
    the AttributeError the interpreters would raise at run time (test_*_still_refused below);
  * a field's type is fixed at its first writing site, and a LATER write of an incompatible
    type is a located compile error, even though the interpreters place no such restriction
    (test_a_later_incompatible_type_is_refused).

Every case here runs on BOTH front ends (the hand-written parser and the CPython AST bridge),
because a divergence in what compiles is a miscompile waiting to happen on whichever side
nobody is looking at (see test_frontend_diagnostic_parity.py's header for the same argument).
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
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

BOTH_FRONT_ENDS = pytest.mark.parametrize("py_parser", [False, True], ids=["cs", "bridge"])


def _compile(tmp_path: Path, source: str, py_parser: bool):
    """(ok, stderr). Never asserts on its own, so a test can demand either outcome."""
    src = tmp_path / "main.py"
    src.write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", "atmega328p",
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(tmp_path)],
        capture_output=True, text=True, env=env,
    )
    return proc.returncode == 0, proc.stderr


HEADER = "from pymcu.types import uint8\n\n"


@BOTH_FRONT_ENDS
def test_a_field_from_a_property_setter_compiles(tmp_path, py_parser):
    """The adafruit_tcs34725 shape: the setter is the ONLY site that assigns the field."""
    ok, err = _compile(tmp_path, HEADER + """
class Sensor:
    def __init__(self, raw: uint8):
        self._raw: uint8 = raw

    @property
    def offset(self) -> uint8:
        return self._offset

    @offset.setter
    def offset(self, val: uint8):
        self._offset = val

def main():
    s = Sensor(10)
    s.offset = 5
    x: uint8 = s.offset
""", py_parser)

    assert ok, err


@BOTH_FRONT_ENDS
def test_a_field_from_a_method_called_by_init_compiles(tmp_path, py_parser):
    """The adafruit_motor.servo shape: __init__ delegates its own setup to a helper method."""
    ok, err = _compile(tmp_path, HEADER + """
class Servo:
    def __init__(self, raw: uint8):
        self._raw: uint8 = raw
        self.configure(raw)

    def configure(self, raw: uint8):
        self._min_duty = raw

def main():
    s = Servo(10)
    x: uint8 = s._min_duty
""", py_parser)

    assert ok, err


@BOTH_FRONT_ENDS
def test_a_typo_in_an_unrelated_method_is_still_refused(tmp_path, py_parser):
    """The typo-safety net this feature must not reopen.

    `update` is neither a property setter nor called from __init__, so a misspelled write there
    stays a compile error on both front ends, exactly as it was before #441.
    """
    ok, err = _compile(tmp_path, HEADER + """
class Sensor:
    def __init__(self, raw: uint8):
        self.temperature: uint8 = raw

    def update(self, raw: uint8):
        self.tempreature = raw

def main():
    s = Sensor(1)
    s.update(2)
""", py_parser)

    assert not ok
    assert "'Sensor' has no field 'tempreature'" in err, err


@BOTH_FRONT_ENDS
def test_a_field_read_and_never_written_is_refused_like_an_attributeerror(tmp_path, py_parser):
    """Measured (issue #441): CPython/MicroPython/CircuitPython all raise
    `AttributeError: 'C' object has no attribute 'x'` for this program. PyMCU refuses it at
    compile time instead, worded to say plainly that this is the same error.
    """
    ok, err = _compile(tmp_path, HEADER + """
class C:
    def method(self):
        pass

def main():
    c = C()
    x: uint8 = c.x
""", py_parser)

    assert not ok
    assert "'C' object has no attribute 'x'" in err, err
    assert "AttributeError" in err, err


@BOTH_FRONT_ENDS
def test_a_later_incompatible_type_is_refused(tmp_path, py_parser):
    """A PyMCU design choice, not interpreter fidelity: measured probe (c) shows CPython,
    MicroPython and CircuitPython all let a field change type freely across writes.
    """
    ok, err = _compile(tmp_path, HEADER + """
class C:
    def __init__(self):
        self.v: uint8 = 5

    def change(self):
        self.v = "hello"

def main():
    c = C()
    c.change()
""", py_parser)

    assert not ok
    assert "'v'" in err, err


@BOTH_FRONT_ENDS
def test_both_front_ends_agree_on_the_message(tmp_path, py_parser):
    """The message text itself, not only the verdict -- a divergence in wording breaks anything
    matching on it, the same argument test_frontend_diagnostic_parity.py makes.
    """
    def message(py_parser_: bool) -> str:
        ok, err = _compile(tmp_path, HEADER + """
class C:
    def method(self):
        pass

def main():
    c = C()
    x: uint8 = c.x
""", py_parser_)
        assert not ok
        line = next(l for l in err.splitlines() if "error:" in l)
        return line.split("error: ", 1)[1]

    assert message(False) == message(True)
