# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, inline, Callable, const


class SPI:
    """Hardware SPI controller or peripheral, zero-cost abstraction.

    Mode 0 (CPOL=0, CPHA=0), MSB-first.

        spi = SPI()                  # controller (default)
        spi = SPI(SPI.PERIPHERAL)    # peripheral
        spi = SPI(cs="PB0")          # controller with explicit CS pin
    """

    CONTROLLER = 0
    PERIPHERAL = 1

    def __init__(self, mode: uint8 = 0, cs: const[str] = ""):
        if mode == 0:
            from pymcu.hal.avr.avr_spi import spi_init
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
            from pymcu.hal.avr.avr_spi import spi_peripheral_init
            spi_peripheral_init()
            self._mode = "p"
            self._cs = ""

    @inline
    def transfer(self, data: uint8) -> uint8:
        if self._mode == "c":
            from pymcu.hal.avr.avr_spi import spi_transfer
            return spi_transfer(data)
        else:
            from pymcu.hal.avr.avr_spi import spi_peripheral_exchange
            return spi_peripheral_exchange(data)

    @inline
    def write(self, data: uint8):
        if self._mode == "c":
            from pymcu.hal.avr.avr_spi import spi_transfer
            spi_transfer(data)

    @inline
    def receive(self) -> uint8:
        from pymcu.hal.avr.avr_spi import spi_peripheral_receive
        return spi_peripheral_receive()

    @inline
    def send(self, data: uint8):
        from pymcu.hal.avr.avr_spi import spi_peripheral_send
        spi_peripheral_send(data)

    @inline
    def ready(self) -> uint8:
        from pymcu.hal.avr.avr_spi import spi_peripheral_ready
        return spi_peripheral_ready()

    @inline
    def irq(self, handler: Callable):
        from pymcu.hal.avr.avr_spi import spi_irq_setup
        spi_irq_setup(handler)

    @inline
    def select(self):
        if self._cs != "":
            self._cs_port[self._cs_bit] = 0
        else:
            from pymcu.hal.avr.avr_spi import spi_select
            spi_select()

    @inline
    def deselect(self):
        if self._cs != "":
            self._cs_port[self._cs_bit] = 1
        else:
            from pymcu.hal.avr.avr_spi import spi_deselect
            spi_deselect()

    def __enter__(self):
        self.select()

    def __exit__(self):
        self.deselect()
