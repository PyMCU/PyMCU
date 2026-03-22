# ATmega328P: SoftSPI bit-bang SPI transfer
#
# Demonstrates:
#   - SoftSPI(sck, mosi, miso, cs): configure pins as outputs/input at init
#   - SoftSPI.write(byte): bit-bang SPI Mode 0, MSB-first
#   - with spi: auto-assert/deassert CS pin
#   - CS pin is idle high; pulled low during transfer
#
# Hardware: Arduino Uno
#   SCK  = PC0 (A0)  -- clock
#   MOSI = PC1 (A1)  -- data out
#   MISO = PC2 (A2)  -- data in (looped back to MOSI for self-test)
#   CS   = PC3 (A3)  -- chip select (active low)
#   UART TX at 9600 baud
#
# Output:
#   "SSPI\n"    -- boot banner
#   "D:XX\n"    -- byte sent via SoftSPI (0xA5 = test byte)
#   "OK\n"      -- transfer completed
#
from whisnake.types import uint8
from whisnake.hal.softspi import SoftSPI
from whisnake.hal.uart import UART


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
    spi  = SoftSPI(sck="PC0", mosi="PC1", miso="PC2", cs="PC3")

    uart.println("SSPI")

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
