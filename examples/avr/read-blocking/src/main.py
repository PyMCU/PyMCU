# PyMCU -- read-blocking: UART.read_blocking() echo
#
# Demonstrates:
#   - UART.read_blocking(): blocking read that polls until a byte arrives
#   - Distinct from read() by name only; identical semantics
#
# Output on UART (9600 baud):
#   "RB\n"    -- boot banner
#   <echoed byte>  -- every received byte echoed back
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART


def main():
    uart = UART(9600)

    uart.println("RB")

    while True:
        b: uint8 = uart.read_blocking()
        uart.write(b)
