# PyMCU -- spi-cs: SPI with custom chip-select pin
#
# Demonstrates:
#   - SPI(cs="PB0"): custom CS pin; idle HIGH, asserted LOW during transfer
#   - SPI.__enter__/__exit__ auto-assert/deassert via with statement
#   - CS pin is zero-cost: compile-time string fold eliminates overhead
#
# Hardware: Arduino Uno
#   PB0 (digital 8) = custom CS pin (drives DAC or external device)
#   MOSI PB3, SCK PB5 = SPI data/clock
#
# Output on UART (9600 baud):
#   "SCS\n"    -- boot banner (SPI CS Started)
#   "D:XX\n"   -- byte XX sent via SPI (0xA5 = test byte)
#   "OK\n"     -- CS init succeeded (CS pin idle high after init)
#
from whipsnake.types import uint8
from whipsnake.hal.spi import SPI
from whipsnake.hal.uart import UART
from whipsnake.hal.gpio import Pin


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart = UART(9600)
    spi = SPI(cs="PB0")

    uart.println("SCS")

    test_byte: uint8 = 0xA5

    with spi:
        spi.write(test_byte)

    uart.write('D')
    uart.write(':')
    uart.write(nibble_hi(test_byte))
    uart.write(nibble_lo(test_byte))
    uart.write('\n')

    uart.println("OK")

    while True:
        pass
