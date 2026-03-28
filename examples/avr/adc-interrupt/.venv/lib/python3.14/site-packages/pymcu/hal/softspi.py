# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Software SPI (bit-bang) -- architecture-independent implementation.
#
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
#
# Controller mode:
#   transfer() uses the shift-left trick: tx and result are shifted left each
#   iteration so bit 7 is always the active bit, eliminating per-bit mask
#   constants.
#
#   half_us: half-period in microseconds; computed from the baudrate (kHz)
#   parameter at construction time as 500 // baudrate.  When half_us == 0
#   (baudrate > 500 kHz) the guards fold at compile time and both delay_us
#   calls are eliminated entirely.
#
# Peripheral mode:
#   exchange() follows SCK driven by the controller.  Data is set up on MISO
#   before each rising edge and sampled from MOSI on the rising edge (Mode 0).
#   No delay_us needed -- the controller sets the pace.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, uint16, inline
from pymcu.hal.gpio import Pin
from pymcu.time import delay_us


# noinspection PyProtectedMember
class SoftSPI:
    """Bit-bang SPI controller or peripheral, zero-cost abstraction.

    Implements Mode 0 (CPOL=0, CPHA=0), MSB-first SPI in software using
    only Pin ZCA methods -- architecture-independent.

    The operating role is selected at construction via the ``mode`` argument
    and baked in at compile time (the unused code path is dead-eliminated):

        spi = SoftSPI(sck, mosi, miso)                          # controller
        spi = SoftSPI(sck, mosi, miso, mode=SoftSPI.PERIPHERAL) # peripheral

    Role constants::

        SoftSPI.CONTROLLER = 0
        SoftSPI.PERIPHERAL = 1

    Controller usage::

        spi = SoftSPI(sck_pin, mosi_pin, miso_pin, cs=cs_pin, baudrate=500)
        with spi:
            rx = spi.transfer(0xA5)

    Peripheral usage::

        spi = SoftSPI(sck_pin, mosi_pin, miso_pin, mode=SoftSPI.PERIPHERAL)
        while True:
            if spi.cs_asserted():
                rx = spi.exchange(0xAB)  # reply 0xAB, return controller byte
    """

    CONTROLLER = 0
    PERIPHERAL = 1

    def __init__(self, sck: Pin, mosi: Pin, miso: Pin, mode: uint8 = 0, cs: Pin = None, baudrate: uint16 = 500):
        """Configure the bit-bang SPI pins.

        sck, mosi, miso: Pin instances configured by the caller.
        mode:            SoftSPI.CONTROLLER (0, default) or SoftSPI.PERIPHERAL (1).
        cs:              optional chip-select Pin.
                         Controller: idle high, driven low during transfers.
                         Peripheral: monitored via cs_asserted(); ignored otherwise.
        baudrate:        target SCK frequency in kHz (controller mode only; default 500 kHz).
        """
        self._sck  = sck
        self._mosi = mosi
        self._miso = miso
        match mode:
            case 0:
                # Controller: SCK and MOSI idle low; CS idle high.
                sck.low()
                mosi.low()
                self._mode = "c"
                # Half-period in microseconds: 500 us / baudrate_kHz.
                # Folds to 0 when baudrate > 500 kHz; delay_us calls removed by DCE.
                half_us: uint8 = 500 // baudrate
                self._half_us = half_us
                if cs is not None:
                    # cs.high() idles the chip-select line high.
                    cs.high()
                    self._cs_pin = cs
                    self._cs = cs.name
                else:
                    self._cs = ""
            case 1:
                # Peripheral: MISO is our output, SCK and MOSI are inputs.
                miso.low()
                self._mode = "p"
                self._half_us = 0
                if cs is not None:
                    # cs.high() enables the internal pull-up so the CS line does not
                    # float before the controller drives it.
                    cs.high()
                    self._cs_pin = cs
                    self._cs = cs.name
                else:
                    self._cs = ""

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Controller mode: send one byte MSB-first and simultaneously receive one byte.

        Uses the shift-left trick: tx and result are shifted left each
        iteration so bit 7 is always the active bit.
        """
        if self._mode == "c":
            tx: uint8 = data
            result: uint8 = 0
            i: uint8 = 0
            while i < 8:
                if tx & 0x80:
                    self._mosi.high()
                else:
                    self._mosi.low()
                if self._half_us > 0:
                    delay_us(self._half_us)
                self._sck.high()
                result = result << 1
                if self._miso.value():
                    result = result | 1
                if self._half_us > 0:
                    delay_us(self._half_us)
                self._sck.low()
                tx = tx << 1
                i = i + 1
            return result
        return 0

    @inline
    def write(self, data: uint8):
        """Controller mode: send one byte; the received byte is discarded."""
        if self._mode == "c":
            self.transfer(data)

    @inline
    def exchange(self, data: uint8) -> uint8:
        """Peripheral mode: drive MISO with data while controller clocks 8 bits.

        Implements SPI Mode 0 (CPOL=0, CPHA=0): MISO is set up before each
        rising SCK edge and MOSI is sampled on the rising edge.  The controller
        drives the clock -- no delay_us is used.

        Returns the byte received from the controller.
        """
        if self._mode == "p":
            tx: uint8 = data
            result: uint8 = 0
            i: uint8 = 0
            while i < 8:
                # Drive MISO with current MSB before rising edge.
                if tx & 0x80:
                    self._miso.high()
                else:
                    self._miso.low()
                # Wait for SCK rising edge.
                sck_val: uint8 = self._sck.value()
                while sck_val == 0:
                    sck_val = self._sck.value()
                # Sample MOSI on rising edge.
                result = result << 1
                if self._mosi.value():
                    result = result | 1
                # Wait for SCK falling edge.
                sck_val = self._sck.value()
                while sck_val == 1:
                    sck_val = self._sck.value()
                tx = tx << 1
                i = i + 1
            return result
        return 0

    @inline
    def receive(self) -> uint8:
        """Peripheral mode: receive one byte from the controller (TX = 0x00)."""
        if self._mode == "p":
            return self.exchange(0)
        return 0

    @inline
    def cs_asserted(self) -> uint8:
        """Return 1 if the CS line is asserted (low), else 0.

        Peripheral mode only.  Returns 0 if no cs pin was configured.
        """
        if self._mode == "p":
            if self._cs != "":
                if self._cs_pin.value() == 0:
                    return 1
        return 0

    @inline
    def select(self):
        """Controller mode: assert the chip-select line low."""
        if self._mode == "c":
            if self._cs != "":
                self._cs_pin.low()

    @inline
    def deselect(self):
        """Controller mode: deassert the chip-select line high."""
        if self._mode == "c":
            if self._cs != "":
                self._cs_pin.high()

    def __enter__(self):
        """Controller mode: assert chip-select (context manager entry)."""
        self.select()

    def __exit__(self):
        """Controller mode: deassert chip-select (context manager exit)."""
        self.deselect()
