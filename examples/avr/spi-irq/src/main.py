# ATmega328P: Hardware SPI peripheral -- interrupt-driven byte receive
#
# Demonstrates:
#   - SPI(SPI.PERIPHERAL): configure hardware SPI in peripheral mode
#   - spi.irq(handler): register ISR at SPI STC vector via compile_isr;
#     no @interrupt decorator or manual SPCR/SREG writes needed
#   - GPIOR0 atomic flag pattern: ISR stores byte, main loop reads it
#
# Hardware: Arduino Uno as SPI peripheral (connect to any SPI controller)
#   MISO = PB4 (Arduino pin 12) -- data out (peripheral drives this)
#   MOSI = PB3 (Arduino pin 11) -- data in  (controller drives this)
#   SCK  = PB5 (Arduino pin 13) -- clock    (controller drives this)
#   SS   = PB2 (Arduino pin 10) -- chip select (controller drives this)
#   UART TX at 9600 baud
#
# ISR contract: on_byte() MUST read SPDR to clear SPIF; otherwise the
# interrupt re-fires immediately. SPDR holds the byte from the controller.
#
# Output:
#   "SPII\n"    -- boot banner
#   "XX\n"      -- two hex digits for each byte received from controller
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import SPDR, GPIOR0
from pymcu.hal.spi import SPI
from pymcu.hal.uart import UART


def on_byte():
    # Reading SPDR clears SPIF and captures the received byte atomically.
    GPIOR0[0] = SPDR.value


def main():
    uart = UART(9600)
    spi  = SPI(SPI.PERIPHERAL)

    # irq() enables SPIE + SEI and places on_byte at the SPI STC vector.
    # No @interrupt decorator or asm("SEI") needed.
    spi.irq(on_byte)

    GPIOR0[0] = 0
    uart.println("SPII")

    while True:
        if GPIOR0[0] != 0:
            byte: uint8 = GPIOR0[0]
            GPIOR0[0] = 0
            uart.write_hex(byte)
            uart.write('\n')
