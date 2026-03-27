# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR SPI Controller/Peripheral HAL — ATmega328P hardware SPI
#
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
#
# ATmega328P SPI pins (Arduino Uno mapping):
#   MOSI = PB3  (Arduino pin 11) — Controller: output; Peripheral: input
#   MISO = PB4  (Arduino pin 12) — Controller: input;  Peripheral: output
#   SCK  = PB5  (Arduino pin 13) — Controller: output; Peripheral: input
#   SS   = PB2  (Arduino pin 10) — Controller: output; Peripheral: input
#
# Register map (all in IN/OUT range 0x40-0x5F → I/O offset -0x20):
#   SPCR = 0x4C  — SPI Control Register
#   SPSR = 0x4D  — SPI Status Register
#   SPDR = 0x4E  — SPI Data Register (write → TX, read → RX)
#
# Note: SPDR.value = data correctly emits OUT 0x2E, Rn (full byte, not BitWrite).
# -----------------------------------------------------------------------------

from pymcu.chips.atmega328p import DDRB, PORTB, SPCR, SPSR, SPDR, SREG
from pymcu.types import uint8, inline, compile_isr, Callable


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


# --- Peripheral (slave) mode -------------------------------------------------

@inline
def spi_peripheral_init():
    # MOSI (PB3), SCK (PB5), SS (PB2) -> input; MISO (PB4) -> output.
    DDRB[4] = 1   # MISO: output (peripheral drives MISO)
    # MOSI, SCK, SS are inputs by default after reset; no DDRB write needed.
    # SPCR = 0x40: SPE(6)=1 (enable SPI) | MSTR(4)=0 (peripheral mode)
    # DORD(5)=0 (MSB first), CPOL(3)=0, CPHA(2)=0 (mode 0), SPR[1:0]=00
    SPCR.value = 0x40


@inline
def spi_peripheral_ready() -> uint8:
    """Return 1 if the controller has completed a transfer (SPIF=1), else 0."""
    result: uint8 = SPSR[7]
    return result


@inline
def spi_peripheral_exchange(data: uint8) -> uint8:
    """Preload TX byte, wait for controller transfer, return received byte.

    Places data in SPDR so the controller will clock it out on the next
    transfer, then waits for SPIF and returns the byte the controller sent.
    """
    SPDR.value = data           # preload TX
    while SPSR[7] == 0:         # wait for SPIF
        pass
    result: uint8 = SPDR.value  # read clears SPIF
    return result


@inline
def spi_peripheral_receive() -> uint8:
    """Wait for controller transfer (TX=0x00), return received byte."""
    SPDR.value = 0              # TX placeholder (controller ignores it)
    while SPSR[7] == 0:         # wait for SPIF
        pass
    result: uint8 = SPDR.value  # read clears SPIF
    return result


@inline
def spi_peripheral_send(data: uint8):
    """Preload SPDR for the next controller transfer (non-blocking)."""
    SPDR.value = data


# --- Interrupt-driven setup --------------------------------------------------
# SPI STC vector: byte address 0x0022 (word 0x0011, .org 0x44 in vector table)

@inline
def spi_irq_setup(handler: Callable):
    SPCR[7] = 1                  # SPIE: enable SPI interrupt
    SREG[7] = 1                  # SEI: enable global interrupts
    compile_isr(handler, 0x0022) # SPI STC vector byte address
