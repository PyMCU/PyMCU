# ATmega328P: SoftSPI bit-bang SPI peripheral mode
#
# Demonstrates:
#   - SoftSPI(sck, mosi, miso, mode=SoftSPI.PERIPHERAL): configure as peripheral
#   - SoftSPI.exchange(reply): drive MISO with reply, return received byte
#   - SoftSPI.cs_asserted(): poll CS pin before starting a transfer
#
# Hardware: Arduino Uno
#   SCK  = PC0 (A0)  -- clock input  (driven by external controller)
#   MOSI = PC1 (A1)  -- data input   (driven by external controller)
#   MISO = PC2 (A2)  -- data output  (driven by this firmware)
#   CS   = PC3 (A3)  -- chip select input (active low from controller)
#   UART TX at 9600 baud
#
# Protocol:
#   Boot banner: "SSPIP\n"
#   Waits for CS asserted (low), then exchanges one byte:
#     - Sends 0xAB to controller via MISO
#     - Receives controller's byte via MOSI
#   Reports received byte: "R:XX\n"
#   Reports completion:    "OK\n"
#
from pymcu.types import uint8
from pymcu.hal.softspi import SoftSPI
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin


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
    sck_pin  = Pin("PC0", Pin.IN)
    mosi_pin = Pin("PC1", Pin.IN)
    miso_pin = Pin("PC2", Pin.OUT)
    cs_pin   = Pin("PC3", Pin.IN)
    spi = SoftSPI(sck=sck_pin, mosi=mosi_pin, miso=miso_pin, mode=SoftSPI.PERIPHERAL, cs=cs_pin)

    uart.println("SSPIP")

    # Wait for CS to be asserted (driven low by the controller).
    cs_state: uint8 = spi.cs_asserted()
    while cs_state == 0:
        cs_state = spi.cs_asserted()

    # Exchange one byte: reply 0xAB, receive the controller's byte.
    rx: uint8 = spi.exchange(0xAB)

    uart.write('R')
    uart.write(':')
    uart.write(nibble_hi(rx))
    uart.write(nibble_lo(rx))
    uart.write('\n')

    uart.println("OK")

    while True:
        pass
