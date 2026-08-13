# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V203 GPIO port map -- pymcu.hal.riscv.gpio.ch32v203
#
# Ports A to D, sixteen pins each. Configuration is split across two registers:
# CFGLR holds pins 0-7 and CFGHR pins 8-15, with the nibble at (pin % 8) * 4.
# INDR/OUTDR/BSHR stay 16 bits wide and are indexed by the raw pin number.
# -----------------------------------------------------------------------------

from pymcu.chips.ch32v203 import (
    RCC_APB2ENR,
    GPIOA_CFGLR, GPIOA_CFGHR, GPIOA_INDR, GPIOA_OUTDR, GPIOA_BSHR,
    GPIOB_CFGLR, GPIOB_CFGHR, GPIOB_INDR, GPIOB_OUTDR, GPIOB_BSHR,
    GPIOC_CFGLR, GPIOC_CFGHR, GPIOC_INDR, GPIOC_OUTDR, GPIOC_BSHR,
    GPIOD_CFGLR, GPIOD_CFGHR, GPIOD_INDR, GPIOD_OUTDR, GPIOD_BSHR,
)
from pymcu.types import uint8, int32, ptr, inline
from pymcu.exceptions import CompileError

# RCC_APB2PCENR clock-enable bits.
_IOPAEN = 2
_IOPBEN = 3
_IOPCEN = 4
_IOPDEN = 5


