# PyMCU -- tuple-unpack: Extended unpacking (PEP 3132, F6) and multi-item with (F5)
#
# Demonstrates:
#   F6: a, *rest = (1, 2, 3, 4, 5)
#   F5: with ctx_a as x, ctx_b as y:
#
# Output on UART (9600 baud):
#   "TU\n"    -- boot banner
#   "H:01\n"  -- head of (1,2,3,4,5) = 1
#   "R:02\n"  -- rest[0] of (1,2,3,4,5) = 2
#   "L:05\n"  -- last of (1,2,3,4,5) = 5
#   "W:01\n"  -- multi-item with: entered = 1
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


class Flag:
    entered: uint8

    @inline
    def __init__(self, v: uint8):
        self.entered = v

    @inline
    def __enter__(self):
        pass

    @inline
    def __exit__(self):
        pass


def main():
    uart = UART(9600)
    uart.println("TU")

    # F6: extended unpacking — head, *middle, last = (1,2,3,4,5)
    head: uint8 = 0
    middle: uint8[3]
    last: uint8 = 0
    head, *middle, last = (1, 2, 3, 4, 5)

    uart.write('H')
    uart.write(':')
    uart.write(nibble_hi(head))
    uart.write(nibble_lo(head))
    uart.write('\n')

    uart.write('R')
    uart.write(':')
    uart.write(nibble_hi(middle[0]))
    uart.write(nibble_lo(middle[0]))
    uart.write('\n')

    uart.write('L')
    uart.write(':')
    uart.write(nibble_hi(last))
    uart.write(nibble_lo(last))
    uart.write('\n')

    # F5: multi-item with
    a = Flag(1)
    b = Flag(2)
    with a as fa, b as fb:
        uart.write('W')
        uart.write(':')
        uart.write(nibble_hi(fa.entered))
        uart.write(nibble_lo(fa.entered))
        uart.write('\n')

    while True:
        pass
