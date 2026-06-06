# machine.SPI multi-byte write / readinto / write_readinto integration test fixture
#
# MicroPython deviations covered:
#   write(buf, n)                    -- MicroPython: write(buf) infers len(buf)
#   readinto(buf, n, write_byte)     -- MicroPython: readinto(buf) fills up to len(buf)
#   write_readinto(w_buf, r_buf, n)  -- MicroPython: write_readinto(w_buf, r_buf) infers len
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'W' (0x57): spi.write(out_buf, 3)                    -- echoes 'W' on completion
#   Cmd 'R' (0x52): spi.readinto(in_buf, 3)                  -- echoes 3 received bytes
#   Cmd 'X' (0x58): spi.write_readinto(out_buf, in_buf, 3)   -- echoes 'X' + 3 in bytes

from machine import SPI, UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    spi = SPI()
    uart.println("READY")

    out_buf: bytearray = bytearray(4)
    in_buf: bytearray = bytearray(4)
    out_buf[0] = 0xAA
    out_buf[1] = 0xBB
    out_buf[2] = 0xCC

    while True:
        cmd: uint8 = uart.read()

        if cmd == 87:
            spi.write(out_buf, 3)
            uart.write(87)

        if cmd == 82:
            spi.readinto(in_buf, 3)
            j: uint8 = 0
            while j < 3:
                uart.write(in_buf[j])
                j = j + 1

        if cmd == 88:
            spi.write_readinto(out_buf, in_buf, 3)
            uart.write(88)
            k: uint8 = 0
            while k < 3:
                uart.write(in_buf[k])
                k = k + 1
