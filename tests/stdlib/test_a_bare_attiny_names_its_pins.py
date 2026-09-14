"""A bare ATtiny that refuses a pin says which pins it has.

These parts have no silkscreen to number, so the HAL refuses `Pin(0)` on purpose rather
than picking a leg: guessing would land on PB5, which is RESET, and driving it needs the
RSTDISBL fuse -- after which the chip can no longer be programmed over ISP.

What it used to say was `NotImplementedError: Unsupported Pin`, four words that name
neither the chip nor a pin that would have worked. The sentence that does explain all this
was already written, in `board_pin_name`, and nothing on the digitalio path ever called it:
the CircuitPython layer hands the value straight to `Pin`, so `select_port` answered first
and answered with nothing (pymcu-circuitpython#4).

So the refusal itself is not the bug and is not being removed here. What is asserted is
that it carries the pin names, on all three bare-ATtiny families and through both front
ends, and that the naming forms which DO work are still accepted.
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

HEADER = re.compile(r"^(?P<path>[^\s:]+):(?P<line>\d+):(?P<col>\d+): error:", re.MULTILINE)


def _compile(tmp_path: Path, source: str, target: str, py_parser: bool) -> str:
    src = tmp_path / "main.py"
    src.write_text(source)
    # Inherit the environment: a stripped one silently disables the CPython front end,
    # and the failure then reads as a divergence between the two rather than a broken harness.
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    # --emit-ir, not -o: pymcuc cannot run the AVR backend itself, so asking it for a
    # firmware fails with an InternalCompilerError about the backend on every program,
    # including the ones that must build. The guard under test is in the front end, and
    # IR is the last artifact produced before the backend is handed anything.
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True, env=env,
    )
    return proc.stdout + proc.stderr


def _program(pin: str) -> str:
    return ('from pymcu.hal.gpio import Pin\n\n\n'
            'def main() -> None:\n'
            f'    led = Pin({pin}, Pin.OUT)\n'
            '    led.high()\n')


# (target, the pin names the refusal must offer)
FAMILIES = [
    ("attiny85", "PB0 to PB5"),
    ("attiny84", "PA0 to PA7"),
    ("attiny2313", "PB0 to PB7"),
]


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
@pytest.mark.parametrize("target,names", FAMILIES, ids=[f[0] for f in FAMILIES])
@pytest.mark.parametrize("pin", ["0", "'PZ9'"], ids=["a-bare-number", "a-pin-it-has-not-got"])
def test_the_refusal_names_the_pins_the_chip_has(tmp_path, target, names, pin, py_parser):
    out = _compile(tmp_path, _program(pin), target, py_parser)

    assert HEADER.search(out), f"expected a diagnostic, got:\n{out}"
    assert "Unsupported Pin" not in out, "the four-word refusal is what this replaced"
    assert names in out, f"the refusal must name the pins {target} has:\n{out}"
    assert "board.D" in out, "and the CircuitPython spelling of the same legs"


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
def test_the_bare_number_is_refused_where_it_was_written(tmp_path, py_parser):
    """The caret belongs on the argument, not on the HAL line that raised.

    `Pin(0)` is the reader's own line and the one they can change, so the diagnostic has to
    land there. This is the half that must not drift while the sentence is being improved.
    """
    out = _compile(tmp_path, _program("0"), "attiny85", py_parser)
    m = HEADER.search(out)

    assert m, out
    assert Path(m.group("path")).name == "main.py", "the reader's own file, not the HAL's"
    assert int(m.group("line")) == 5, "the Pin(0) they wrote"


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
@pytest.mark.parametrize("target,pin", [("attiny85", "'PB0'"), ("attiny84", "'PA0'"),
                                        ("attiny2313", "'PD0'")],
                         ids=["attiny85", "attiny84", "attiny2313"])
def test_a_port_name_the_chip_does_have_still_builds(tmp_path, target, pin, py_parser):
    """The other direction. A refusal that got wider would pass every assertion above."""
    out = _compile(tmp_path, _program(pin), target, py_parser)

    assert not HEADER.search(out), f"{target} {pin} must still compile:\n{out}"


def test_both_front_ends_refuse_identically(tmp_path):
    """The differential axis cannot see this: both front ends refuse, so no image is compared."""
    hand = _compile(tmp_path, _program("0"), "attiny85", py_parser=False)
    cpython = _compile(tmp_path, _program("0"), "attiny85", py_parser=True)

    assert HEADER.search(hand).groups() == HEADER.search(cpython).groups()
