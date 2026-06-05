# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, inline, const
from pymcu.chips import __CHIP__

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.uart.avr import (
        uart_write_str,
        uart_write_decimal_u8,
        uart_write_float,
    )


@inline
def print_str(s: const[str]):
    match __CHIP__.arch:
        case "avr":
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
