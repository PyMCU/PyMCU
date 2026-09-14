# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# USART frame format -- pymcu.hal.avr.uart.frame
#
# The data bits, parity and stop bits of a UART frame, turned into the one register
# every AVR USART configures them with (UCSRnC). Every chip module in this package
# calls this, so the encoding lives once.
#
# The parity codes are the HAL's, not the AVR's: 0 none, 1 even, 2 odd. Every
# architecture's uart_init takes the same three numbers and resolves them its own way.
#
# It used to be the literal 0x06 in three chip modules, so bits, parity and stop were
# accepted by every layer above and thrown away: a program that asked for 7E1 ran 8N1
# and the frames went out wrong with nothing said.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, inline, const
from pymcu.exceptions import CompileError


@inline
def uart_frame_ucsrc(bits: const[uint8], parity: const[uint8], stop: const[uint8]) -> uint8:
    # UCSRnC, asynchronous mode (UMSELn1:0 = 00, bits 7:6):
    #   bits 5:4  UPMn1:0   00 none, 10 even, 11 odd
    #   bit  3    USBSn     0 = one stop bit, 1 = two
    #   bits 2:1  UCSZn1:0  00 = 5 data bits, 01 = 6, 10 = 7, 11 = 8
    #   bit  0    UCPOLn    0 in asynchronous mode
    # Every argument is a compile-time constant, so this folds to one register write.
    if bits == 9:
        # Nine data bits put the ninth in UCSZn2 (in UCSRnB) and carry it in RXB8/TXB8, so
        # every read and every write would need a second register access. Nothing in PyMCU
        # speaks that frame, and pretending to would be worse than saying so.
        raise CompileError(
            "a 9-bit UART frame is not supported. The ninth bit lives in a different "
            "register from the other eight (UCSZn2 in UCSRnB, then RXB8/TXB8 per byte), and "
            "this HAL reads and writes one register per byte. Use 8 data bits, and carry a "
            "ninth bit of your own in the payload if the protocol needs one.")
    if bits < 5 or bits > 8:
        raise CompileError(
            "this UART frame size is not supported. The AVR USART takes 5, 6, 7 or 8 data "
            "bits (and a 9-bit frame this HAL does not speak). Pass one of those.")
    if stop != 1 and stop != 2:
        raise CompileError(
            "this UART stop-bit count is not supported. The AVR USART sends one stop bit or "
            "two. Pass 1 or 2.")
    if parity > 2:
        raise CompileError(
            "this UART parity is not supported. The AVR USART does none (0), even (1) or "
            "odd (2). Pass one of those, or None for no parity.")

    # parity 0 -> UPM 00, 1 -> UPM 10, 2 -> UPM 11: the high bit is "parity at all" and the
    # low bit is "odd", which is exactly parity + 1 for the two that use it.
    if parity == 0:
        return uint8(((stop - 1) << 3) | ((bits - 5) << 1))
    return uint8(((parity + 1) << 4) | ((stop - 1) << 3) | ((bits - 5) << 1))
