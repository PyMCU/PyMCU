# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# PIC16F877A USART. Baud is the REAL baud rate (9600, 19200, ...) like the AVR
# HAL; SPBRG comes from per-frequency tables (BRGH=1, SPBRG = Fosc/(16*baud)-1)
# selected at compile time via match __FREQ__, so no runtime division is needed.
# -----------------------------------------------------------------------------
from pymcu.chips.pic16f877a import TXSTA, RCSTA, TXREG, RCREG, SPBRG, TRISC, PIR1
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, inline, const

@inline
def uart_init(baud: const[uint16]):
    # RC6 = TX (output), RC7 = RX (input)
    TRISC[6] = 0
    TRISC[7] = 1
    match __FREQ__:
        case 4_000_000:
            if baud == 9600:
                SPBRG.value = 25
            elif baud == 19200:
                SPBRG.value = 12
            elif baud == 38400:
                SPBRG.value = 6
            elif baud == 57600:
                SPBRG.value = 3
            elif baud == 115200:
                SPBRG.value = 1
        case 8_000_000:
            if baud == 9600:
                SPBRG.value = 51
            elif baud == 19200:
                SPBRG.value = 25
            elif baud == 38400:
                SPBRG.value = 12
            elif baud == 57600:
                SPBRG.value = 8
            elif baud == 115200:
                SPBRG.value = 3
        case 20_000_000:
            if baud == 9600:
                SPBRG.value = 129
            elif baud == 19200:
                SPBRG.value = 64
            elif baud == 38400:
                SPBRG.value = 32
            elif baud == 57600:
                SPBRG.value = 21
            elif baud == 115200:
                SPBRG.value = 10
        case _:
            # 16 MHz table (also the fallback for unlisted frequencies)
            if baud == 9600:
                SPBRG.value = 103
            elif baud == 19200:
                SPBRG.value = 51
            elif baud == 38400:
                SPBRG.value = 25
            elif baud == 57600:
                SPBRG.value = 16
            elif baud == 115200:
                SPBRG.value = 8
    # TXEN=1, BRGH=1 (async high speed)
    TXSTA.value = 0x24
    # SPEN=1, CREN=1 (serial port + continuous receive)
    RCSTA.value = 0x90

@inline
def uart_write(data: uint8):
    # Wait for TRMT (TSR empty), then load the transmit register.
    while TXSTA[1] == 0:
        pass
    TXREG.value = data

@inline
def uart_read() -> uint8:
    # Wait for RCIF (PIR1<5>), then read RCREG (clears RCIF).
    while PIR1[5] == 0:
        pass
    result: uint8 = RCREG.value
    return result

@inline
def uart_read_ready() -> uint8:
    result: uint8 = 0
    if PIR1[5] == 1:
        result = 1
    return result

@inline
def uart_write_byte(data: uint8):
    TXREG.value = data
