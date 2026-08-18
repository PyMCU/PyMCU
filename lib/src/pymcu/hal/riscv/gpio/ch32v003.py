# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V003 GPIO port map -- pymcu.hal.riscv.gpio.ch32v003
#
# Ports A, C and D, eight pins each, so a single CFGLR covers a whole port and
# the configuration nibble sits at bit*4.
# -----------------------------------------------------------------------------

from pymcu.chips.ch32v003 import (
    RCC_APB2PCENR,
    GPIOA_CFGLR, GPIOA_INDR, GPIOA_OUTDR, GPIOA_BSHR,
    GPIOC_CFGLR, GPIOC_INDR, GPIOC_OUTDR, GPIOC_BSHR,
    GPIOD_CFGLR, GPIOD_INDR, GPIOD_OUTDR, GPIOD_BSHR,
)
from pymcu.types import uint8, int32, ptr, inline
from pymcu.exceptions import CompileError

# RCC_APB2PCENR clock-enable bits.
_IOPAEN = 2
_IOPCEN = 4
_IOPDEN = 5


@inline
def select_cfg(name: str) -> ptr[int32]:
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
def select_cfg_shift(name: str) -> uint8:
    match name:
        case 'PA0' | 'PC0' | 'PD0':
            return 0
        case 'PA1' | 'PC1' | 'PD1':
            return 4
        case 'PA2' | 'PC2' | 'PD2':
            return 8
        case 'PA3' | 'PC3' | 'PD3':
            return 12
        case 'PA4' | 'PC4' | 'PD4':
            return 16
        case 'PA5' | 'PC5' | 'PD5':
            return 20
        case 'PA6' | 'PC6' | 'PD6':
            return 24
        case 'PA7' | 'PC7' | 'PD7':
            return 28
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

