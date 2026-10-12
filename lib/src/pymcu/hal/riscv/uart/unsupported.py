# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# The RISC-V UART for parts whose USART this HAL does not program yet (the CH32V20x).
# The entry points refuse when they are called, so the refusal reaches a program at the
# operation it asked for instead of at the import: pymcu/hal/__init__.py re-exports every
# peripheral, so `from pymcu.hal.gpio import Pin` alone drags pymcu.hal.uart into the build.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, uint32, const, inline


@inline
def _no_usart():
    # The sentence cannot name the chip it is refusing (a raise message is built while
    # compiling and an f-string over __CHIP__.name degrades to the payload's class name), so
    # it names the part that does have a UART HAL.
    raise CompileError(
        "UART is implemented for the CH32V003 only: the CH32V20x USART has a different "
        "clock tree and its HAL has not been written.")


@inline
def uart_init(baud: const[uint32]):
    _no_usart()


@inline
def uart_write(data: uint8):
    _no_usart()


@inline
def uart_write_text_sink(data: uint8):
    # NOT an entry point: hal/uart_text.py builds its writers on one uart_write primitive and
    # they are emitted whether or not the program calls them, so the primitive they are built
    # on cannot refuse. Nothing reaches those writers without first constructing a UART or
    # calling print(), and both go through uart_init above.
    pass


@inline
def uart_read() -> uint8:
    _no_usart()
    return 0


@inline
def uart_available() -> uint8:
    _no_usart()
    return 0


@inline
def uart_read_nb() -> uint8:
    _no_usart()
    return 0


@inline
def uart_write_fmt(value: uint32, width: uint8, zero_pad: uint8, base: uint8):
    _no_usart()


@inline
def uart_write_byte_repr(value: uint8):
    _no_usart()
