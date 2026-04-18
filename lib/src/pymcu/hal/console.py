# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline, const
from pymcu.chips import __CHIP__


@inline
def print_str(s: const[str]):
    # Arch-dispatched compile-time string output.
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal._uart.avr import uart_write_str
            uart_write_str(s)
        case _:
            pass


@inline
def print_u8(value: uint8):
    # Arch-dispatched uint8 decimal output.
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal._uart.avr import uart_write_decimal_u8
            uart_write_decimal_u8(value)
        case _:
            pass
