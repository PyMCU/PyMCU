# machine.I2C multi-byte writeto / readfrom_into integration test fixture
#
# MicroPython deviations covered:
#   writeto(addr, buf, n)     -- MicroPython: writeto(addr, bytes_obj)
#   readfrom_into(addr, buf, n) -- MicroPython: readfrom(addr, nbytes) -> bytes
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'W' (0x57): writeto(0x48, buf, 3) -- sends 3 bytes; echoes 'W' on completion
#   Cmd 'R' (0x52): readfrom_into(0x48, buf, 3) -- reads 3 bytes; echoes them

from machine import I2C, UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    i2c = I2C()
    uart.println("READY")

    buf: uint8[8] = bytearray(8)

    while True:
        cmd: uint8 = uart.read()
        if cmd == 87:
            buf[0] = 0xAA
            buf[1] = 0xBB
            buf[2] = 0xCC
            i2c.writeto(0x48, buf, 3)
            uart.write(87)
        if cmd == 82:
            n: uint8 = i2c.readfrom_into(0x48, buf, 3)
            uart.write(n)
            j: uint8 = 0
            while j < 3:
                uart.write(buf[j])
                j = j + 1
