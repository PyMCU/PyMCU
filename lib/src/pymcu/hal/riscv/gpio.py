# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V003 GPIO HAL -- pymcu.hal.riscv.gpio
#
# The QingKe GPIO block is the classic STM32F1 layout: one 4-bit CFGLR nibble
# per pin selects direction, drive and speed, and BSHR gives an atomic set/reset
# that needs no read-modify-write. Ports A, C and D exist on the CH32V003 and
# each has 8 pins, so a single CFGLR register covers a whole port.
#
# Every method is @inline and the pin name is a compile-time string, so the
# port base, the bit index and the shift amounts all fold to constants.
# -----------------------------------------------------------------------------

from pymcu.chips.ch32v003 import (
    RCC_APB2ENR,
    GPIOA_CFGLR, GPIOA_INDR, GPIOA_OUTDR, GPIOA_BSHR,
    GPIOC_CFGLR, GPIOC_INDR, GPIOC_OUTDR, GPIOC_BSHR,
    GPIOD_CFGLR, GPIOD_INDR, GPIOD_OUTDR, GPIOD_BSHR,
)
from pymcu.types import uint8, int32, ptr, const, inline
from pymcu.exceptions import CompileError

# CFGLR nibble values (CNF[1:0] << 2 | MODE[1:0]).
_CFG_IN_FLOATING = 0x4
_CFG_IN_PULL     = 0x8
_CFG_OUT_PP_10M  = 0x1

# RCC_APB2ENR clock-enable bits for each GPIO port.
_IOPAEN = 2
_IOPCEN = 4
_IOPDEN = 5


@inline
def select_cfglr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return GPIOA_CFGLR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return GPIOC_CFGLR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return GPIOD_CFGLR
        case _:
            raise CompileError('Unsupported Pin')


@inline
def select_outdr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return GPIOA_OUTDR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return GPIOC_OUTDR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return GPIOD_OUTDR
        case _:
            raise CompileError('Unsupported Pin')


@inline
def select_indr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return GPIOA_INDR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return GPIOC_INDR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return GPIOD_INDR
        case _:
            raise CompileError('Unsupported Pin')


@inline
def select_bshr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return GPIOA_BSHR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return GPIOC_BSHR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return GPIOD_BSHR
        case _:
            raise CompileError('Unsupported Pin')


@inline
def select_clock_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return _IOPAEN
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return _IOPCEN
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return _IOPDEN
        case _:
            raise CompileError('Unsupported Pin')


@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PC0' | 'PD0':
            return 0
        case 'PA1' | 'PC1' | 'PD1':
            return 1
        case 'PA2' | 'PC2' | 'PD2':
            return 2
        case 'PA3' | 'PC3' | 'PD3':
            return 3
        case 'PA4' | 'PC4' | 'PD4':
            return 4
        case 'PA5' | 'PC5' | 'PD5':
            return 5
        case 'PA6' | 'PC6' | 'PD6':
            return 6
        case 'PA7' | 'PC7' | 'PD7':
            return 7
        case _:
            raise CompileError('Unsupported Pin')


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
            raise CompileError("Open-drain mode not supported on CH32V003 yet")
        if alt != -1:
            raise CompileError("Alternate functions not supported on CH32V003 yet")
        if drive:
            raise CompileError("Drive strength control not supported on CH32V003")

        self._cfg  = select_cfglr(name)
        self._out  = select_outdr(name)
        self._in   = select_indr(name)
        self._bshr = select_bshr(name)
        self._bit  = select_bit(name)

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
        self._cfg.value = (self._cfg.value & ~(0xF << (self._bit * 4))) | (nibble << (self._bit * 4))

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
