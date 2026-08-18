# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# PIC14 UART dispatcher -- selects the chip implementation at compile time via
# module-level conditional imports (same pattern as the AVR uart facade).
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, const

if __CHIP__.name == "pic16f877a":
    from pymcu.hal.pic14.pic16f877a_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
elif __CHIP__.name == "pic16f18877":
    from pymcu.hal.pic14.pic16f18877_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
else:
    raise CompileError("UART is not implemented for this PIC14 chip")


def uart_write_str(s: const[str]):
    # Non-@inline on purpose (same rationale as the AVR HAL): as a real
    # subroutine, the compiler passes the string by reference (the flash word
    # address of its RETLW table, FlashStrAddr) and this single loop is emitted
    # once and shared by every write_str/println call site. s[i] reads the
    # table entry at (s + i) via a computed GOTO (FlashLoadPtr).
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i = i + 1
        b = s[i]
