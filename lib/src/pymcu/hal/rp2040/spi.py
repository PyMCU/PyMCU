# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 SPI HAL -- pymcu.hal.rp2040.spi (SPI0, ARM PL022 master)
#
# Zero-cost: every register is a fixed MMIO address, so every method folds to a
# volatile load/store. Default pins are the SPI0 group GP2=SCK, GP3=MOSI(TX),
# GP4=MISO(RX); a CS pin is managed in software when provided.

from pymcu.chips.rp2040 import (
    SPI0_SSPCR0, SPI0_SSPCR1, SPI0_SSPDR, SPI0_SSPSR, SPI0_SSPCPSR,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_SPI0,
    RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, GPIO_FUNC_SPI,
    SIO_GPIO_OUT_SET, SIO_GPIO_OUT_CLR, SIO_GPIO_OE_SET,
    SSP_SR_TNF, SSP_SR_RNE, SSP_SR_BSY,
)
from pymcu.types import ptr, uint8, uint32, const, inline

# Peripheral clock used for the baud divisor (pico-sdk default 125 MHz).
_CLK_PERI = 125000000


class SPI:
    """Hardware SPI0 (PL022) master, zero-cost abstraction."""

    def __init__(self, baud: const = 1000000, polarity: const = 0, phase: const = 0,
                 sck: const = 2, mosi: const = 3, miso: const = 4, cs: const = -1):
        self._cs = cs

        # Bring SPI0, IO_BANK0 and PADS_BANK0 out of reset.
        reset_mask: uint32 = (1 << RESET_SPI0) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        self.configure(baud, polarity, phase)

        # Route SCK / MOSI / MISO to the SPI function.
        sck_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * sck + 4)
        sck_ctrl.value = GPIO_FUNC_SPI
        # A pin of -1 is not routed (a write-only or read-only bus).
        if mosi != -1:
            mosi_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * mosi + 4)
            mosi_ctrl.value = GPIO_FUNC_SPI
        if miso != -1:
            miso_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * miso + 4)
            miso_ctrl.value = GPIO_FUNC_SPI

        # Software CS as a GPIO output, idle high.
        if cs != -1:
            SIO_GPIO_OE_SET.value = 1 << cs
            SIO_GPIO_OUT_SET.value = 1 << cs

    @inline
    def configure(self, baud: uint32, polarity: uint8, phase: uint8):
        # Reprogram the clock rate and mode on an already-built bus (what
        # busio.SPI.configure() needs: CircuitPython builds the bus once and
        # reconfigures it per device, after locking). SSE (CR1 bit1) has to
        # be off while CR0/CPSDVSR change, the same disable/program/enable
        # shape __init__ used inline before this method existed to share it.
        SPI0_SSPCR1.value = 0
        # Baud: SSPCLK / (CPSDVSR * (1 + SCR)). CPSDVSR fixed at 2 (the
        # smallest legal, even value); SCR is the only other divisor and is
        # an 8-bit field (0-255), so a rate below the slowest it reaches
        # clamps to 255 (as slow as the hardware goes) and one above the
        # fastest clamps to 0 (as fast as the hardware goes) -- rather than
        # the unsigned (_CLK_PERI // huge_divisor) - 1 underflowing to a
        # near-4-billion SCR for the first case, or dividing by a divisor
        # bigger than _CLK_PERI and silently keeping whatever SCR a stale
        # register held for the second.
        cpsdvsr: uint32 = 2
        divisor: uint32 = cpsdvsr * baud
        scr: uint32 = 0
        if divisor > 0 and divisor <= _CLK_PERI:
            scr = (_CLK_PERI // divisor) - 1
            if scr > 255:
                scr = 255
        SPI0_SSPCPSR.value = cpsdvsr
        # CR0: DSS=8 bits (0x7), Motorola frame, SPO=polarity, SPH=phase, SCR[15:8].
        SPI0_SSPCR0.value = 0x07 | (polarity << 6) | (phase << 7) | ((scr & 0xFF) << 8)
        # CR1: enable (SSE bit1), master (MS bit2 = 0).
        SPI0_SSPCR1.value = 1 << 1

    @inline
    def frequency(self) -> uint32:
        # The rate actually programmed, not necessarily the one asked for:
        # CPSDVSR*(1+SCR) are the only divisors this hardware has, so a rate
        # that does not land exactly on one rounds DOWN to the nearest one
        # at or below it (the `- 1` and integer floor-division in configure()
        # above). Read back from the live registers rather than kept in a
        # second copy that could drift from what configure() last wrote.
        cpsdvsr: uint32 = SPI0_SSPCPSR.value
        scr: uint32 = (SPI0_SSPCR0.value >> 8) & 0xFF
        return _CLK_PERI // (cpsdvsr * (scr + 1))

    @inline
    def transfer(self, data: uint8) -> uint8:
        # Wait for TX FIFO space, push the byte, then wait for the reply.
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
