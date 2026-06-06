# CircuitPython supervisor module integration test fixture
#
# Verifies that supervisor.ticks_ms() compiles and returns 0 at boot
# (the Timer0 millis counter starts at 0 and is masked to 29 bits).
#
# Expected UART output:
#   Byte 0: 0x00 -- low byte of ticks_ms() result (0 at boot)
#   Byte 1: 0x44 ('D') -- done marker
#
import board
import busio
import supervisor
from pymcu.types import uint8, uint32


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    t: uint32 = supervisor.ticks_ms()   # 0 at boot
    buf: uint8[1]
    buf[0] = t & 0xFF
    uart.write(buf)       # 0x00
    uart.write(b"D")      # 'D' done marker
    while True:
        pass
