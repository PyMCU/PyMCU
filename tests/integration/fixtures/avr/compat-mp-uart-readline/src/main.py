# machine.UART.readline() and readinto() integration test fixture
#
# Verifies two MicroPython UART read deviations:
#   readline(buf, max_len) -- reads until '\n'; caller provides buffer
#   readinto(buf, nbytes)  -- reads exactly nbytes bytes
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'L' (0x4C): readline -> send count byte, then count bytes
#   Cmd 'I' (0x49): readinto(3) -> echo the 3 received bytes

from machine import UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    uart.println("READY")

    buf: uint8[16] = bytearray(16)

    while True:
        cmd: uint8 = uart.read()
        if cmd == 76:
            n: uint8 = uart.readline(buf, 16)
            uart.write(n)
            j: uint8 = 0
            while j < n:
                uart.write(buf[j])
                j = j + 1
        if cmd == 73:
            uart.readinto(buf, 3)
            k: uint8 = 0
            while k < 3:
                uart.write(buf[k])
                k = k + 1
