# PyMCU -- static-method: @staticmethod on @inline ZCA class methods.
#
# Tests that @staticmethod is accepted and the method behaves correctly
# when called as Class.method().
#
# Math.clamp(200, 10, 100) -> 100 = 0x64
#   GPIOR0 = 0x64, BREAK
#
# Math.abs_diff(30, 7) -> 23 = 0x17
#   GPIOR0 = 0x17, BREAK
#
# Math.clamp(5, 10, 100) -> 10 = 0x0A  (below lower bound)
#   GPIOR0 = 0x0A, BREAK
#
# Data-space address (ATmega328P): GPIOR0 = 0x3E
#
from pymcu.types import uint8, inline, asm
from pymcu.chips.atmega328p import GPIOR0


class Math:
    @staticmethod
    @inline
    def clamp(val: uint8, lo: uint8, hi: uint8) -> uint8:
        if val < lo:
            return lo
        if val > hi:
            return hi
        return val

    @staticmethod
    @inline
    def abs_diff(a: uint8, b: uint8) -> uint8:
        if a > b:
            return a - b
        return b - a


def main():
    # Checkpoint 1: clamp(200, 10, 100) = 100 = 0x64
    r: uint8 = Math.clamp(200, 10, 100)
    GPIOR0.value = r
    asm("BREAK")

    # Checkpoint 2: abs_diff(30, 7) = 23 = 0x17
    d: uint8 = Math.abs_diff(30, 7)
    GPIOR0.value = d
    asm("BREAK")

    # Checkpoint 3: clamp(5, 10, 100) = 10 = 0x0A (below lower bound)
    r2: uint8 = Math.clamp(5, 10, 100)
    GPIOR0.value = r2
    asm("BREAK")

    while True:
        pass
