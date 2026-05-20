# CircuitPython supervisor module integration test fixture
#
# Verifies that supervisor.ticks_ms() compiles and returns 0 at boot
# (PyMCU does not maintain a hardware tick counter by default).
#
# Expected UART output:
#   Byte 0: 0x00 -- low byte of ticks_ms() result (always 0 at boot)
#   Byte 1: 0x44 ('D') -- done marker
#
import board
import busio
import supervisor
from pymcu.types import uint8, uint16


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    t: uint16 = supervisor.ticks_ms()   # stub returns 0
    lo: uint8 = t & 0xFF
    uart.write(lo)        # 0x00
    uart.write(0x44)      # 'D' done marker
    while True:
        pass
