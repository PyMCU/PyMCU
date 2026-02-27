# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR SPI Master HAL — ATmega328P hardware SPI (Mode 0, MSB-first, fosc/4)
#
# ATmega328P SPI pins (Arduino Uno mapping):
#   MOSI = PB3  (Arduino pin 11) — data to device
#   MISO = PB4  (Arduino pin 12) — data from device
#   SCK  = PB5  (Arduino pin 13) — clock
#   SS   = PB2  (Arduino pin 10) — chip select (active low)
#
# Register map (all in IN/OUT range 0x40-0x5F → I/O offset -0x20):
#   SPCR = 0x4C  — SPI Control Register
#   SPSR = 0x4D  — SPI Status Register
#   SPDR = 0x4E  — SPI Data Register (write → TX, read → RX)
#
# Note: SPDR.value = data correctly emits OUT 0x2E, Rn (full byte, not BitWrite).
# -----------------------------------------------------------------------------

from pymcu.chips.atmega328p import DDRB, PORTB, SPCR, SPSR, SPDR
from pymcu.types import uint8, inline


@inline
def spi_init():
    # MOSI (PB3), SCK (PB5), SS (PB2) → output; MISO (PB4) → input (HW-controlled)
    DDRB[3] = 1   # MOSI: output
    DDRB[5] = 1   # SCK:  output
    DDRB[2] = 1   # SS:   output (we drive it manually as chip-select)
    PORTB[2] = 1  # SS:   idle high (no device selected)

    # SPCR = 0x50: SPE(6)=1 (enable SPI) | MSTR(4)=1 (master mode)
    # DORD(5)=0 (MSB first), CPOL(3)=0, CPHA(2)=0 (mode 0), SPR[1:0]=00 (fosc/4)
    SPCR.value = 0x50


@inline
def spi_select():
    PORTB[2] = 0  # SS low — activate device


@inline
def spi_deselect():
    PORTB[2] = 1  # SS high — deactivate device


@inline
def spi_transfer(data: uint8) -> uint8:
    # Writing SPDR starts the 8-clock transfer; reading it returns received byte.
    SPDR.value = data          # OUT 0x2E, Rn  — correct full-byte write
    while SPSR[7] == 0:        # Wait for SPIF (Transfer Complete flag, bit 7)
        pass
    result: uint8 = SPDR.value  # IN Rn, 0x2E  — reading clears SPIF
    return result
