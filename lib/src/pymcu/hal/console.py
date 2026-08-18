# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, inline, const
from pymcu.chips import __CHIP__

if __CHIP__.name == "rp2040" or __CHIP__.name == "rp2350":
    from pymcu.hal.rp.console import uart_write, uart_write_fmt

from pymcu.hal.uart_text import (
    uart_write_str, uart_write_decimal_u8, uart_write_decimal_u16,
    uart_write_decimal_i16, uart_write_decimal_u32, uart_write_decimal_i32, uart_write_float,
)


@inline
def print_str(s: const[str]):
    # Only the targets that import uart_write_str (avr, rp2040, rp2350, pic14,
    # pic18) write the literal; other arches drop it (no-op) rather than
    # failing to link.
    if __CHIP__.arch == "avr":
        uart_write_str(s)
    elif __CHIP__.name == "rp2040":
        uart_write_str(s)
    elif __CHIP__.name == "rp2350":
        uart_write_str(s)
    elif __CHIP__.arch == "pic14":
        uart_write_str(s)
    elif __CHIP__.arch == "pic18":
        uart_write_str(s)


@inline
def print_u8(value: uint8):
    match __CHIP__.arch:
        case "avr":
            uart_write_decimal_u8(value)


@inline
def print_float(value: float):
    match __CHIP__.arch:
        case "avr":
            uart_write_float(value)
        case "pic18":
            uart_write_float(value)
