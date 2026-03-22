# PyMCU -- extern-call: C interop via @extern decorator
#
# Demonstrates calling C functions from PyMCU firmware using:
#   @extern("c_symbol") -- declares an external C function
#   [tool.whip.ffi]    -- pyproject.toml section listing C sources
#
# The C functions live in c_src/math_helper.c and are compiled with
# avr-gcc, then linked with the firmware via avr-ld.
#
# Output on UART (9600 baud, 16 MHz):
#   "EXTERN\n"    -- boot banner
#   "M:1E\n"      -- c_mul8(3, 10) = 30 = 0x1E
#   "S:FF\n"      -- c_add_saturate(200, 100) = 255 (saturated) = 0xFF
#   "A:0A\n"      -- c_add_saturate(4, 6) = 10 = 0x0A
#   "OK\n"        -- done

from whipsnake.types import uint8, inline
from whipsnake.hal.uart import UART
from whipsnake.ffi import extern


# Declare C functions -- bodies are stubs (compiler ignores them).
@extern("c_mul8")
def c_mul8(a: uint8, b: uint8) -> uint8:
    pass

@extern("c_add_saturate")
def c_add_saturate(a: uint8, b: uint8) -> uint8:
    pass


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
def print_hex(uart: UART, prefix: uint8, val: uint8):
    uart.write(prefix)
    uart.write(':')
    uart.write(nibble_hi(val))
    uart.write(nibble_lo(val))
    uart.write('\n')

def main():
    uart = UART(9600)
    print("EXTERN")

    # c_mul8(3, 10) == 30 == 0x1E
    m: uint8 = c_mul8(3, 10)
    print_hex(uart, 'M', m)

    # c_add_saturate(200, 100) == 255 (clamped) == 0xFF
    s: uint8 = c_add_saturate(200, 100)
    print_hex(uart, 'S', s)

    # c_add_saturate(4, 6) == 10 == 0x0A
    a: uint8 = c_add_saturate(4, 6)
    print_hex(uart, 'A', a)

    print("OK")

    while True:
        pass
