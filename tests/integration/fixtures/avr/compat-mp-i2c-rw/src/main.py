# machine.I2C multi-byte writeto / readfrom_into integration test fixture
#
# Matches MicroPython API:
#   writeto(addr, buf)      -- sends len(buf) bytes (matches MicroPython)
#   readfrom_into(addr, buf) -- fills len(buf) bytes (matches MicroPython; returns 1/0 vs None)
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'W' (0x57): writeto(0x48, buf)    -- sends 3 bytes; echoes 'W' on completion
#   Cmd 'R' (0x52): readfrom_into(0x48, buf) -- reads 3 bytes; echoes status + bytes

from machine import I2C, UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    i2c = I2C()
    uart.println("READY")

    buf: uint8[3] = [0, 0, 0]

    while True:
        cmd: uint8 = uart.read()
        if cmd == 87:
            buf[0] = 0xAA
            buf[1] = 0xBB
            buf[2] = 0xCC
            i2c.writeto(0x48, buf)
            uart.write(87)
        if cmd == 82:
            n: uint8 = i2c.readfrom_into(0x48, buf)
            uart.write(n)
            j: uint8 = 0
            while j < 3:
                uart.write(buf[j])
                j = j + 1
