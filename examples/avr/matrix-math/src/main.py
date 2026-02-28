# ATmega328P: Virtual 4x8 LED matrix frame buffer
#
# Stress-tests:
#   - Fixed-size arrays: frame: uint8[4] = [0, 0, 0, 0]
#   - Array element read/write with constant indices: frame[0] = set_bit(frame[0], ...)
#   - Variable left-shift: mask = 1 << col  (Binary{LShift, Const(1), Variable})
#   - Bitwise OR on uint8 array elements via non-inline functions
#   - Non-inline functions: set_bit takes uint8 args, returns uint8
#     (tests GCC-AVR calling convention: arg -> R24, return -> R24)
#   - Dynamic column wrap: (col + offset) & 0x07
#
# Hardware: Arduino Uno
#   UART TX on PD1 (Arduino pin 1) -- connect to PC at 9600 baud
#   Open a hex viewer to see raw byte frames.
#
# Frame format (5 bytes per frame):
#   Bytes 0-3: frame[0]..frame[3] as bit patterns (bit N = column N pixel)
#   Byte 4   : 0x0A (newline separator)
#
# Animation: a diagonal line (4 lit pixels) scrolls horizontally one step per frame.
# Expected first 8 frames (hex, each row shows frame[0]..frame[3]):
#   Frame 0: 01 02 04 08   (cols 0,1,2,3)
#   Frame 1: 02 04 08 10   (cols 1,2,3,4)
#   Frame 2: 04 08 10 20   (cols 2,3,4,5)
#   ...

from pymcu.types import uint8
from pymcu.hal.uart import UART


def set_bit(val: uint8, col: uint8) -> uint8:
    # Set bit `col` in `val`; returns the modified byte.
    # Tests: variable shift (1 << col), bitwise OR.
    mask: uint8 = 1 << col
    result: uint8 = val | mask
    return result


def main():
    uart = UART(9600)
    uart.println("MATRIX")

    # 4-row x 8-column frame buffer as a fixed-size uint8 array.
    # Each element represents one row; bit N = state of column N pixel.
    frame: uint8[4] = [0, 0, 0, 0]

    col: uint8 = 0      # current diagonal offset (0-7)

    while True:
        # Clear all rows
        frame[0] = 0
        frame[1] = 0
        frame[2] = 0
        frame[3] = 0

        # Draw diagonal: row R gets pixel at column (col + R) mod 8.
        frame[0] = set_bit(frame[0], col & 0x07)
        frame[1] = set_bit(frame[1], (col + 1) & 0x07)
        frame[2] = set_bit(frame[2], (col + 2) & 0x07)
        frame[3] = set_bit(frame[3], (col + 3) & 0x07)

        # Transmit frame over UART (raw bytes)
        uart.write(frame[0])
        uart.write(frame[1])
        uart.write(frame[2])
        uart.write(frame[3])
        uart.write(10)          # 0x0A frame separator

        # Advance diagonal and wrap at 8 columns
        col = (col + 1) & 0x07
