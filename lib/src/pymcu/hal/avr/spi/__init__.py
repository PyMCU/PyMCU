# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR SPI facade -- pymcu.hal.avr.spi
#
# Single implementation covers all AVR chips with hardware SPI.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint32, inline, Callable, const
from pymcu.hal.avr.spi.avr import (
    spi_init, spi_configure, spi_frequency, spi_select, spi_deselect, spi_transfer,
    spi_write_bytes, spi_readinto_n, spi_write_readinto_n,
    spi_peripheral_init, spi_peripheral_ready, spi_peripheral_exchange,
    spi_peripheral_receive, spi_peripheral_send, spi_irq_setup,
)


class SPI:
    """Hardware SPI controller or peripheral, zero-cost abstraction.

    spi = SPI()                  # controller (default)
    spi = SPI(SPI.PERIPHERAL)    # peripheral
    spi = SPI(cs="PB0")          # controller with explicit CS pin
    """

    CONTROLLER = 0
    PERIPHERAL = 1

    # baudrate, polarity, phase and lsb_first describe the clock and the frame. They used
    # to be nowhere: the control register was the literal for mode 0 at fosc/4, so
    # busio.SPI.configure() recorded three of them and reprogrammed none, and a display
    # asked for mode 3 at 8 MHz ran mode 0 at 4 MHz.
    def __init__(self, mode: uint8 = 0, cs: const[str] = "",
                 baudrate: const[uint32] = 4000000, polarity: const[uint8] = 0,
                 phase: const[uint8] = 0, lsb_first: const[uint8] = 0):
        self._baudrate = baudrate
        if mode == 0:
            spi_init(baudrate, polarity, phase, lsb_first)
            self._mode = "c"
            if cs != "":
                from pymcu.hal.avr.gpio import Pin as _Pin
                _r = _Pin(cs, _Pin.OUT)
                self._cs_port = _r._port
                self._cs_bit  = _r._bit
                self._cs_port[self._cs_bit] = 1
                self._cs = cs
            else:
                self._cs = ""
        elif mode == 1:
            spi_peripheral_init()
            self._mode = "p"
            self._cs = ""

    # Reprogram a bus that is already running. The pin directions are already set, so only
    # the two control registers are written.
    @inline
    def configure(self, baudrate: const[uint32] = 4000000, polarity: const[uint8] = 0,
                  phase: const[uint8] = 0, lsb_first: const[uint8] = 0):
        spi_configure(baudrate, polarity, phase, lsb_first)

    # The bit rate the hardware actually produces for what was asked: the AVR's dividers are
    # powers of two, so 3 MHz at a 16 MHz clock is 2 MHz, and reporting 3 MHz back would be a
    # number the pin never carried.
    @inline
    def frequency(self) -> uint32:
        return spi_frequency(self._baudrate)

    @inline
    def transfer(self, data: uint8) -> uint8:
        if self._mode == "c":
            return spi_transfer(data)
        else:
            return spi_peripheral_exchange(data)

    @inline
    def write(self, data: uint8):
        if self._mode == "c":
            spi_transfer(data)

    @inline
    def write_bytes(self, buf, n: uint8):
        if self._mode == "c":
            spi_write_bytes(buf, n)

    @inline
    def readinto_n(self, buf, n: uint8, write_byte: uint8):
        if self._mode == "c":
            spi_readinto_n(buf, n, write_byte)

    @inline
    def write_readinto_n(self, write_buf, read_buf, n: uint8):
        if self._mode == "c":
            spi_write_readinto_n(write_buf, read_buf, n)

    @inline
    def receive(self) -> uint8:
        return spi_peripheral_receive()

    @inline
    def send(self, data: uint8):
        spi_peripheral_send(data)

    @inline
    def ready(self) -> uint8:
        return spi_peripheral_ready()

    @inline
    def irq(self, handler: Callable):
        spi_irq_setup(handler)

    @inline
    def select(self):
        if self._cs != "":
            self._cs_port[self._cs_bit] = 0
        else:
            spi_select()

    @inline
    def deselect(self):
        if self._cs != "":
            self._cs_port[self._cs_bit] = 1
        else:
            spi_deselect()

    def __enter__(self):
        self.select()

    def __exit__(self):
        self.deselect()
