# -----------------------------------------------------------------------------
# PyMCU os -- compile-time facts that CircuitPython spells as os.uname().
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
#
# There is no operating system and no filesystem on the target. listdir,
# getenv, stat and friends are not provided. uname() is not those: it is a
# five-field record of the part this firmware was built for, and every field
# is a fact the compiler already has as __CHIP__.
#
#   from os import uname
#   u = uname()          # sysname, nodename, release, version, machine
#   u.sysname            # "PyMCU"
#   u.machine            # __CHIP__.name (RP parts include the RP2040/RP2350 token)
#   "Linux" not in uname()
#
# The last of those is adafruit_dht's CircuitPython-vs-Blinka test. sysname is
# never "Linux" here, so the CircuitPython arm is the one that remains.
from pymcu.chips import __CHIP__
from pymcu.types import inline, const


# CircuitPython reports posix; nothing here is Windows.
name: str = "posix"
sep: str = "/"


class uname_result:
    def __init__(self, sysname: str, nodename: str, release: str, version: str, machine: str):
        self.sysname = sysname
        self.nodename = nodename
        self.release = release
        self.version = version
        self.machine = machine

    @inline
    def __contains__(self, item: const[str]) -> bool:
        # Compare against literals, not field names: `item == self.sysname`
        # is two names and the string-equality fold only fires when one
        # operand is a StringLiteral. sysname is always "PyMCU"; the three
        # unused posix fields are empty; machine is the only part-specific
        # field and is also tested as a substring via `item in uname().machine`.
        return item == "PyMCU" or item == "" or item == self.machine


@inline
def uname() -> uname_result:
    # machine is the chip the firmware was built for. The RP parts also carry
    # the token Adafruit's platformdetect looks for (`"RP2350" in uname().machine`).
    match __CHIP__.name:
        case "rp2350":
            return uname_result("PyMCU", "", "", "", "rp2350 RP2350")
        case "rp2040":
            return uname_result("PyMCU", "", "", "", "rp2040 RP2040")
        case _:
            return uname_result("PyMCU", "", "", "", __CHIP__.name)
