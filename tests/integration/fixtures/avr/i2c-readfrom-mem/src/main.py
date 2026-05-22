# PyMCU -- i2c-readfrom-mem: I2C writeto_mem / readfrom_mem HAL methods
#
# Demonstrates I2C.writeto_mem(addr, reg, data) and
# I2C.readfrom_mem(addr, reg, buf, n).
# With no real I2C device attached the bus operations complete with NACK;
# writeto_mem returns 0 and readfrom_mem returns 0.
#
# UART output (9600 baud):
#   "WM\n"  -- writeto_mem called (result printed as W0 or W1)
#   "RM\n"  -- readfrom_mem called (result printed as R0 or R1)
#
from pymcu.types import uint8
from pymcu.hal.i2c import I2C
from pymcu.hal.uart import UART


def main():
    uart = UART(9600)
    i2c  = I2C()
    buf: uint8[3] = [0, 0, 0]

    # writeto_mem: write value 0xAB to register 0x00 on device at 0x48
    # (no device -- will NACK, returns 0)
    i2c.writeto_mem(0x48, 0x00, 0xAB)
    uart.write('W')
    uart.write('M')
    uart.write('\n')

    # readfrom_mem: read 3 bytes starting at register 0x00 from device 0x48
    # (no device -- will NACK on SLA+W, returns 0, buf unchanged)
    i2c.readfrom_mem(0x48, 0x00, buf, 3)
    uart.write('R')
    uart.write('M')
    uart.write('\n')

    while True:
        pass
