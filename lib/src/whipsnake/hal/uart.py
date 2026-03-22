# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline, const
from whipsnake.chips import __CHIP__

class UART:
    @inline
    def __init__(self, baud: const[uint16] = 9600):
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_init
                uart_init(baud)
            case "pic14":
                from whipsnake.hal._uart.pic14 import uart_init
                uart_init(baud)
            case "pic18":
                from whipsnake.hal._uart.pic18 import uart_init
                uart_init(baud)

    @inline
    def write(self, data: uint8):
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_write
                uart_write(data)
            case "pic14":
                from whipsnake.hal._uart.pic14 import uart_write
                uart_write(data)
            case "pic18":
                from whipsnake.hal._uart.pic18 import uart_write
                uart_write(data)

    @inline
    def read(self) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_read
                return uart_read()
            case "pic14":
                from whipsnake.hal._uart.pic14 import uart_read
                return uart_read()
            case "pic18":
                from whipsnake.hal._uart.pic18 import uart_read
                return uart_read()

    @inline
    def write_str(self, s: const[str]):
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_write_str
                uart_write_str(s)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        self.write(10)  # '\n'

    @inline
    def print_byte(self, value: uint8):
        # Print a uint8 value as decimal digits followed by a newline.
        # For float values: use print_fixed(int_part, dec_part) when available.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_write_decimal_u8
                uart_write_decimal_u8(value)
        self.write(10)  # '\n'

    @inline
    def read_blocking(self) -> uint8:
        # Blocking read: polls until a byte arrives (RXC0 set), then returns it.
        # This is identical to read() but named explicitly to distinguish from read_nb().
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_read
                return uart_read()
            case "pic14":
                from whipsnake.hal._uart.pic14 import uart_read
                return uart_read()
            case "pic18":
                from whipsnake.hal._uart.pic18 import uart_read
                return uart_read()

    @inline
    def available(self) -> uint8:
        # Returns 1 if a byte is waiting in the UART receive buffer (RXC bit set), 0 otherwise.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_available
                return uart_available()
        return 0

    @inline
    def read_nb(self) -> uint8:
        # Non-blocking read: returns the received byte if one is available, else 0.
        # Checks the RXC0 bit (bit 7 of UCSR0A) without blocking.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_read_nb
                return uart_read_nb()
        return 0

    @inline
    def read_byte_isr(self) -> uint8:
        # ISR-safe read: reads the byte from the UART data register directly.
        # Must only be called when RXC0 is set (e.g. from inside @interrupt USART_RX_vect).
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_read_byte_isr
                return uart_read_byte_isr()
        return 0

    @inline
    def enable_rx_interrupt(self):
        # Enable RXCIE0 in UCSR0B so the USART_RX ISR fires on each received byte.
        # After calling this, define an @interrupt(0x0024) handler that calls rx_isr().
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()

    @inline
    def rx_isr(self):
        # Call from inside the @interrupt(0x0024) USART_RX handler.
        # Reads UDR0 into the 16-byte ring buffer and advances the head index.
        # Bytes are dropped silently if the buffer is full.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_rx_isr
                uart_rx_isr()

    @inline
    def rx_available(self) -> uint8:
        # Returns 1 if at least one byte is waiting in the ring buffer, 0 otherwise.
        # Use after enable_rx_interrupt() to check before calling rx_read().
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_rx_available
                return uart_rx_available()
        return 0

    @inline
    def rx_read(self) -> uint8:
        # Read one byte from the ring buffer and advance the tail pointer.
        # Returns 0 if the buffer is empty. Check rx_available() before calling.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._uart.avr import uart_rx_read
                return uart_rx_read()
        return 0
