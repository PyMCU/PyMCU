# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline, Callable
from pymcu.chips import __CHIP__
from pymcu.hal.gpio import Pin


# noinspection PyProtectedMember
class SPI:
    """Hardware SPI controller or peripheral, zero-cost abstraction.

    Mode 0 (CPOL=0, CPHA=0), MSB-first.  The operating role is selected once
    at construction via the ``mode`` argument and baked in at compile time:

        spi = SPI()                      # controller (default)
        spi = SPI(SPI.PERIPHERAL)        # peripheral

    Controller-mode constants::

        SPI.CONTROLLER = 0
        SPI.PERIPHERAL = 1

    Controller context-manager support::

        with SPI(cs=cs_pin):
            spi.write(0xFF)

    Peripheral use::

        spi = SPI(SPI.PERIPHERAL)
        while True:
            rx = spi.exchange(0xAB)   # reply 0xAB, return what controller sent

    All methods are @inline; the ``mode`` check in each one folds away at
    compile time with no SRAM or code cost for the unused path.
    """

    CONTROLLER = 0
    PERIPHERAL = 1

    def __init__(self, mode: uint8 = 0, cs: Pin = None):
        """Initialize SPI.

        mode: SPI.CONTROLLER (0, default) or SPI.PERIPHERAL (1).
        cs:   optional chip-select Pin for controller mode (idle high).
              Ignored in peripheral mode.
        """
        match __CHIP__.arch:
            case "avr":
                match mode:
                    case 0:
                        from pymcu.hal._spi.avr import spi_init
                        spi_init()
                        self._mode = "c"
                        if cs is not None:
                            from pymcu.hal._gpio.atmega328p import select_port, select_ddr, select_bit
                            _cs_ddr = select_ddr(cs.name)
                            _cs_ddr[select_bit(cs.name)] = 1
                            self._cs_port = select_port(cs.name)
                            self._cs_bit  = select_bit(cs.name)
                            self._cs_port[self._cs_bit] = 1
                            self._cs = cs.name
                        else:
                            self._cs = ""
                    case 1:
                        from pymcu.hal._spi.avr import spi_peripheral_init
                        spi_peripheral_init()
                        self._mode = "p"
                        self._cs = ""

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Send one byte and simultaneously receive one byte.

        Controller: drives the clock; returns what the peripheral sent.
        Peripheral: preloads TX with data, blocks until controller clocks
                    a full byte, returns what the controller sent.
        """
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from pymcu.hal._spi.avr import spi_transfer
                    return spi_transfer(data)
                else:
                    from pymcu.hal._spi.avr import spi_peripheral_exchange
                    return spi_peripheral_exchange(data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        """Send one byte; the received byte is discarded (controller mode)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from pymcu.hal._spi.avr import spi_transfer
                    spi_transfer(data)

    @inline
    def receive(self) -> uint8:
        """Peripheral mode: block until controller sends a byte, return it (TX=0x00)."""
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_peripheral_receive
                return spi_peripheral_receive()
            case _:
                return 0

    @inline
    def send(self, data: uint8):
        """Peripheral mode: preload SPDR for the next controller transfer (non-blocking)."""
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_peripheral_send
                spi_peripheral_send(data)

    @inline
    def ready(self) -> uint8:
        """Return 1 if a transfer has completed (SPIF=1), else 0 (both modes)."""
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_peripheral_ready
                return spi_peripheral_ready()
            case _:
                return 0

    @inline
    def irq(self, handler: Callable):
        """Register an interrupt handler for SPI transfer-complete (STC) events.

        handler: compile-time function reference; automatically registered
                 at the SPI STC vector -- no @interrupt decorator needed.
                 The handler MUST read SPDR to clear SPIF and get the byte.

        Enables SPIE and global interrupts (SEI).
        Most useful in PERIPHERAL mode where each byte from the controller
        triggers the ISR; works in CONTROLLER mode too.
        """
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_irq_setup
                spi_irq_setup(handler)

    @inline
    def select(self):
        """Controller mode: assert the chip-select line (drive it low)."""
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    self._cs_port[self._cs_bit] = 0
                else:
                    from pymcu.hal._spi.avr import spi_select
                    spi_select()

    @inline
    def deselect(self):
        """Controller mode: deassert the chip-select line (drive it high)."""
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    self._cs_port[self._cs_bit] = 1
                else:
                    from pymcu.hal._spi.avr import spi_deselect
                    spi_deselect()

    def __enter__(self):
        """Controller mode: assert chip-select (context manager entry)."""
        self.select()

    def __exit__(self):
        """Controller mode: deassert chip-select (context manager exit)."""
        self.deselect()
