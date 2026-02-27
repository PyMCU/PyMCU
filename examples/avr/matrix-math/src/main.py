# ATmega328P: Virtual 4x8 LED matrix frame buffer
#
# Stress-tests:
#   - Variable left-shift: mask = 1 << col  (Binary{LShift, Const(1), Variable})
#   - Bitwise OR / AND / XOR on uint8 locals from non-inline functions
#   - Non-inline functions: set_bit / clear_bit take uint8 args, return uint8
#     (tests GCC-AVR calling convention: arg → R24, return → R24)
#   - Dynamic column wrap: (col + offset) & 0x07
#   - Pure-function style: frame buffer stored as locals in main(), updated via
#     returned values from set_bit — no global state in called functions
#
# Hardware: Arduino Uno
#   UART TX on PD1 (Arduino pin 1) — connect to PC at 9600 baud
#   Open a hex viewer to see raw byte frames.
#
# Frame format (5 bytes per frame):
#   Bytes 0-3: row0..row3 as bit patterns (bit N = column N pixel)
#   Byte 4   : 0x0A (newline separator)
#
# Animation: a diagonal line (4 lit pixels) scrolls horizontally one step per frame.
# Expected first 8 frames (hex, each row shows row0,row1,row2,row3):
#   Frame 0: 01 02 04 08   (cols 0,1,2,3)
#   Frame 1: 02 04 08 10   (cols 1,2,3,4)
#   Frame 2: 04 08 10 20   (cols 2,3,4,5)
#   ...

from pymcu.types import uint8
from pymcu.hal.uart import UART


# --- Non-inline helper functions (compiled to RCALL/RET) ---

def set_bit(val: uint8, col: uint8) -> uint8:
    # Set bit `col` in `val`; returns the modified byte.
    # Tests: variable shift (1 << col), bitwise OR.
    mask: uint8 = 1 << col
    result: uint8 = val | mask
    return result


def clear_bit(val: uint8, col: uint8) -> uint8:
    # Clear bit `col` in `val`; returns the modified byte.
    # Tests: variable shift, XOR with 0xFF (bitwise NOT), bitwise AND.
    mask: uint8 = 1 << col
    inv: uint8 = mask ^ 0xFF
    result: uint8 = val & inv
    return result


def main():
    uart = UART(9600)
    uart.println("MATRIX")

    # 4-row x 8-column frame buffer as local uint8 variables.
    # Each byte represents one row; bit N = state of column N pixel.
    row0: uint8 = 0
    row1: uint8 = 0
    row2: uint8 = 0
    row3: uint8 = 0

    col: uint8 = 0      # current diagonal offset (0-7)

    while True:
        # Clear all rows
        row0 = 0
        row1 = 0
        row2 = 0
        row3 = 0

        # Draw diagonal: row R gets pixel at column (col + R) mod 8.
        # Uses set_bit (non-inline RCALL) + bitwise mask wrapping.
        row0 = set_bit(row0, col & 0x07)
        row1 = set_bit(row1, (col + 1) & 0x07)
        row2 = set_bit(row2, (col + 2) & 0x07)
        row3 = set_bit(row3, (col + 3) & 0x07)

        # Transmit frame over UART (raw bytes)
        uart.write(row0)
        uart.write(row1)
        uart.write(row2)
        uart.write(row3)
        uart.write(10)          # 0x0A frame separator

        # Advance diagonal and wrap at 8 columns
        col = (col + 1) & 0x07
