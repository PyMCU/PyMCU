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
from whipsnake.types import uint8
from whipsnake.hal.softspi import SoftSPI
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
    sck_pin  = Pin("PC0", Pin.OUT)
    mosi_pin = Pin("PC1", Pin.OUT)
    miso_pin = Pin("PC2", Pin.IN)
    cs_pin   = Pin("PC3", Pin.OUT)
    spi  = SoftSPI(sck=sck_pin, mosi=mosi_pin, miso=miso_pin, cs=cs_pin)

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
