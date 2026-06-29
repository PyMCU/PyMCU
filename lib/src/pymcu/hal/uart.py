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
    from pymcu.hal.avr.uart import uart_rx_isr as _avr_uart_rx_isr
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.uart import UART
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.uart import UART
elif __CHIP__.name == "rp2040":
    from pymcu.hal.rp2040.uart import UART
elif __CHIP__.name == "rp2350":
    from pymcu.hal.rp2350.uart import UART
else:
    raise CompileError("UART not supported on this architecture")


@inline
def uart_rx_isr():
    match __CHIP__.arch:
        case "avr":
            _avr_uart_rx_isr()
