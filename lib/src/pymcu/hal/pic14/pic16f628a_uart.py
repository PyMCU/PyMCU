# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# PIC16F628A EUSART. TX is RB2 and RX is RB1, and both TRISB bits must be SET:
# on this family the peripheral only reaches the pad while the pin is configured
# as an input, the opposite of the PIC18 convention.
# -----------------------------------------------------------------------------
from pymcu.chips.pic16f628a import TXSTA, RCSTA, TXREG, RCREG, SPBRG, TRISB, PIR1
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, inline, const
from pymcu.exceptions import CompileError


@inline
def uart_init(baud: const[uint16]):
    TRISB[1] = 1
    TRISB[2] = 1
    match __FREQ__:
        case 4_000_000:
            if baud == 2400:
                SPBRG.value = 103
            elif baud == 4800:
                SPBRG.value = 51
            elif baud == 9600:
                SPBRG.value = 25
            elif baud == 19200:
                SPBRG.value = 12
            else:
                raise CompileError("PIC16F628A at 4 MHz supports 2400, 4800, 9600 and 19200 baud; 38400 lands 7% off and does not survive a real receiver")
        case 8_000_000:
            if baud == 2400:
                SPBRG.value = 207
            elif baud == 4800:
                SPBRG.value = 103
            elif baud == 9600:
                SPBRG.value = 51
            elif baud == 19200:
                SPBRG.value = 25
            elif baud == 38400:
                SPBRG.value = 12
            else:
                raise CompileError("Unsupported baud rate for the PIC16F628A at 8 MHz")
        case _:
            raise CompileError("PIC16F628A UART is calibrated for 4 MHz and 8 MHz only")
    TXSTA.value = 0x24
    RCSTA.value = 0x90


@inline
def uart_write(data: uint8):
    while TXSTA[1] == 0:
        pass
    TXREG.value = data


@inline
def uart_write_byte(data: uint8):
    TXREG.value = data


@inline
def uart_read_ready() -> uint8:
    result: uint8 = 0
    if PIR1[5] == 1:
        result = 1
    return result


@inline
def uart_read() -> uint8:
    while PIR1[5] == 0:
        pass
    result: uint8 = RCREG.value
    return result
