"""The hardware I2C raises the internal pull-ups on SDA and SCL, as Arduino does.

Every Arduino sketch gets this for free: `twi_init()` in Wire/utility/twi.c writes
`digitalWrite(SDA, 1); digitalWrite(SCL, 1)` before enabling the TWI, which switches on
the AVR's 20-50 kOhm internal pull-ups. A module with weak or missing pull-up resistors
still answers; without any pull-up at all SDA/SCL float, the START condition never
completes and the first probe times out.

The HAL does the same now, in `i2c_init`, before the TWBR write. The two bus pins are a
fact of the chip, named `TWI_SDA_PORT`/`TWI_SDA_BIT`/`TWI_SCL_PORT`/`TWI_SCL_BIT` in its
chip module (numbers, not pin-name strings: a string constant in a module every program
imports would shift the string pool it lands in), and the build-time chip name selects
the module they come from -- so a part with no TWI peripheral binds none, and using the
pins there is a compile error rather than silent writes to an ATmega328P register map
that part does not have.

`pullups=False` on the HAL `I2C.__init__` opts out (a bus of 3.3 V devices that must not
see a 5 V pull-up); the CircuitPython and MicroPython layers keep their own APIs and get
the default. The register-level assertion lives in the AVR integration suite
(`compat-cp-board-buses` reads PORTC at the BREAK); here the MIR is checked for the
bit-set operations the pull-up writes compile to, per chip, on both front ends.
"""

import json
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

I2C = ('from pymcu.hal.i2c import I2C\n\n\n'
       'def main() -> None:\n'
       '    i2c = I2C({args})\n')

# (target, PORTx data-space address, the two bits i2c_init must raise).
# PORTC is 0x28 on the 48/88/168/328 family, PORTD is 0x2B on the 2560 and 32U4.
FAMILIES = [
    ("atmega328p", 0x28, {4, 5}),
    ("atmega168", 0x28, {4, 5}),
    ("atmega88", 0x28, {4, 5}),
    ("atmega48", 0x28, {4, 5}),
    ("atmega2560", 0x2B, {0, 1}),
    ("atmega32u4", 0x2B, {0, 1}),
]

TWBR = 0xB8


def _compile(tmp_path: Path, source: str, target: str, py_parser: bool):
    src = tmp_path / "main.py"
    src.write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", target,
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True, env=env,
    )
    return proc.returncode, proc.stdout + proc.stderr, mir


def _main_ops(mir: Path):
    body = json.loads(mir.read_text())["functions"][0]["body"]
    return [op for op in body if op["$t"] != "dbg"]


def _bsets(ops):
    return {(op["target"]["address"], op["bit"]) for op in ops if op["$t"] == "bset"}


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
@pytest.mark.parametrize("target,port,bits", FAMILIES, ids=[f[0] for f in FAMILIES])
def test_the_bus_pins_get_their_pullups(tmp_path, target, port, bits, py_parser):
    rc, out, mir = _compile(tmp_path, I2C.format(args=""), target, py_parser)

    assert rc == 0, out
    ops = _main_ops(mir)
    assert _bsets(ops) == {(port, b) for b in bits}, \
        "SDA and SCL get their internal pull-ups, and nothing else does"


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
def test_the_pullups_come_before_the_twi_enable(tmp_path, py_parser):
    """A pull-up raised after TWEN would be a pull-up the bus never saw."""
    rc, out, mir = _compile(tmp_path, I2C.format(args=""), "atmega328p", py_parser)

    assert rc == 0, out
    ops = _main_ops(mir)
    twbr = next(i for i, op in enumerate(ops)
                if op["$t"] == "copy" and op["dst"].get("address") == TWBR)
    first_bset = next(i for i, op in enumerate(ops) if op["$t"] == "bset")
    assert first_bset < twbr


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
def test_pullups_false_leaves_the_pins_alone(tmp_path, py_parser):
    rc, out, mir = _compile(tmp_path, I2C.format(args="pullups=False"),
                            "atmega328p", py_parser)

    assert rc == 0, out
    assert _bsets(_main_ops(mir)) == set(), "no pull-up writes, and none of the cost"


@pytest.mark.parametrize("py_parser", [False, True], ids=["hand-written", "cpython"])
def test_a_chip_without_a_twi_is_refused(tmp_path, py_parser):
    """The ATtiny has a USI, not a TWI: SDA/SCL exist only in software."""
    rc, out, mir = _compile(tmp_path, I2C.format(args=""), "attiny85", py_parser)

    assert rc != 0
    assert HEADER.search(out), f"expected a diagnostic, got:\n{out}"
