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
from pymcu.types import uint8, const

if __CHIP__.name == "pic16f628a":
    from pymcu.hal.pic14.pic16f628a_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
elif __CHIP__.name == "pic16f877a":
    from pymcu.hal.pic14.pic16f877a_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
elif __CHIP__.name == "pic16f18877":
    from pymcu.hal.pic14.pic16f18877_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
else:
    # A part with no USART gets entry points that refuse when they are called, not a
    # refusal at import. pymcu/hal/__init__.py pulls this dispatcher into every PIC14
    # build through its UART re-export, so raising here failed programs -- a plain
    # blink among them -- that never asked for a UART at all.
    from pymcu.hal.pic14.pic14_uart_unsupported import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )


