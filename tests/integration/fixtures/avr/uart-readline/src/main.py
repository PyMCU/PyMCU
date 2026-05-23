# PyMCU -- uart-readline: UART.read_line() echo fixture
#
# Output on UART (9600 baud):
#   "RL\n"      -- boot banner
#   <N>         -- length of received line (as raw byte)
#   <echoed>    -- the bytes of the received line
#
# The test injects a line over the virtual serial port and verifies
# that the correct bytes are echoed back with a length prefix.
#
from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    uart = UART(9600)
    uart.println("RL")

    buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]
    while True:
        n: uint8 = uart.read_line(buf, 8)
        uart.write(n)
        i: uint8 = 0
        while i < n:
            uart.write(buf[i])
            i = i + 1
