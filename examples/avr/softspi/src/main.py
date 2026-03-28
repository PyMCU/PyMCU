# ATmega328P: SoftSPI bit-bang controller mode
#
# Demonstrates:
#   - SoftSPI(sck, mosi, miso, cs=cs): configure pins at init
#   - SoftSPI.write(byte): send one byte (received byte discarded)
#   - spi.select() / spi.deselect(): explicit CS assert/deassert
#   - CS pin is idle high; pulled low during transfer
#
# Hardware: Arduino Uno
#   SCK  = PC0 (A0)  -- clock
#   MOSI = PC1 (A1)  -- data out
#   MISO = PC2 (A2)  -- data in
#   CS   = PC3 (A3)  -- chip select (active low)
#   UART TX at 9600 baud
#
# Expected output:
#   "SSPI\n"  -- boot banner
#   "D:A5\n"  -- byte sent
#   "OK\n"    -- transfer completed
#
from pymcu.types import uint8
from pymcu.hal.softspi import SoftSPI
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin


def main():
    uart = UART(9600)
    spi = SoftSPI(Pin("PC0", Pin.OUT), Pin("PC1", Pin.OUT), Pin("PC2", Pin.IN), cs=Pin("PC3", Pin.OUT))

    uart.println("SSPI")

    test_byte: uint8 = 0xA5

    spi.select()
    spi.write(test_byte)
    spi.deselect()

    uart.write_str("D:")
    uart.write_hex(test_byte)
    uart.write('\n')
    uart.println("OK")

    while True:
        pass
