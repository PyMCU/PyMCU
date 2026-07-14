# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2350 SPI HAL -- pymcu.hal.rp2350.spi (SPI0, ARM PL022 master)
#
# Same structure as the RP2040 SPI HAL; only the register addresses (from
# pymcu.chips.rp2350) and the peripheral clock differ, and the SPI pads are
# de-isolated (RP2350 pads power up with ISO=1).

from pymcu.chips.rp2350 import (
    SPI0_SSPCR0, SPI0_SSPCR1, SPI0_SSPDR, SPI0_SSPSR, SPI0_SSPCPSR,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_SPI0,
    RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, PADS_BANK0_BASE, GPIO_FUNC_SPI,
    SIO_GPIO_OUT_SET, SIO_GPIO_OUT_CLR, SIO_GPIO_OE_SET,
    SSP_SR_TNF, SSP_SR_RNE, SSP_SR_BSY,
)
from pymcu.types import ptr, uint8, uint32, const, inline

_CLK_PERI = 150000000


class SPI:
    """Hardware SPI0 (PL022) master, zero-cost abstraction."""

    def __init__(self, baud: const = 1000000, polarity: const = 0, phase: const = 0,
                 sck: const = 2, mosi: const = 3, miso: const = 4, cs: const = -1):
        self._cs = cs

        reset_mask: uint32 = (1 << RESET_SPI0) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        cpsdvsr: uint32 = 2
        scr: uint32 = (_CLK_PERI // (cpsdvsr * baud)) - 1
        SPI0_SSPCPSR.value = cpsdvsr
        SPI0_SSPCR0.value = 0x07 | (polarity << 6) | (phase << 7) | ((scr & 0xFF) << 8)
        SPI0_SSPCR1.value = 1 << 1

        # De-isolate and route SCK / MOSI / MISO to the SPI function.
        sck_pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * sck)
        sck_pad.value = 1 << 6
        mosi_pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * mosi)
        mosi_pad.value = 1 << 6
        miso_pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * miso)
        miso_pad.value = 1 << 6
        sck_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * sck + 4)
        sck_ctrl.value = GPIO_FUNC_SPI
        mosi_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * mosi + 4)
        mosi_ctrl.value = GPIO_FUNC_SPI
        miso_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * miso + 4)
        miso_ctrl.value = GPIO_FUNC_SPI

        if cs != -1:
            SIO_GPIO_OE_SET.value = 1 << cs
            SIO_GPIO_OUT_SET.value = 1 << cs

    @inline
    def transfer(self, data: uint8) -> uint8:
        while ((SPI0_SSPSR.value >> SSP_SR_TNF) & 1) == 0:
            pass
        SPI0_SSPDR.value = data
        while (SPI0_SSPSR.value >> SSP_SR_BSY) & 1:
            pass
        while ((SPI0_SSPSR.value >> SSP_SR_RNE) & 1) == 0:
            pass
        return SPI0_SSPDR.value & 0xFF

    @inline
    def write(self, data: uint8):
        self.transfer(data)

    @inline
    def select(self):
        if self._cs != -1:
            SIO_GPIO_OUT_CLR.value = 1 << self._cs

    @inline
    def deselect(self):
        if self._cs != -1:
            SIO_GPIO_OUT_SET.value = 1 << self._cs
