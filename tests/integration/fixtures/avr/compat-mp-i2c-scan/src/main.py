# machine.I2C.scan(buf, max_count) integration test fixture
#
# Verifies the MicroPython I2C.scan() deviation:
#   MicroPython: scan() -> list[int]  (heap allocated)
#   PyMCU:       scan(buf, max_count) -> uint8 count; caller owns buffer
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'S' (0x53): scan(buf, 8) -> send count byte, then count address bytes
#   Cmd 'C' (0x43): scan() -> send count byte only (count-only backward compat)

from machine import I2C, UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    i2c = I2C()
    uart.println("READY")

    buf: uint8[8] = bytearray(8)

    while True:
        cmd: uint8 = uart.read()
        if cmd == 83:
            n: uint8 = i2c.scan(buf, 8)
            uart.write(n)
            j: uint8 = 0
            while j < n:
                uart.write(buf[j])
                j = j + 1
        if cmd == 67:
            c: uint8 = i2c.scan()
            uart.write(c)
