# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable


class UART:
    """Hardware UART, zero-cost abstraction (all methods @inline).

    Chip dispatch is folded at compile time via match __CHIP__.name inside each
    method, so only the code for the actual target chip is emitted.
    """

    def __init__(self, baud: const[uint16] = 9600):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_init
                uart_init(baud)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_init
                uart_init(baud)
            case _:
                from pymcu.hal.avr.avr_uart import uart_init
                uart_init(baud)

    @inline
    def write(self, data: uint8):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_write
                uart_write(data)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_write
                uart_write(data)
            case _:
                from pymcu.hal.avr.avr_uart import uart_write
                uart_write(data)

    @inline
    def read(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_read
                return uart_read()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_read
                return uart_read()
            case _:
                from pymcu.hal.avr.avr_uart import uart_read
                return uart_read()

    @inline
    def read_blocking(self) -> uint8:
        return self.read()

    @inline
    def write_hex(self, byte: uint8):
        hi: uint8 = (byte >> 4) & 0x0F
        lo: uint8 = byte & 0x0F
        if hi < 10:
            self.write(hi + 48)
        else:
            self.write(hi - 10 + 65)
        if lo < 10:
            self.write(lo + 48)
        else:
            self.write(lo - 10 + 65)

    @inline
    def write_str(self, s: const[str]):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_write_str
                uart_write_str(s)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_write_str
                uart_write_str(s)
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_str
                uart_write_str(s)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        self.write(10)

    @inline
    def print_byte(self, value: uint8):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_write_decimal_u8
                uart_write_decimal_u8(value)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_write_decimal_u8
                uart_write_decimal_u8(value)
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_decimal_u8
                uart_write_decimal_u8(value)
        self.write(10)

    @inline
    def print_uint16(self, value: uint16):
        match __CHIP__.name:
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_decimal_u16
                uart_write_decimal_u16(value)
        self.write(10)

    @inline
    def print_int16(self, value: int16):
        match __CHIP__.name:
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_decimal_i16
                uart_write_decimal_i16(value)
        self.write(10)

    @inline
    def print_uint32(self, value: uint32):
        match __CHIP__.name:
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_decimal_u32
                uart_write_decimal_u32(value)
        self.write(10)

    @inline
    def print_float(self, value: float):
        match __CHIP__.name:
            case _:
                from pymcu.hal.avr.avr_uart import uart_write_float
                uart_write_float(value)
        self.write(10)

    @inline
    def read_line(self, buf, max_len: uint8) -> uint8:
        match __CHIP__.name:
            case _:
                from pymcu.hal.avr.avr_uart import uart_read_line
                return uart_read_line(buf, max_len)
        return 0

    @inline
    def available(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_available
                return uart_available()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_available
                return uart_available()
            case _:
                from pymcu.hal.avr.avr_uart import uart_available
                return uart_available()
        return 0

    @inline
    def read_nb(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_read_nb
                return uart_read_nb()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_read_nb
                return uart_read_nb()
            case _:
                from pymcu.hal.avr.avr_uart import uart_read_nb
                return uart_read_nb()
        return 0

    @inline
    def read_byte_isr(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_read_byte_isr
                return uart_read_byte_isr()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_read_byte_isr
                return uart_read_byte_isr()
            case _:
                from pymcu.hal.avr.avr_uart import uart_read_byte_isr
                return uart_read_byte_isr()
        return 0

    @inline
    def irq(self, handler: Callable):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.chips.attiny2313 import UCSRB, SREG
                UCSRB[7] = 1
                SREG[7] = 1
                compile_isr(handler, 0x0016)
            case "atmega32u4":
                from pymcu.chips.atmega32u4 import UCSR1B, SREG
                UCSR1B[7] = 1
                SREG[7] = 1
                compile_isr(handler, 0x002C)
            case _:
                from pymcu.chips.atmega328p import UCSR0B, SREG
                UCSR0B[7] = 1
                SREG[7] = 1
                compile_isr(handler, 0x0024)

    @inline
    def enable_rx_interrupt(self):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()
            case _:
                from pymcu.hal.avr.avr_uart import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()

    @inline
    def rx_isr(self):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_rx_isr
                uart_rx_isr()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_rx_isr
                uart_rx_isr()
            case _:
                from pymcu.hal.avr.avr_uart import uart_rx_isr
                uart_rx_isr()

    @inline
    def rx_available(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_rx_available
                return uart_rx_available()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_rx_available
                return uart_rx_available()
            case _:
                from pymcu.hal.avr.avr_uart import uart_rx_available
                return uart_rx_available()
        return 0

    @inline
    def rx_read(self) -> uint8:
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_uart import uart_rx_read
                return uart_rx_read()
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_uart import uart_rx_read
                return uart_rx_read()
            case _:
                from pymcu.hal.avr.avr_uart import uart_rx_read
                return uart_rx_read()
        return 0
