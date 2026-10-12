# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V003 USART1 -- pymcu.hal.riscv.uart.ch32v003
#
# The primitives the text writers (pymcu.hal.uart_text) are built on, over the polled
# STM32-style USART: STATR flags, DATAR, BRR = fck / baud. The pins are the default
# mapping, TX = PD5 (alternate-function push-pull) and RX = PD6 (input with pull-up).
#
# This module must not import pymcu.hal.uart_text: uart_text resolves its uart_write from
# here, and the cycle would close on itself.
# -----------------------------------------------------------------------------
from pymcu.chips import __FREQ__
from pymcu.chips.ch32v003 import (
    RCC_APB2PCENR, GPIOD_CFGLR, GPIOD_OUTDR,
    USART1_STATR, USART1_DATAR, USART1_BRR, USART1_CTLR1, USART1_CTLR2, USART1_CTLR3,
    USART_STATR_RXNE, USART_STATR_TXE, USART_CTLR1_RE, USART_CTLR1_TE, USART_CTLR1_UE,
)
from pymcu.types import uint8, uint16, uint32, int32, const, inline

_IOPDEN = 5
_USART1EN = 14

# CFGLR nibble (CNF << 2 | MODE): alternate-function push-pull at 50 MHz, and input with pull.
_CFG_AF_PP_50M = 0xB
_CFG_IN_PULL = 0x8


@inline
def uart_init(baud: const):
    # Frame: 8N1 only, the reset state of CTLR1/CTLR2 (UART.__init__ refuses the rest).
    RCC_APB2PCENR.value = RCC_APB2PCENR.value | (1 << _IOPDEN) | (1 << _USART1EN)
    # PD5 = TX, PD6 = RX. A pull-up input needs its latch bit high to pull up.
    GPIOD_CFGLR.value = ((GPIOD_CFGLR.value & ~((0xF << 20) | (0xF << 24)))
                         | (_CFG_AF_PP_50M << 20) | (_CFG_IN_PULL << 24))
    GPIOD_OUTDR.value = GPIOD_OUTDR.value | (1 << 6)
    USART1_CTLR1.value = 0
    USART1_CTLR2.value = 0
    USART1_CTLR3.value = 0
    USART1_BRR.value = (__FREQ__ + baud // 2) // baud
    USART1_CTLR1.value = (1 << USART_CTLR1_UE) | (1 << USART_CTLR1_TE) | (1 << USART_CTLR1_RE)


def uart_write(data: uint8):
    while (USART1_STATR.value >> USART_STATR_TXE) & 1 == 0:
        pass
    USART1_DATAR.value = data


def uart_read() -> uint8:
    while (USART1_STATR.value >> USART_STATR_RXNE) & 1 == 0:
        pass
    return USART1_DATAR.value & 0xFF


def uart_available() -> uint8:
    return (USART1_STATR.value >> USART_STATR_RXNE) & 1


def uart_read_nb() -> uint8:
    if (USART1_STATR.value >> USART_STATR_RXNE) & 1 == 0:
        return 0
    return USART1_DATAR.value & 0xFF


def uart_write_fmt(value: int32, base: uint8, width: uint8, flags: uint8):
    zero_pad: uint8 = flags & 0x04
    pad: uint8 = 32
    if zero_pad != 0:
        pad = 48
    neg: uint8 = 0
    mag: uint32 = 0
    if (flags & 0x02) and value < 0:
        neg = 1
        mag = uint32(0 - value)
    else:
        mag = uint32(value)
    if neg != 0 and zero_pad != 0:
        uart_write(45)
        if width > 0:
            width = width - 1
    buf: uint8[32] = [0] * 32
    n: uint8 = 0
    if mag == 0:
        buf[0] = 48
        n = 1
    else:
        while mag > 0:
            d: uint8 = 0
            if base == 16:
                d = uint8(mag & 0xF)
                mag = mag >> 4
            elif base == 10:
                d = uint8(mag % 10)
                mag = mag // 10
            elif base == 8:
                d = uint8(mag & 0x7)
                mag = mag >> 3
            else:
                d = uint8(mag & 1)
                mag = mag >> 1
            if d < 10:
                buf[n] = d + 48
            elif flags & 0x01:
                buf[n] = d - 10 + 65
            else:
                buf[n] = d - 10 + 97
            n = n + 1
    if neg != 0 and zero_pad == 0:
        buf[n] = 45
        n = n + 1
    while n < width and n < 32:
        buf[n] = pad
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


def uart_write_byte_repr(b: uint8):
    # One byte of a CPython bytes/bytearray repr: printable ASCII as itself, backslash and
    # quote escaped, tab/newline/return as \t \n \r, anything else as \xNN lowercase.
    if b == 92 or b == 39:
        uart_write(92)
        uart_write(b)
    elif b == 9:
        uart_write(92)
        uart_write(116)
    elif b == 10:
        uart_write(92)
        uart_write(110)
    elif b == 13:
        uart_write(92)
        uart_write(114)
    elif b >= 32 and b <= 126:
        uart_write(b)
    else:
        uart_write(92)
        uart_write(120)
        hi: uint8 = b >> 4
        lo: uint8 = b & 0x0F
        if hi < 10:
            uart_write(48 + hi)
        else:
            uart_write(87 + hi)
        if lo < 10:
            uart_write(48 + lo)
        else:
            uart_write(87 + lo)
