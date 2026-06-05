# ATmega328P: exception-based error handling
#
# Demonstrates try/except with multiple exception types using
# avr-libc setjmp/longjmp under the hood (zero extra runtime code).
#
# Scenarios:
#   A — validate_sensor: value out of range (0-100) raises ValueError
#   B — safe_divide:     divisor == 0 raises ValueError
#   C — bounds_check:    index >= table size raises IndexError
#   D — type_dispatch:   calls raise_by_type to show multiple exception types
#
# Expected UART output (9600 baud):
#   ERR-HANDLING
#   A:out-of-range
#   B:div-by-zero
#   C:out-of-bounds
#   D:type-err
#   DONE
#
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.exceptions import ValueError, TypeError, IndexError


# ── helpers ──────────────────────────────────────────────────────────────────

def validate_sensor(raw: uint8) -> uint8:
    if raw > 100:
        raise ValueError
    return raw


def safe_divide(num: uint8, den: uint8) -> uint8:
    if den == 0:
        raise ValueError
    return num // den


def bounds_check(idx: uint8, size: uint8) -> uint8:
    if idx >= size:
        raise IndexError
    return idx


def raise_by_type(which: uint8) -> uint8:
    if which == 1:
        raise ValueError
    if which == 2:
        raise TypeError
    return 0


# ── entry ─────────────────────────────────────────────────────────────────────

def main():
    uart = UART(9600)
    uart.println("ERR-HANDLING")

    # A: sensor value out of valid range
    try:
        reading: uint8 = validate_sensor(200)
        uart.println("A:ok")
    except ValueError:
        uart.println("A:out-of-range")

    # B: integer division by zero
    try:
        result: uint8 = safe_divide(10, 0)
        uart.println("B:ok")
    except ValueError:
        uart.println("B:div-by-zero")

    # C: array index out of bounds
    try:
        idx: uint8 = bounds_check(7, 4)
        uart.println("C:ok")
    except IndexError:
        uart.println("C:out-of-bounds")

    # D: multiple exception types in one try block
    try:
        x: uint8 = raise_by_type(2)
        uart.println("D:ok")
    except ValueError:
        uart.println("D:val-err")
    except TypeError:
        uart.println("D:type-err")

    uart.println("DONE")

    while True:
        delay_ms(1000)