@inline
def select_cfg(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return GPIOA_CFGLR
        case 'PA8' | 'PA9' | 'PA10' | 'PA11' | 'PA12' | 'PA13' | 'PA14' | 'PA15':
            return GPIOA_CFGHR
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return GPIOB_CFGLR
        case 'PB8' | 'PB9' | 'PB10' | 'PB11' | 'PB12' | 'PB13' | 'PB14' | 'PB15':
            return GPIOB_CFGHR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return GPIOC_CFGLR
        case 'PC8' | 'PC9' | 'PC10' | 'PC11' | 'PC12' | 'PC13' | 'PC14' | 'PC15':
            return GPIOC_CFGHR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return GPIOD_CFGLR
        case 'PD8' | 'PD9' | 'PD10' | 'PD11' | 'PD12' | 'PD13' | 'PD14' | 'PD15':
            return GPIOD_CFGHR
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_cfg_shift(name: str) -> uint8:
    match name:
        case 'PA0' | 'PA8' | 'PB0' | 'PB8' | 'PC0' | 'PC8' | 'PD0' | 'PD8':
            return 0
        case 'PA1' | 'PA9' | 'PB1' | 'PB9' | 'PC1' | 'PC9' | 'PD1' | 'PD9':
            return 4
        case 'PA2' | 'PA10' | 'PB2' | 'PB10' | 'PC2' | 'PC10' | 'PD2' | 'PD10':
            return 8
        case 'PA3' | 'PA11' | 'PB3' | 'PB11' | 'PC3' | 'PC11' | 'PD3' | 'PD11':
            return 12
        case 'PA4' | 'PA12' | 'PB4' | 'PB12' | 'PC4' | 'PC12' | 'PD4' | 'PD12':
            return 16
        case 'PA5' | 'PA13' | 'PB5' | 'PB13' | 'PC5' | 'PC13' | 'PD5' | 'PD13':
            return 20
        case 'PA6' | 'PA14' | 'PB6' | 'PB14' | 'PC6' | 'PC14' | 'PD6' | 'PD14':
            return 24
        case 'PA7' | 'PA15' | 'PB7' | 'PB15' | 'PC7' | 'PC15' | 'PD7' | 'PD15':
            return 28
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_outdr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 'PA8' | 'PA9' | 'PA10' | 'PA11' | 'PA12' | 'PA13' | 'PA14' | 'PA15':
            return GPIOA_OUTDR
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 'PB8' | 'PB9' | 'PB10' | 'PB11' | 'PB12' | 'PB13' | 'PB14' | 'PB15':
            return GPIOB_OUTDR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 'PC8' | 'PC9' | 'PC10' | 'PC11' | 'PC12' | 'PC13' | 'PC14' | 'PC15':
            return GPIOC_OUTDR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 'PD8' | 'PD9' | 'PD10' | 'PD11' | 'PD12' | 'PD13' | 'PD14' | 'PD15':
            return GPIOD_OUTDR
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_indr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 'PA8' | 'PA9' | 'PA10' | 'PA11' | 'PA12' | 'PA13' | 'PA14' | 'PA15':
            return GPIOA_INDR
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 'PB8' | 'PB9' | 'PB10' | 'PB11' | 'PB12' | 'PB13' | 'PB14' | 'PB15':
            return GPIOB_INDR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 'PC8' | 'PC9' | 'PC10' | 'PC11' | 'PC12' | 'PC13' | 'PC14' | 'PC15':
            return GPIOC_INDR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 'PD8' | 'PD9' | 'PD10' | 'PD11' | 'PD12' | 'PD13' | 'PD14' | 'PD15':
            return GPIOD_INDR
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_bshr(name: str) -> ptr[int32]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 'PA8' | 'PA9' | 'PA10' | 'PA11' | 'PA12' | 'PA13' | 'PA14' | 'PA15':
            return GPIOA_BSHR
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 'PB8' | 'PB9' | 'PB10' | 'PB11' | 'PB12' | 'PB13' | 'PB14' | 'PB15':
            return GPIOB_BSHR
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 'PC8' | 'PC9' | 'PC10' | 'PC11' | 'PC12' | 'PC13' | 'PC14' | 'PC15':
            return GPIOC_BSHR
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 'PD8' | 'PD9' | 'PD10' | 'PD11' | 'PD12' | 'PD13' | 'PD14' | 'PD15':
            return GPIOD_BSHR
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_clock_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 'PA8' | 'PA9' | 'PA10' | 'PA11' | 'PA12' | 'PA13' | 'PA14' | 'PA15':
            return _IOPAEN
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 'PB8' | 'PB9' | 'PB10' | 'PB11' | 'PB12' | 'PB13' | 'PB14' | 'PB15':
            return _IOPBEN
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 'PC8' | 'PC9' | 'PC10' | 'PC11' | 'PC12' | 'PC13' | 'PC14' | 'PC15':
            return _IOPCEN
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 'PD8' | 'PD9' | 'PD10' | 'PD11' | 'PD12' | 'PD13' | 'PD14' | 'PD15':
            return _IOPDEN
        case _:
            raise CompileError('Unsupported Pin')

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PB0' | 'PC0' | 'PD0':
            return 0
        case 'PA1' | 'PB1' | 'PC1' | 'PD1':
            return 1
        case 'PA2' | 'PB2' | 'PC2' | 'PD2':
            return 2
        case 'PA3' | 'PB3' | 'PC3' | 'PD3':
            return 3
        case 'PA4' | 'PB4' | 'PC4' | 'PD4':
            return 4
        case 'PA5' | 'PB5' | 'PC5' | 'PD5':
            return 5
        case 'PA6' | 'PB6' | 'PC6' | 'PD6':
            return 6
        case 'PA7' | 'PB7' | 'PC7' | 'PD7':
            return 7
        case 'PA8' | 'PB8' | 'PC8' | 'PD8':
            return 8
        case 'PA9' | 'PB9' | 'PC9' | 'PD9':
            return 9
        case 'PA10' | 'PB10' | 'PC10' | 'PD10':
            return 10
        case 'PA11' | 'PB11' | 'PC11' | 'PD11':
            return 11
        case 'PA12' | 'PB12' | 'PC12' | 'PD12':
            return 12
        case 'PA13' | 'PB13' | 'PC13' | 'PD13':
            return 13
        case 'PA14' | 'PB14' | 'PC14' | 'PD14':
            return 14
        case 'PA15' | 'PB15' | 'PC15' | 'PD15':
            return 15
        case _:
            raise CompileError('Unsupported Pin')

