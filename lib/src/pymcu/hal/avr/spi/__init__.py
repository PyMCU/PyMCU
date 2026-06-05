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
from pymcu.types import uint8, inline, Callable, const
from pymcu.hal.avr.spi.avr import (
    spi_init, spi_select, spi_deselect, spi_transfer,
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

    def __init__(self, mode: uint8 = 0, cs: const[str] = ""):
        if mode == 0:
            spi_init()
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
