# ATmega328P: 16-bit arithmetic and comparison edge cases
# Tests: uint16 addition, subtraction, equality comparison (JumpIfEqual 16-bit fix),
#        less-than, greater-than, uint16 wrap-around at 0xFFFF -> 0x0000
#
# Each test sends 'P' (pass=80) or 'F' (fail=70) over UART at 9600 baud.
# Final output: "PPPPPP\nDONE\n" if all tests pass.
#
from pymcu.types import uint8, uint16
from pymcu.hal.uart import UART


def main():
    uart = UART(9600)

    # --- Test 1: 16-bit addition ---
    a: uint16 = 1000
    b: uint16 = 234
    c: uint16 = a + b          # expected: 1234

    if c == 1234:
        uart.write('P')
    else:
        uart.write('F')

    # --- Test 2: 16-bit subtraction ---
    d: uint16 = c - a          # expected: 234
    if d == 234:
        uart.write('P')
    else:
        uart.write('F')

    # --- Test 3: 16-bit less-than ---
    if a < c:
        uart.write('P')        # 1000 < 1234
    else:
        uart.write('F')

    # --- Test 4: 16-bit greater-than ---
    if c > b:
        uart.write('P')        # 1234 > 234
    else:
        uart.write('F')

    # --- Test 5: 16-bit wrap-around (0xFFFF + 1 = 0x0000) ---
    e: uint16 = 65535
    e = e + 1
    if e == 0:
        uart.write('P')
    else:
        uart.write('F')

    # --- Test 6: 16-bit byte extraction ---
    f: uint16 = 0x1234
    lo: uint8 = f & 0xFF       # expected: 0x34 = 52
    hi: uint8 = (f >> 8) & 0xFF  # expected: 0x12 = 18
    if lo == 52 and hi == 18:
        uart.write('P')
    else:
        uart.write('F')

    # --- Newline + DONE marker ---
    uart.write('\n')
    uart.println("DONE")

    while True:
        pass
