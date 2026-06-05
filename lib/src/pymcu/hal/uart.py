# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import inline

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.uart import UART
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.uart import UART
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.uart import UART
else:
    raise CompileError("UART not supported on this architecture")


@inline
def uart_rx_isr():
    """Ring-buffer filler ISR. Call from within a uart.irq() handler."""
    match __CHIP__.name:
        case "attiny2313" | "attiny4313":
            from pymcu.hal.avr.attiny2313_uart import uart_rx_isr as _impl
            _impl()
        case "atmega32u4":
            from pymcu.hal.avr.atmega32u4_uart import uart_rx_isr as _impl
            _impl()
        case _:
            match __CHIP__.arch:
                case "avr":
                    from pymcu.hal.avr.avr_uart import uart_rx_isr as _impl
                    _impl()
