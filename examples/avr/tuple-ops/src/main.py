# PyMCU -- tuple-ops: Tuple unpacking, inline multi-return, and enumerate()
#
# Demonstrates:
#   - Literal tuple unpack: a, b = (3, 7)
#   - Inline multi-return: quot, rem = divmod8(10, 3)
#   - enumerate() over const list: for i, x in enumerate([10, 20, 30])
#   - enumerate() over range: for i, x in enumerate(range(3))
#
# Output on UART (9600 baud):
#   "TO\n"      -- boot banner
#   "A:03\n"    -- literal unpack: a=3, b=7; a hex
#   "B:07\n"    -- literal unpack: b=7 hex
#   "Q:03\n"    -- divmod8(10,3): quotient = 3
#   "R:01\n"    -- divmod8(10,3): remainder = 1
#   "I:03\n"    -- enumerate list: sum of indices 0+1+2=3
#   "X:3C\n"    -- enumerate list: sum of values 10+20+30=60=0x3C
#   "J:03\n"    -- enumerate range(3): sum of indices 0+1+2=3
#   "Y:03\n"    -- enumerate range(3): sum of values 0+1+2=3
#
from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.uart import UART


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


@inline
def divmod8(a: uint8, b: uint8):
    q: uint8 = a // b
    r: uint8 = a - (q * b)
    return (q, r)


def main():
    uart = UART(9600)

    uart.println("TO")

    # -- Test literal tuple unpack: a, b = (3, 7) --
    a: uint8 = 0
    b: uint8 = 0
    a, b = (3, 7)
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(a))
    uart.write(nibble_lo(a))
    uart.write('\n')
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hi(b))
    uart.write(nibble_lo(b))
    uart.write('\n')

    # -- Test inline multi-return: quot, rem = divmod8(10, 3) --
    quot: uint8 = 0
    rem: uint8 = 0
    quot, rem = divmod8(10, 3)
    uart.write('Q')
    uart.write(':')
    uart.write(nibble_hi(quot))
    uart.write(nibble_lo(quot))
    uart.write('\n')
    uart.write('R')
    uart.write(':')
    uart.write(nibble_hi(rem))
    uart.write(nibble_lo(rem))
    uart.write('\n')

    # -- Test enumerate() over const list --
    i_sum: uint8 = 0
    x_sum: uint8 = 0
    for i, x in enumerate([10, 20, 30]):
        i_sum = i_sum + i
        x_sum = x_sum + x
    uart.write('I')
    uart.write(':')
    uart.write(nibble_hi(i_sum))
    uart.write(nibble_lo(i_sum))
    uart.write('\n')
    uart.write('X')
    uart.write(':')
    uart.write(nibble_hi(x_sum))
    uart.write(nibble_lo(x_sum))
    uart.write('\n')

    # -- Test enumerate() over range(3) --
    j_sum: uint8 = 0
    y_sum: uint8 = 0
    for j, y in enumerate(range(3)):
        j_sum = j_sum + j
        y_sum = y_sum + y
    uart.write('J')
    uart.write(':')
    uart.write(nibble_hi(j_sum))
    uart.write(nibble_lo(j_sum))
    uart.write('\n')
    uart.write('Y')
    uart.write(':')
    uart.write(nibble_hi(y_sum))
    uart.write(nibble_lo(y_sum))
    uart.write('\n')

    while True:
        pass
