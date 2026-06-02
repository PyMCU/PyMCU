# PyMCU -- map-range / constrain integration test
#
# UART output (9600 baud):
#   "MR\n"     -- boot banner
#   "A:80\n"   -- map_range(128, 0, 255, 0, 255) == 128  -> 0x80
#   "B:FF\n"   -- map_range(255, 0, 255, 0, 255) == 255  -> 0xFF
#   "C:40\n"   -- map_range(128, 0, 255, 0, 128) == 64   -> 0x40
#   "D:00\n"   -- map_range(0,   0, 255, 0, 255) == 0    -> 0x00
#   "E:0A\n"   -- constrain(10, 0, 20)  == 10  -> 0x0A
#   "F:05\n"   -- constrain(0,  5, 20)  == 5   (clamps up to lo)  -> 0x05
#   "G:14\n"   -- constrain(50, 0, 20)  == 20  -> 0x14
from pymcu.types import uint8, uint16
from pymcu.hal.uart import UART
from pymcu.math import map_range, constrain
from pymcu.types import inline


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
    uart.write_str("MR\n")

    a: uint16 = map_range(128, 0, 255, 0, 255)
    print_hex(uart, "A:", a)

    b: uint16 = map_range(255, 0, 255, 0, 255)
    print_hex(uart, "B:", b)

    c: uint16 = map_range(128, 0, 255, 0, 128)
    print_hex(uart, "C:", c)

    d: uint16 = map_range(0, 0, 255, 0, 255)
    print_hex(uart, "D:", d)

    e: uint16 = constrain(10, 0, 20)
    print_hex(uart, "E:", e)

    f: uint16 = constrain(0, 5, 20)
    print_hex(uart, "F:", f)

    g: uint16 = constrain(50, 0, 20)
    print_hex(uart, "G:", g)

    while True:
        pass
