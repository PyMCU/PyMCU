# PyMCU -- builtin-ops: New builtin operators and functions
#
# Tests:
#   - not in operator: x not in [1,2,3]
#   - is / is not operators: x is 5, x is not 5
#   - in operator: x in [1,2,3]
#   - sum([list]) builtin
#   - any([list]) builtin
#   - all([list]) builtin
#   - hex(n) compile-time string
#   - bin(n) compile-time string
#   - divmod(a, b) built-in
#   - uart.available() HAL method
#
# UART output (9600 baud):
#   "BO\n"      -- boot banner
#   "I:01\n"    -- in: 3 in [1,2,3] = 1
#   "N:01\n"    -- not in: 5 not in [1,2,3] = 1
#   "S:01\n"    -- is: z is 7 (z=7) = 1
#   "T:01\n"    -- is not: z is not 3 (z=7) = 1
#   "U:06\n"    -- sum([1,2,3]) = 6
#   "A:01\n"    -- any([0,0,1]) = 1
#   "L:01\n"    -- all([1,1,1]) = 1
#   "Q:03\n"    -- divmod(10,3) quotient = 3
#   "R:01\n"    -- divmod(10,3) remainder = 1
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart = UART(9600)

    uart.println("BO")

    # -- in operator: 3 in [1, 2, 3] => 1 --
    x: uint8 = 3
    result: uint8 = 0
    if x in [1, 2, 3]:
        result = 1
    uart.write('I')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    # -- not in operator: 5 not in [1, 2, 3] => 1 --
    y: uint8 = 5
    result = 0
    if y not in [1, 2, 3]:
        result = 1
    uart.write('N')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    # -- is operator: z is 7 (z=7) => 1 --
    z: uint8 = 7
    result = 0
    if z is 7:
        result = 1
    uart.write('S')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    # -- is not operator: z is not 3 (z=7) => 1 --
    result = 0
    if z is not 3:
        result = 1
    uart.write('T')
    uart.write(':')
    uart.write(nibble_hi(result))
    uart.write(nibble_lo(result))
    uart.write('\n')

    # -- sum([1, 2, 3]) == 6 --
    s: uint8 = sum([1, 2, 3])
    uart.write('U')
    uart.write(':')
    uart.write(nibble_hi(s))
    uart.write(nibble_lo(s))
    uart.write('\n')

    # -- any([0, 0, 1]) == 1 --
    a: uint8 = any([0, 0, 1])
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(a))
    uart.write(nibble_lo(a))
    uart.write('\n')

    # -- all([1, 1, 1]) == 1 --
    l: uint8 = all([1, 1, 1])
    uart.write('L')
    uart.write(':')
    uart.write(nibble_hi(l))
    uart.write(nibble_lo(l))
    uart.write('\n')

    # -- divmod(10, 3) => quotient=3, remainder=1 --
    quot: uint8 = 0
    rem: uint8 = 0
    quot, rem = divmod(10, 3)
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

    # -- hex(255) => "0xff" (compile-time string) --
    # Output: "H:0xff\n"
    uart.write('H')
    uart.write(':')
    uart.write_str(hex(255))
    uart.write('\n')

    # -- bin(5) => "0b101" (compile-time string) --
    # Output: "B:0b101\n"
    uart.write('B')
    uart.write(':')
    uart.write_str(bin(5))
    uart.write('\n')

    # -- uart.available() => 0 when no data waiting --
    # (No data will be in the RX buffer at this point, so result=0)
    avail: uint8 = uart.available()
    uart.write('V')
    uart.write(':')
    uart.write(nibble_hi(avail))
    uart.write(nibble_lo(avail))
    uart.write('\n')

    while True:
        pass
