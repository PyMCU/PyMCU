# WiFi HAL facade: dispatch to the CYW43439 driver.
#
# The CYW43439 is not part of any RP2xxx chip. It is a separate part soldered next to it on
# some boards and absent on others, and the silicon is identical either way: a Pico and a Pico
# W are both rp2040, a Pico 2 and a Pico 2 W are both rp2350. So the question this file has to
# answer is about the BOARD, and the only thing it is given is the chip.
#
# What that costs, stated rather than hidden: this import succeeds for a plain Pico or Pico 2
# as well, and the program compiles with WiFi in it for a board that has no radio. Closing that
# needs the board identity to reach the compiler, which today stops at the driver (it maps a
# board name to a chip and passes only the chip). Until then no wording here can tell the two
# apart, and a guard that refused either chip would break the W board, which is the one that
# works.
#
# One driver serves both: the part is the same CYW43439, wired to the same four pins, and only
# the MCU registers underneath it differ. See pymcu/hal/rp/cyw43.py.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.name == "rp2040" or __CHIP__.name == "rp2350":
    from pymcu.hal.rp.cyw43 import CYW43
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
