# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# The PIC14 UART for parts that have no USART. The entry points refuse when they
# are called, so the refusal reaches a program at the operation it asked for
# instead of at the import.
#
# pymcu/hal/__init__.py re-exports all five peripherals, so `from pymcu.hal.gpio
# import Pin` alone drags pymcu.hal.uart and this dispatcher into any PIC14 build.
# The refusal used to sit at the module level of pic14_uart.py, where it took down
# a plain LED blink for the PIC16F84A over a peripheral the program never mentions.
# The sibling pic14 facades -- adc.py, pwm.py, timer.py -- already dispatch inside
# the class body for exactly this reason; the UART was the one that did not.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, const


@inline
def _no_usart():
    # The one line in this module a reader is ever meant to see. The entry points
    # funnel here so the sentence is written once and cannot drift apart.
    # The sentence cannot name the chip it is refusing: a raise message is built while
    # compiling, and an f-string over __CHIP__.name degrades to the payload's class name,
    # so the reader gets "CompileError: CompileError" and nothing else. It therefore names
    # the parts that DO have a USART, which is the list that answers "what do I do now",
    # and treats the 16F84A as an example rather than as the whole of the else arm.
    raise CompileError(
        "this PIC14 chip has no hardware UART, so pymcu.hal.uart cannot drive one and "
        "print() has nowhere to write. The PIC14 parts whose USART this HAL programs are "
        "the PIC16F628A, the 16F877A and the 16F18877; every other PIC14 part, the "
        "16F84A among them, reaches this refusal. Use one of those, or carry the data "
        "over another peripheral this chip has.")


@inline
def uart_init(baud: const[uint16]):
    _no_usart()


@inline
def uart_write(data: uint8):
    _no_usart()


@inline
def uart_write_text_sink(data: uint8):
    # NOT an entry point. hal/uart_text.py builds uart_write_str and the decimal writers
    # on one uart_write primitive, and those writers are deliberately not @inline: they
    # are emitted as shared subroutines whether or not the program calls them. Lowering
    # their bodies lowers this call, so the primitive THEY are built on cannot refuse --
    # a raise here takes down a blink that never asked for a UART, which is the bug this
    # module exists to fix.
    #
    # The refusal lives on uart_write above instead, which is the name the dispatcher
    # re-exports and the only one user code and UART.write ever reach. Nothing can call
    # this sink except those library writers, and nothing can reach those writers without
    # first constructing a UART or calling print(), both of which go through uart_init.
    pass


@inline
def uart_read() -> uint8:
    _no_usart()
    return 0


@inline
def uart_read_ready() -> uint8:
    _no_usart()
    return 0


@inline
def uart_write_byte(data: uint8):
    _no_usart()
