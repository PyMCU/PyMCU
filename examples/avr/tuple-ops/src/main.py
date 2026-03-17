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
#   "E:02\n"    -- enumerate sum: i_sum = 0+1+2 = 3? no: sum of i*x: 0*10+1*20+2*30=80=0x50
#                  wait -- let's output i_sum = 0+1+2=3 and x_sum=10+20+30=60
#   "I:03\n"    -- enumerate list: sum of indices 0+1+2=3
#   "X:3C\n"    -- enumerate list: sum of values 10+20+30=60=0x3C
#   "J:03\n"    -- enumerate range(4): sum of indices 0+1+2+3=6 no, 0+1+2=3 (range(3))
#   "Y:03\n"    -- enumerate range(3): sum of values 0+1+2=3
#
from pymcu.types import uint8, uint16, inline
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


@inline
def divmod8(a: uint8, b: uint8):
    q: uint8 = a // b
    r: uint8 = a - (q * b)
    return (q, r)


def main():
    uart = UART(9600)

    # Boot banner
    uart.write(84)   # T
    uart.write(79)   # O
    uart.write(10)   # \n

    # -- Test literal tuple unpack: a, b = (3, 7) --
    a: uint8 = 0
    b: uint8 = 0
    a, b = (3, 7)
    uart.write(65)   # A
    uart.write(58)   # :
    uart.write(nibble_hi(a))
    uart.write(nibble_lo(a))
    uart.write(10)   # \n
    uart.write(66)   # B
    uart.write(58)   # :
    uart.write(nibble_hi(b))
    uart.write(nibble_lo(b))
    uart.write(10)   # \n

    # -- Test inline multi-return: quot, rem = divmod8(10, 3) --
    quot: uint8 = 0
    rem: uint8 = 0
    quot, rem = divmod8(10, 3)
    uart.write(81)   # Q
    uart.write(58)   # :
    uart.write(nibble_hi(quot))
    uart.write(nibble_lo(quot))
    uart.write(10)   # \n
    uart.write(82)   # R
    uart.write(58)   # :
    uart.write(nibble_hi(rem))
    uart.write(nibble_lo(rem))
    uart.write(10)   # \n

    # -- Test enumerate() over const list --
    i_sum: uint8 = 0
    x_sum: uint8 = 0
    for i, x in enumerate([10, 20, 30]):
        i_sum = i_sum + i
        x_sum = x_sum + x
    uart.write(73)   # I
    uart.write(58)   # :
    uart.write(nibble_hi(i_sum))
    uart.write(nibble_lo(i_sum))
    uart.write(10)   # \n
    uart.write(88)   # X
    uart.write(58)   # :
    uart.write(nibble_hi(x_sum))
    uart.write(nibble_lo(x_sum))
    uart.write(10)   # \n

    # -- Test enumerate() over range(3) --
    j_sum: uint8 = 0
    y_sum: uint8 = 0
    for j, y in enumerate(range(3)):
        j_sum = j_sum + j
        y_sum = y_sum + y
    uart.write(74)   # J
    uart.write(58)   # :
    uart.write(nibble_hi(j_sum))
    uart.write(nibble_lo(j_sum))
    uart.write(10)   # \n
    uart.write(89)   # Y
    uart.write(58)   # :
    uart.write(nibble_hi(y_sum))
    uart.write(nibble_lo(y_sum))
    uart.write(10)   # \n

    while True:
        pass
