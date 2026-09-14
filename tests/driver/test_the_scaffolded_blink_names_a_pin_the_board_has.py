# tests/driver/test_the_scaffolded_blink_names_a_pin_the_board_has.py
#
# `pymcu new --board attiny85 --stdlib circuitpython` used to write the standard
# `board.LED` blink, and that program could not be built for the chip it had just been
# generated for: the bare DIPs have no LED soldered to them, so the layer's board module
# does not define one, and the build stopped at "Unknown module member: board_LED"
# (pymcu-circuitpython#3).
#
# The failure arrived after the project was already on disk, which is what made it read as
# "the compiler is broken" rather than "this part has no LED".
#
# What is pinned here is the whole class, not the one board: for every board this command
# accepts, the constant the CircuitPython scaffold writes has to exist in the board module
# the build will generate from. A new board file with no LED is then caught at the moment
# it is added rather than the first time somebody scaffolds against it.

import ast
import re
from pathlib import Path

import pytest

from src.driver.commands.new import _chip_imports
from src.driver.core.boards import BOARD_CHIPS

boards_pkg = pytest.importorskip(
    "pymcu_circuitpython.boards",
    reason="the CircuitPython layer is not installed in this environment")

BOARDS_DIR = Path(boards_pkg.__file__).parent

# The scaffolder emits `digitalio.DigitalInOut(board.NAME)`; NAME is what must exist.
PIN = re.compile(r"\bboard\.([A-Za-z_][A-Za-z0-9_]*)")

AVR_BOARDS = sorted(b for b, chip in BOARD_CHIPS.items() if chip.startswith("at"))


def _constants(board: str) -> dict[str, str]:
    """Every `NAME = "Pxn"` in that board file, read rather than imported.

    Importing is not an option here: the Arduino board files do `from busio import I2C`,
    and `busio` is a top-level name only the compiler's module resolution provides. Under
    CPython the import fails, which would have turned this test into one that skips the
    four boards that actually work and checks only the ATtinys.
    """
    path = BOARDS_DIR / f"{board}.py"
    assert path.exists(), f"no board file for {board}"
    out: dict[str, str] = {}
    for node in ast.parse(path.read_text()).body:
        if not isinstance(node, ast.Assign) or not isinstance(node.value, ast.Constant):
            continue
        for target in node.targets:
            if isinstance(target, ast.Name) and isinstance(node.value.value, str):
                out[target.id] = node.value.value
    return out


@pytest.mark.parametrize("board", AVR_BOARDS)
def test_the_scaffolded_pin_exists_in_that_boards_module(board):
    source = _chip_imports(BOARD_CHIPS[board], "circuitpython", board)
    names = PIN.findall(source)

    assert names, f"the CircuitPython scaffold for {board} names no board pin:\n{source}"

    defined = _constants(board)
    for name in names:
        assert name in defined, (
            f"`pymcu new --board {board} --stdlib circuitpython` scaffolds board.{name}, "
            f"which pymcu_circuitpython/boards/{board}.py does not define. The project "
            f"would be created and then fail to build.")


@pytest.mark.parametrize("board", AVR_BOARDS)
def test_the_scaffolded_pin_resolves_to_a_port_name(board):
    """A constant that exists but holds something the HAL cannot take is the same dead end.

    Every AVR board constant in this layer is a port name like "PB0". Asserting the shape
    catches an alias pointed at another alias, or at a bare Arduino number, which the bare
    ATtinys refuse.
    """
    source = _chip_imports(BOARD_CHIPS[board], "circuitpython", board)
    defined = _constants(board)

    for name in PIN.findall(source):
        value = defined[name]
        assert re.fullmatch(r"P[A-L][0-7]", value), (
            f"board.{name} on {board} is {value!r}, which is not a port pin name")


def test_the_bare_attiny_that_reported_this_does_not_ask_for_an_led():
    """The reported case, named on its own so a regression says which board broke."""
    source = _chip_imports("attiny85", "circuitpython", "attiny85")

    assert "board.LED" not in source
    assert "LED" not in _constants("attiny85"), (
        "a bare ATtiny85 has no LED; defining one would point at PB5, which is RESET")
