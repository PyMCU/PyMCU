# machine.UART.readline() and readinto() integration test fixture
#
# readline(buf, max_len) -- reads until '\n'; caller provides buffer (PyMCU deviation)
# readinto(buf)          -- fills len(buf) bytes (matches MicroPython)
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'L' (0x4C): readline -> send count byte, then count bytes
#   Cmd 'I' (0x49): readinto -> echo the 3 received bytes

from machine import UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    uart.println("READY")

    line_buf: uint8[16] = bytearray(16)
    data_buf: uint8[3] = [0, 0, 0]

    while True:
        cmd: uint8 = uart.read()
        if cmd == 76:
            n: uint8 = uart.readline(line_buf, 16)
            uart.write(n)
            j: uint8 = 0
            while j < n:
                uart.write(line_buf[j])
                j = j + 1
        if cmd == 73:
            uart.readinto(data_buf)
            k: uint8 = 0
            while k < 3:
                uart.write(data_buf[k])
                k = k + 1
