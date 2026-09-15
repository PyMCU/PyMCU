"""PyMCU#417. A name bound only under a guarded import (`try: ... except ImportError: pass`
or `if TYPE_CHECKING:`) is accepted in an annotation like `Optional` already is, and a real
read of the value is refused by its OWN name -- not, as before #417, a neighboring
parameter's, because the inline-call argument binder checked the wrong parameter's type
(`func.Params[i]` instead of the self-offset index).

`adafruit_register/i2c_bits.py`'s `RWBits.__set__(self, obj, value)` is the shape that found
it: `obj` (past `self`) carries the guarded annotation, `value` does not. `circuitpython_typing`
is never put on the include path here, so its import genuinely fails to resolve on this
target -- exactly like on a real board -- and the try folds to its handler.

Both front ends (the hand-written C# parser and the CPython-AST bridge) must agree, because
`ConditionalCompilator`'s guard folding and the argument binder it feeds are both shared
code paths reached identically from either parse.
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

# The exact adafruit_register/i2c_bits.py shape, trimmed to what the bug needs: `obj` (past
# `self`) carries the guarded name, `value` (the next parameter) does not.
RWBITS = """\
try:
    from typing import Optional, Type
    from circuitpython_typing.device_drivers import I2CDeviceDriver
except ImportError:
    pass


class RWBits:
    def __init__(self, lowest_bit):
        self.lowest_bit = lowest_bit

    def __set__(self, obj: I2CDeviceDriver, value: int) -> None:
        {body}
"""


def _compile(tmp_path: Path, body: str, py_parser: bool):
    """(ok, stderr). Never asserts on its own, so a test can demand either outcome."""
    src = tmp_path / "main.py"
    src.write_text(
        RWBITS.format(body=body)
        + "\n\nr = RWBits(2)\n\n\ndef main():\n    r.__set__(0, 5)\n"
    )
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


@BOTH_FRONT_ENDS
def test_the_guarded_annotation_is_accepted_and_the_other_parameter_still_works(
    tmp_path, py_parser
):
    # `value` (a plain int, the parameter AFTER the guarded `obj`) is read and must compile
    # normally -- before #417 this read the one that was refused, misattributing obj's
    # annotation to it via an off-by-one.
    ok, err = _compile(tmp_path, "value <<= self.lowest_bit", py_parser)
    assert ok, f"a plain parameter past a guarded one should compile, got:\n{err}"


@BOTH_FRONT_ENDS
def test_a_real_read_of_the_guarded_parameter_is_refused_by_its_own_name(tmp_path, py_parser):
    ok, err = _compile(tmp_path, "obj.poke(self.lowest_bit, value)", py_parser)
    assert not ok, "reading a value with no concrete type must be refused"
    assert "'obj'" in err, f"the refusal must name 'obj', not a neighboring parameter:\n{err}"
    assert "I2CDeviceDriver" in err
    assert "'value'" not in err, f"the off-by-one used to blame 'value' instead:\n{err}"


@BOTH_FRONT_ENDS
def test_both_front_ends_refuse_at_the_same_parameter(tmp_path, py_parser):
    # Redundant with the two tests above taken together, but stated as the parity property
    # itself: whichever front end parsed the program, the guard folds the same way and the
    # argument binder blames the same name.
    ok, err = _compile(tmp_path, "obj.poke(self.lowest_bit, value)", py_parser)
    assert not ok
    assert "'obj'" in err
