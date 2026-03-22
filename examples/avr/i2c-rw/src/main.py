# PyMCU -- i2c-rw: I2C write_to / read_from HAL methods
#
# Demonstrates the new I2C.write_to(addr, data) and I2C.read_from(addr) methods.
# With no real I2C device attached, write_to sends START+addr+data+STOP,
# and read_from sends START+addr+read+NACK+STOP.
# Boot banner then writes a byte to address 0x48, then reads from 0x48.
#
# UART output (9600 baud):
#   "IW\n"  -- init + write_to done
#   "IR\n"  -- read_from done
#
from whisnake.types import uint8
from whisnake.hal.i2c import I2C
from whisnake.hal.uart import UART


def main():
    uart = UART(9600)
    i2c  = I2C()

    # write_to: send one data byte (0xAB) to address 0x48
    i2c.write_to(0x48, 0xAB)
    uart.write('I')
    uart.write('W')
    uart.write('\n')

    # read_from: attempt to read one byte from address 0x48
    # (With no device, NACK received -- result is 0xFF or 0x00 depending on bus)
    val: uint8 = i2c.read_from(0x48)
    uart.write('I')
    uart.write('R')
    uart.write('\n')

    while True:
        pass
