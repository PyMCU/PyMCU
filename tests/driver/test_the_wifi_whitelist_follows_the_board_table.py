# tests/driver/test_the_wifi_whitelist_follows_the_board_table.py
#
# The seam between two files that have to agree and cannot see each other.
#
# src/driver/core/boards.py knows which board names exist. lib/src/pymcu/hal/wifi.py
# carries a whitelist of the ones that have a CYW43439 soldered on, and refuses the rest.
# Nothing links them, so a W board added to the driver and not to the HAL is refused at
# compile time with a message that reads like a bug in wifi.py.
#
# A comment at each end asks the editor to remember. This test does not ask.

import re
from pathlib import Path

from src.driver.core.boards import BOARD_CHIPS, board_label

REPO = Path(__file__).resolve().parents[2]
WIFI = REPO / "lib" / "src" / "pymcu" / "hal" / "wifi.py"

# The guard is a chain of `__CHIP__.board == "name"` comparisons, because the compile-time
# evaluator does string equality and not membership. If that ever becomes a tuple or a set,
# this regex stops finding anything and the test fails LOUDLY on the empty set rather than
# passing by matching nothing. Rewrite the regex then; do not delete the test.
_COMPARISON = re.compile(r'__CHIP__\.board\s*==\s*"([^"]*)"')


def _whitelisted_boards() -> set[str]:
    found = {name for name in _COMPARISON.findall(WIFI.read_text()) if name}
    assert found, (
        f"no `__CHIP__.board == \"...\"` comparisons found in {WIFI}. The guard was rewritten "
        "in a shape this test cannot read. Update the regex above so the two lists keep being "
        "compared; an unread whitelist is the failure this test exists to prevent."
    )
    return found


def _boards_that_carry_a_radio() -> set[str]:
    """The W boards, decided from the PRODUCT NAME and not from the whitelist.

    This has to be an independent source or the test is a mirror. The product label is one:
    a board with the radio is sold as a "... W" and one without is not, so the label answers
    the hardware question without consulting the file under test.
    """
    return {
        board for board, chip in BOARD_CHIPS.items()
        if chip in ("rp2040", "rp2350") and board_label(board).endswith(" W")
    }


def test_every_w_board_the_driver_knows_is_allowed_wifi():
    """A W board in boards.py and not in wifi.py is refused, and looks like a HAL bug.

    This is the loud failure of the two, and the one the whitelist chooses to have. It costs
    a user a compile error on hardware that works, so the fix is a line in wifi.py.
    """
    missing = _boards_that_carry_a_radio() - _whitelisted_boards()

    assert not missing, (
        f"these boards carry a CYW43439 and the WiFi guard refuses them: {sorted(missing)}. "
        f"Add each one to the whitelist in {WIFI}."
    )


def test_the_whitelist_names_no_board_without_a_radio():
    """The silent failure, and the reason the list is a whitelist.

    A name here that has no radio builds the driver into a firmware for hardware that cannot
    run it, and nothing says so until someone has the board on a desk. That is why the guard
    lists what IS allowed rather than what is not.
    """
    radioless = _whitelisted_boards() - _boards_that_carry_a_radio()

    assert not radioless, (
        f"the WiFi guard allows these, and they have no radio: {sorted(radioless)}. Either the "
        f"whitelist in {WIFI} has a name too many, or boards.py is missing a board that really "
        "is a W and whose label does not say so."
    )


def test_every_whitelisted_name_is_a_board_the_driver_accepts():
    """A misspelling in the whitelist is invisible: it just never matches.

    The comparison is against a string the driver passes through unchanged, so a typo does not
    fail, it silently stops protecting the board it was meant to name.
    """
    unknown = _whitelisted_boards() - set(BOARD_CHIPS)

    assert not unknown, (
        f"the WiFi guard compares against names no board table has: {sorted(unknown)}. A name "
        "that matches nothing is dead code that looks like coverage."
    )


def test_a_chip_name_used_as_a_board_name_is_not_whitelisted():
    """PINNED, and deliberately not a mistake to fix.

    `rp2040` and `rp2350` are themselves board keys, mapping to their own chip, so a project
    can write `board = "rp2040"` meaning the bare chip. That board is refused WiFi, which is
    correct: a bare chip has no radio next to it. If it ever becomes right to accept them,
    something has changed about what those keys mean and this test should be read before the
    whitelist is edited.
    """
    whitelist = _whitelisted_boards()

    assert "rp2040" not in whitelist
    assert "rp2350" not in whitelist
