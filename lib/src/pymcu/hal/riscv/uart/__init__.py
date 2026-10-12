# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# WCH RISC-V UART facade -- pymcu.hal.riscv.uart
#
# The CH32V003's USART1, polled. Every method is @inline and lowers to volatile MMIO
# accesses; the baud divisor folds at compile time from the (const) baud and the build's
# frequency.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, int16, uint32, int32, const, inline
from pymcu.exceptions import CompileError

if __CHIP__.name == "ch32v003":
    from pymcu.hal.riscv.uart.ch32v003 import (
        uart_init, uart_write, uart_read, uart_available, uart_read_nb,
        uart_write_fmt, uart_write_byte_repr,
    )
else:
    from pymcu.hal.riscv.uart.unsupported import (
        uart_init, uart_write, uart_read, uart_available, uart_read_nb,
        uart_write_fmt, uart_write_byte_repr,
    )

from pymcu.hal.uart_text import (
    uart_write_str, uart_write_decimal_u8, uart_write_decimal_u16,
    uart_write_decimal_i16, uart_write_decimal_u32, uart_write_decimal_i32,
    uart_write_float,
)


class UART:
    """Hardware USART1, zero-cost abstraction."""

    def __init__(self, baud: const = 115200, bits: const = 8, parity: const = 0, stop: const = 1):
        # bits, parity and stop describe the frame, and this HAL sends 8N1 and nothing else.
        # Accepting them and ignoring them would run a 7E1 request as 8N1 and say nothing.
        if bits != 8 or parity != 0 or stop != 1:
            raise CompileError(
                "this UART sends 8 data bits, no parity and one stop bit, and this HAL does "
                "not program any other frame on the CH32V003. Drop the bits, parity and stop "
                "arguments.")
        uart_init(baud)

    @inline
    def write(self, data: uint8):
        uart_write(data)

    @inline
    def write(self, buf: bytearray):
        # Named 'buf', not 'data' like the uint8 overload: two @inline overloads sharing a
        # parameter name collide in the compiler's bytearray-parameter tracking.
        i: uint16 = 0
        n: uint16 = len(buf)
        while i < n:
            uart_write(buf[i])
            i = i + 1

    @inline
    def read(self) -> uint8:
        return uart_read()

    @inline
    def read_blocking(self) -> uint8:
        return uart_read()

    @inline
    def read_nb(self) -> uint8:
        return uart_read_nb()

    @inline
    def available(self) -> uint8:
        return uart_available()

    @inline
    def write_str(self, s: const[str]):
        for ch in s:
            uart_write(ch)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        uart_write(10)

    @inline
    def write_hex(self, byte: uint8):
        hi: uint8 = (byte >> 4) & 0x0F
        lo: uint8 = byte & 0x0F
        if hi < 10:
            uart_write(hi + 48)
        else:
            uart_write(hi - 10 + 65)
        if lo < 10:
            uart_write(lo + 48)
        else:
            uart_write(lo - 10 + 65)

    @inline
    def print_byte(self, value: uint8):
        uart_write_decimal_u8(value)

    @inline
    def print_uint16(self, value: uint16):
        uart_write_decimal_u16(value)

    @inline
    def print_int16(self, value: int16):
        uart_write_decimal_i16(value)

    @inline
    def print_uint32(self, value: uint32):
        uart_write_decimal_u32(value)

    @inline
    def print_int32(self, value: int32):
        uart_write_decimal_i32(value)

    @inline
    def print_float(self, value: float):
        uart_write_float(value)
        uart_write(10)

    @inline
    def read_line(self, buf: bytearray, max_len: uint8) -> uint8:
        # Reads bytes into buf until '\n' (10) or max_len-1 bytes received. CR is discarded.
        # Appends a null terminator; returns the byte count (excluding newline).
        count: uint8 = 0
        while count < max_len - 1:
            b: uint8 = uart_read()
            if b == 10:
                break
            if b != 13:
                buf[count] = b
                count = count + 1
        if count < max_len:
            buf[count] = 0
        return count
