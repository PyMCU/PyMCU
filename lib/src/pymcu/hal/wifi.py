# WiFi HAL facade: dispatch to the CYW43439 driver.
#
# The CYW43439 is not part of any RP2xxx chip. It is a separate part soldered next to it on
# some boards and absent on others, and the silicon is identical either way: a Pico and a Pico
# W are both rp2040, a Pico 2 and a Pico 2 W are both rp2350. So the question this file has to
# answer is about the BOARD, and the only thing it is given is the chip.
#
# The board identity now reaches here, as __CHIP__.board, and that closes the question FOR
# PROGRAMS THAT ANSWER IT. It does not close it for everyone, and the difference is worth
# stating because it will look like the field did not work:
#
#   built with `board = "pico"`    refused, correctly: that board has no radio
#   built with `board = "pico_w"`  compiles
#   built with `target = "rp2040"` compiles, for a plain Pico as well
#
# The last row is not a gap left open by carelessness. A project sets `board` or `target` and
# the driver refuses both at once, so a program built by target has no board to give and the
# compiler is not told which one it is. 344 of the 374 projects in the three trees are built
# that way. What changed is not that the silence went away: it is that a program can now say
# enough to be protected from it, and one that does not is no longer being failed by the
# compiler, it is answering a question it was never asked.
#
# One driver serves both: the part is the same CYW43439, wired to the same four pins, and only
# the MCU registers underneath it differ. See pymcu/hal/rp/cyw43.py.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.name == "rp2040" or __CHIP__.name == "rp2350":
    # The board, when the program said which one. Every board that carries a CYW43439 is named
    # here, and a named board that is not on the list is refused: of the two ways to be wrong,
    # refusing a Pico W is loud and gets reported in a minute, while building a radio into a
    # firmware for a board with no radio is silent and gets found on a desk.
    #
    # THIS LIST IS COUPLED TO src/driver/core/boards.py. A W board added there and not here is
    # refused with the message below, which will read as a bug in this file and is really a
    # missing line. tests/driver/test_the_wifi_whitelist_follows_the_board_table.py compares the
    # two lists so nobody has to notice, and boards.py carries the other half of this note.
    #
    # Four spellings because the driver accepts four and canonicalises none: resolve_chip_for_board
    # is a dict lookup, so whatever the user wrote arrives here unchanged. Listing all four is not
    # caution, it is the only thing that works.
    #
    # The empty string is the 92% and is accepted in silence. A project sets `board` or
    # `target`, never both (the driver refuses the pair), so a program built by target has no
    # board to give: measured over the three trees, 344 projects set target and 30 set board,
    # and all four WiFi programs that exist set target. Treating "" as "no" would take WiFi
    # away from every one of them, the Pico 2 W demo included.
    if (__CHIP__.board == ""
            or __CHIP__.board == "pico_w"
            or __CHIP__.board == "raspberry_pi_pico_w"
            or __CHIP__.board == "pico2_w"
            or __CHIP__.board == "raspberry_pi_pico2_w"):
        from pymcu.hal.rp.cyw43 import CYW43
    else:
        # A board that was named and does not carry the radio. This is the one case the board
        # field buys, and it is the only one where telling someone to use another board is the
        # correct advice rather than an insult to the hardware they own.
        raise CompileError(
            "this board has no WiFi radio. The CYW43439 is a separate part carried by the W "
            "boards and absent on the others, so the chip is the same and the radio is not "
            "there: a Pico W or a Pico 2 W is the one to build for. If the board is a W and "
            "this still fires, it is missing from the list in pymcu/hal/wifi.py."
        )
else:
    # Not an RP2xxx at all. No board in this family carries a CYW43439, so this is the case
    # where another board really is the answer.
    # One string literal and no concatenation with __CHIP__.name: a `raise CompileError(...)`
    # message has to be literal text, and building one from the chip name is a syntax error that
    # breaks EVERY target, the supported ones included.
    raise CompileError(
        "WiFi (CYW43439) is not available on this chip. The radio is a separate part carried by "
        "some RP2xxx boards, so no chip outside that family can reach it. A Pico W (rp2040) or a "
        "Pico 2 W (rp2350) is the supported board."
    )
