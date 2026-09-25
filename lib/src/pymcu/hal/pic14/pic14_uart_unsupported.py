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
    raise CompileError(
        "this chip has no hardware UART. The PIC16F84A has no USART peripheral, so "
        "pymcu.hal.uart cannot drive one, and print() has nowhere to write. Use a part "
        "that has one (the PIC16F628A, 16F877A and 16F18877 do), or carry the data over "
        "another peripheral this chip has.")


@inline
def uart_init(baud: const[uint16]):
    _no_usart()


@inline
def uart_write(data: uint8):
    # The one entry point that cannot refuse, and the reason is worth knowing before
    # changing it. uart_write_str in hal/uart_text.py is deliberately NOT @inline: it
    # is emitted once as a shared subroutine whether or not the program calls it, and
    # lowering its body lowers this call. A raise here fires on that library function
    # and takes down the same blink all over again.
    #
    # Inert is not silent here. Every way to reach a byte on the wire goes through
    # uart_init first -- UART.__init__ calls it, and print() is the driver injecting
    # UART(baud) as _pymcu_stdout -- and uart_init refuses. Nothing that survives the
    # gate can arrive at this line, and test_a_uartless_pic14_refuses_only_on_use.py
    # pins the gate rather than leaving it as an argument.
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
