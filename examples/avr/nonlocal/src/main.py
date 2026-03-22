# PyMCU -- nonlocal: PEP 3104 nonlocal variable binding in nested @inline functions
#
# Demonstrates:
#   - A function defined inside another function using @inline
#   - nonlocal var allows the inner function to write to the outer variable
#   - Counter pattern: each call to increment() mutates outer 'count'
#
# Output on UART (9600 baud):
#   "NL\n"    -- boot banner
#   "A:03\n"  -- count after 3 increments = 3
#   "B:0A\n"  -- total after add(10) = 10
#   "C:19\n"  -- total after add(15) = 25 = 0x19
#
from whipsnake.types import uint8, inline
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


def main():
    uart = UART(9600)
    uart.println("NL")

    # Counter: inner function increments outer variable
    count: uint8 = 0

    @inline
    def increment():
        nonlocal count
        count = count + 1

    increment()
    increment()
    increment()

    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(count))
    uart.write(nibble_lo(count))
    uart.write('\n')

    # Accumulator: inner function adds to outer total
    total: uint8 = 0

    @inline
    def add(delta: uint8):
        nonlocal total
        total = total + delta

    add(10)
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hi(total))
    uart.write(nibble_lo(total))
    uart.write('\n')

    add(15)
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hi(total))
    uart.write(nibble_lo(total))
    uart.write('\n')

    while True:
        pass
