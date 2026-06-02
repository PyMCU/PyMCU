# PyMCU -- random PRNG integration test
#
# Tests:
#   - randomSeed() makes the sequence deterministic
#   - random(n) returns values in [0, n)
#   - random2(lo, hi) returns values in [lo, hi)
#
# UART output (9600 baud):
#   "RN\n"        -- boot banner
#   "A:XX\n"      -- random(100)  after seed(42): in 0..99
#   "B:XX\n"      -- random(100)  second call:    in 0..99, != A
#   "C:XX\n"      -- random2(10,50) after seed(42): in 10..49
#   "D:XX\n"      -- random(100)  after re-seed(42): must equal A (determinism check)
from pymcu.types import uint8, uint16, inline
from pymcu.hal.uart import UART
from pymcu.random import randomSeed, random, random2


@inline
def print_hex(uart: UART, prefix: str, val: uint8):
    uart.write_str(prefix)
    hi: uint8 = (val >> 4) & 0x0F
    lo: uint8 = val & 0x0F
    if hi < 10:
        uart.write(0x30 + hi)
    else:
        uart.write(0x41 + hi - 10)
    if lo < 10:
        uart.write(0x30 + lo)
    else:
        uart.write(0x41 + lo - 10)
    uart.write_str("\n")


def main():
    uart = UART(9600)
    uart.write_str("RN\n")

    randomSeed(42)
    a: uint16 = random(100)
    b: uint16 = random(100)
    print_hex(uart, "A:", a)
    print_hex(uart, "B:", b)

    randomSeed(42)
    c: uint16 = random2(10, 50)
    print_hex(uart, "C:", c)

    randomSeed(42)
    d: uint16 = random(100)
    print_hex(uart, "D:", d)

    while True:
        pass
