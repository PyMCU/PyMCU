# PyMCU -- lambda-ops: Lambda expressions (F9) and raw strings (F1)
#
# Demonstrates:
#   F9: lambda x: expr  (no closure capture; inlined at call site)
#   F1: r"\n" raw string literals
#
# Output on UART (9600 baud):
#   "LB\n"    -- boot banner
#   "D:05\n"  -- double(2) = 2*2+1 = 5
#   "T:09\n"  -- triple(3) = 3*3 = 9
#   "R:5C\n"  -- raw string r"\" has one char: backslash = 0x5C
#
from whisnake.types import uint8, inline
from whisnake.hal.uart import UART


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


def main():
    uart = UART(9600)
    uart.println("LB")

    # F9: lambda — call at point of definition
    double = lambda x: x * 2 + 1
    result: uint8 = double(2)
    uart.write('D')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    triple = lambda x: x * x
    result = triple(3)
    uart.write('T')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    # F1: raw string r"\n" contains two characters: backslash (0x5C) + 'n' (0x6E)
    # No escape processing — \n is NOT a newline.
    raw_str: str = r"\n"
    ch: uint8 = ord(raw_str[0])  # first char is backslash = 0x5C
    uart.write('R')
    uart.write(':')
    uart.write(nibble_hi(ch))
    uart.write(nibble_lo(ch))
    uart.write('\n')

    while True:
        pass
