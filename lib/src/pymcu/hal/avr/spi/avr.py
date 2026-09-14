# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR SPI Controller/Peripheral HAL -- ATmega328P hardware SPI
#
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
#
# ATmega328P SPI pins (Arduino Uno mapping):
#   MOSI = PB3  (Arduino pin 11) -- Controller: output; Peripheral: input
#   MISO = PB4  (Arduino pin 12) -- Controller: input;  Peripheral: output
#   SCK  = PB5  (Arduino pin 13) -- Controller: output; Peripheral: input
#   SS   = PB2  (Arduino pin 10) -- Controller: output; Peripheral: input
#
# Register map (all in IN/OUT range 0x40-0x5F -> I/O offset -0x20):
#   SPCR = 0x4C  -- SPI Control Register
#   SPSR = 0x4D  -- SPI Status Register
#   SPDR = 0x4E  -- SPI Data Register (write -> TX, read -> RX)
#
# Note: SPDR.value = data correctly emits OUT 0x2E, Rn (full byte, not BitWrite).
# -----------------------------------------------------------------------------

from pymcu.chips.atmega328p import DDRB, PORTB, SPCR, SPSR, SPDR, SREG
from pymcu.types import uint8, uint32, inline, const, compile_isr, Callable
from pymcu.chips import __FREQ__
from pymcu.exceptions import CompileError


@inline
# The clock divider the SPI hardware will use for a requested bit rate: the smallest divider
# whose resulting clock does not EXCEED the request, because a peripheral rated for 1 MHz
# must not be clocked at 2. The AVR's dividers are 2, 4, 8, 16, 32, 64 and 128.
@inline
def spi_divider(baudrate: const[uint32]) -> uint8:
    if __FREQ__ // 2 <= baudrate:
        return 2
    if __FREQ__ // 4 <= baudrate:
        return 4
    if __FREQ__ // 8 <= baudrate:
        return 8
    if __FREQ__ // 16 <= baudrate:
        return 16
    if __FREQ__ // 32 <= baudrate:
        return 32
    if __FREQ__ // 64 <= baudrate:
        return 64
    return 128


# The bit rate the hardware actually produces for a request. A layer reporting `frequency`
# has to report this: asking for 1 MHz at 16 MHz gets 1 MHz exactly, asking for 3 MHz gets
# 2 MHz, and reporting back the 3 MHz that was asked for is a number the pin never carried.
@inline
def spi_frequency(baudrate: const[uint32]) -> uint32:
    return uint32(__FREQ__ // spi_divider(baudrate))


# SPCR and SPSR for a bit rate, a clock polarity and a clock phase.
#   SPCR: SPIE(7) SPE(6) DORD(5) MSTR(4) CPOL(3) CPHA(2) SPR1(1) SPR0(0)
#   SPSR: SPI2X(0)
# The divider is SPR1:0 with SPI2X doubling it: /2 /4 /8 /16 /32 /64 /128 comes out as
# (SPI2X, SPR) = (1,00) (0,00) (1,01) (0,01) (1,10) (0,10) (0,11).
@inline
def spi_spcr(baudrate: const[uint32], polarity: const[uint8], phase: const[uint8],
             lsb_first: const[uint8]) -> uint8:
    if polarity > 1 or phase > 1:
        raise CompileError(
            "SPI polarity and phase are 0 or 1 each, which together name the four SPI modes "
            "(mode 0 is polarity 0 phase 0, mode 3 is 1 and 1). Pass 0 or 1.")
    spr: uint8 = 0
    if spi_divider(baudrate) == 8 or spi_divider(baudrate) == 16:
        spr = 1
    if spi_divider(baudrate) == 32 or spi_divider(baudrate) == 64:
        spr = 2
    if spi_divider(baudrate) == 128:
        spr = 3
    return uint8(0x50 | (lsb_first << 5) | (polarity << 3) | (phase << 2) | spr)


@inline
def spi_spsr(baudrate: const[uint32]) -> uint8:
    if spi_divider(baudrate) == 2 or spi_divider(baudrate) == 8 or spi_divider(baudrate) == 32:
        return 1
    return 0


@inline
def spi_init(baudrate: const[uint32] = 4000000, polarity: const[uint8] = 0,
             phase: const[uint8] = 0, lsb_first: const[uint8] = 0):
    # MOSI (PB3), SCK (PB5), SS (PB2) -> output; MISO (PB4) -> input (HW-controlled)
    DDRB[3] = 1   # MOSI: output
    DDRB[5] = 1   # SCK:  output
    DDRB[2] = 1   # SS:   output (we drive it manually as chip-select)
    PORTB[2] = 1  # SS:   idle high (no device selected)

    # The bit rate, the mode and the bit order, all compile-time constants, so this folds to
    # the same pair of register writes the literal 0x50 was: the default 4 MHz at a 16 MHz
    # clock IS fosc/4 with SPI2X clear. They used to be nowhere, so busio.SPI.configure()
    # recorded a baudrate, a polarity and a phase and reprogrammed none of them: a display
    # asking for mode 3 at 8 MHz ran mode 0 at 4 MHz, silently.
    SPCR.value = spi_spcr(baudrate, polarity, phase, lsb_first)
    SPSR.value = spi_spsr(baudrate)


# Reprogram the bit rate, mode and bit order of a bus that is already running, which is what
# busio.SPI.configure() is for. The pin directions are already set, so only the two control
# registers are written.
@inline
def spi_configure(baudrate: const[uint32] = 4000000, polarity: const[uint8] = 0,
                  phase: const[uint8] = 0, lsb_first: const[uint8] = 0):
    SPCR.value = spi_spcr(baudrate, polarity, phase, lsb_first)
    SPSR.value = spi_spsr(baudrate)


def spi_select():
    PORTB[2] = 0  # SS low -- activate device


def spi_deselect():
    PORTB[2] = 1  # SS high -- deactivate device


def spi_transfer(data: uint8) -> uint8:
    # Writing SPDR starts the 8-clock transfer; reading it returns received byte.
    SPDR.value = data          # OUT 0x2E, Rn  -- correct full-byte write
    while SPSR[7] == 0:        # Wait for SPIF (Transfer Complete flag, bit 7)
        pass
    result: uint8 = SPDR.value  # IN Rn, 0x2E  -- reading clears SPIF
    return result


@inline
def spi_write_bytes(buf, n: uint8):
    # Send n bytes from buf[]. No return value (full-duplex receive is discarded).
    i: uint8 = 0
    while i < n:
        spi_transfer(buf[i])
        i = i + 1


@inline
def spi_readinto_n(buf, n: uint8, write_byte: uint8):
    # Receive n bytes into buf[] by clocking write_byte as dummy output.
    i: uint8 = 0
    while i < n:
        buf[i] = spi_transfer(write_byte)
        i = i + 1


@inline
def spi_write_readinto_n(write_buf, read_buf, n: uint8):
    # Full-duplex: transmit write_buf[i], receive into read_buf[i], n bytes.
    i: uint8 = 0
    while i < n:
        read_buf[i] = spi_transfer(write_buf[i])
        i = i + 1


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
