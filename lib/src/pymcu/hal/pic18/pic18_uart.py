# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# PIC18 UART dispatcher -- selects the chip implementation at compile time via
# module-level conditional imports (same pattern as the AVR and PIC14 facades).
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, int16, uint32, int32, const

if __CHIP__.name == "pic18f45k50":
    from pymcu.hal.pic18.pic18f45k50_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
else:
    raise CompileError("UART is not implemented for this PIC18 chip")


