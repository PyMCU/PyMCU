# machine.SPI multi-byte write / readinto / write_readinto integration test fixture
#
# Matches MicroPython API: write(buf) / readinto(buf) / write_readinto(w, r) all
# infer the byte count from the buffer length at compile time via len(buf).
#
# Protocol (command-driven loop):
#   Boot: "READY\n"
#   Cmd 'W' (0x57): spi.write(out_buf)                  -- echoes 'W' on completion
#   Cmd 'R' (0x52): spi.readinto(in_buf)                 -- echoes 3 received bytes
#   Cmd 'X' (0x58): spi.write_readinto(out_buf, in_buf)  -- echoes 'X' + 3 in bytes

from machine import SPI, UART
from pymcu.types import uint8


def main():
    uart = UART(0, 9600)
    spi = SPI()
    uart.println("READY")

    out_buf: uint8[3] = [0xAA, 0xBB, 0xCC]
    in_buf: uint8[3] = [0, 0, 0]

    while True:
        cmd: uint8 = uart.read()

        if cmd == 87:
            spi.write(out_buf)
            uart.write(87)

        if cmd == 82:
            spi.readinto(in_buf)
            j: uint8 = 0
            while j < 3:
                uart.write(in_buf[j])
                j = j + 1

        if cmd == 88:
            spi.write_readinto(out_buf, in_buf)
            uart.write(88)
            k: uint8 = 0
            while k < 3:
                uart.write(in_buf[k])
                k = k + 1
