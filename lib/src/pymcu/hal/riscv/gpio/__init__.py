# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# WCH RISC-V GPIO facade -- pymcu.hal.riscv.gpio
#
# The QingKe GPIO block is the classic STM32F1 layout: a 4-bit CFGLR/CFGHR
# nibble per pin selects direction, drive and speed, and BSHR gives an atomic
# set/reset that needs no read-modify-write.
#
# The per-chip modules below differ only in their port map -- the CH32V003 has
# three 8-pin ports, the CH32V203 four 16-pin ones whose configuration spans two
# registers. Module-level conditional imports pick the winning one at compile
# time, so only that chip's tables reach the dependency graph.
#
# Every method is @inline and the pin name is a compile-time string, so the
# register address, the bit index and the shift all fold to constants.
# -----------------------------------------------------------------------------

from pymcu.chips import __CHIP__
from pymcu.types import uint8, const, inline
from pymcu.exceptions import CompileError

if __CHIP__.name == "ch32v003":
    from pymcu.hal.riscv.gpio.ch32v003 import (
        select_cfg, select_cfg_shift, select_outdr, select_indr, select_bshr,
        select_clock_bit, select_bit, RCC_APB2ENR,
    )
elif __CHIP__.name == "ch32v203":
    from pymcu.hal.riscv.gpio.ch32v203 import (
        select_cfg, select_cfg_shift, select_outdr, select_indr, select_bshr,
        select_clock_bit, select_bit, RCC_APB2ENR,
    )

# CFGLR/CFGHR nibble values (CNF[1:0] << 2 | MODE[1:0]).
_CFG_IN_FLOATING = 0x4
_CFG_IN_PULL     = 0x8
_CFG_OUT_PP_10M  = 0x1


class Pin:
    IN  = 1
    OUT = 0
    OPEN_DRAIN = 2

    PULL_UP   = 1
    PULL_DOWN = 2

    def __init__(self, name: str, mode: const[uint8], pull: const[uint8] = -1,
                 value: const = -1, drive: const = 0, alt: const = -1):
        self.name = name
        if mode == 2:
            raise CompileError("Open-drain mode not supported on CH32V yet")
        if alt != -1:
            raise CompileError("Alternate functions not supported on CH32V yet")
        if drive:
            raise CompileError("Drive strength control not supported on CH32V")

        self._cfg   = select_cfg(name)
        self._shift = select_cfg_shift(name)
        self._out   = select_outdr(name)
        self._in    = select_indr(name)
        self._bshr  = select_bshr(name)
        self._bit   = select_bit(name)

        # A GPIO port is dead until its APB2 clock is running.
        RCC_APB2ENR.value = RCC_APB2ENR.value | (1 << select_clock_bit(name))

        if mode == 0:
            self._configure(_CFG_OUT_PP_10M)
        elif pull != -1:
            self._configure(_CFG_IN_PULL)
            # With CNF=10 the OUTDR bit picks the direction of the pull.
            if pull == 1:
                self._bshr.value = 1 << self._bit
            else:
                self._bshr.value = 1 << (self._bit + 16)
        else:
            self._configure(_CFG_IN_FLOATING)

        if value != -1:
            if value == 0:
                self.low()
            else:
                self.high()

    @inline
    def _configure(self, nibble: const[uint8]):
        self._cfg.value = (self._cfg.value & ~(0xF << self._shift)) | (nibble << self._shift)

    @inline
    def high(self):
        self._bshr.value = 1 << self._bit

    @inline
    def low(self):
        # The upper half of BSHR is the reset half.
        self._bshr.value = 1 << (self._bit + 16)

    @inline
    def on(self):
        self.high()

    @inline
    def off(self):
        self.low()

    @inline
    def toggle(self):
        self._out.value = self._out.value ^ (1 << self._bit)

    @inline
    def value(self, x: const = -1) -> uint8:
        if x == -1:
            return (self._in.value >> self._bit) & 1
        elif x == 0:
            self.low()
        else:
            self.high()

    @inline
    def init(self, mode: const = -1, pull: const = -1, value: const = -1):
        if mode == 0:
            self._configure(_CFG_OUT_PP_10M)
        elif mode == 1:
            self._configure(_CFG_IN_FLOATING)
        if value != -1:
            if value == 0:
                self.low()
            else:
                self.high()

    @inline
    def mode(self, m: const = -1):
        if m == 0:
            self._configure(_CFG_OUT_PP_10M)
        elif m == 1:
            self._configure(_CFG_IN_FLOATING)
