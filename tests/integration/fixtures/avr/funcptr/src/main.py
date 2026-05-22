# ATmega328P: Function pointer (Callable / ICALL) integration test
#
# Verifies that:
#   - Assigning a function to a Callable-typed variable captures its address
#   - Calling through a Callable variable emits ICALL (indirect call via Z register)
#   - The result is correct for both stored function references
#
# Protocol (UART at 9600 baud):
#   "FUNCPTR\n"          -- boot banner
#   byte: add_one(10)    -- should be 11  (0x0B)
#   byte: add_two(10)    -- should be 12  (0x0C)
#   byte: fn reassigned, fn(20) via add_two  -- should be 22 (0x16)
#   0x0A                 -- newline

from pymcu.types import uint8, Callable
from pymcu.hal.uart import UART


def add_one(x: uint8) -> uint8:
    result: uint8 = x + 1
    return result


def add_two(x: uint8) -> uint8:
    result: uint8 = x + 2
    return result


def main():
    uart = UART(0, 9600)
    uart.write_str("FUNCPTR\n")

    # Assign function to Callable variable — no explicit funcref() needed
    fn: Callable = add_one
    r1: uint8 = fn(10)
    uart.write(r1)

    fn2: Callable = add_two
    r2: uint8 = fn2(10)
    uart.write(r2)

    # Reassign to a different function
    fn3: Callable = add_two
    r3: uint8 = fn3(20)
    uart.write(r3)

    uart.write(10)

