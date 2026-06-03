# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

class UART:
    """Hardware UART, zero-cost abstraction (all methods @inline).

    Provides byte-level and string transmit/receive over the hardware
    UART peripheral. Supports both polling and interrupt-driven receive
    with a 16-byte ring buffer.

    Usage::

        uart = UART(9600)
        uart.write(0x41)         # send 'A'
        b: uint8 = uart.read()   # blocking receive
    """

    def __init__(self, baud: const[uint16] = 9600):
        """Initialize the UART peripheral at the given baud rate."""
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_init
                uart_init(baud)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_init
                uart_init(baud)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_init
                        uart_init(baud)
                    case "pic14":
                        from pymcu.hal._uart.pic14 import uart_init
                        uart_init(baud)
                    case "pic18":
                        from pymcu.hal._uart.pic18 import uart_init
                        uart_init(baud)
                    case _:
                        raise CompileError("UART not supported on this architecture")

    @inline
    def write(self, data: uint8):
        """Transmit one byte, blocking until the transmit buffer is ready."""
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write
                uart_write(data)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write
                uart_write(data)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write
                        uart_write(data)
                    case "pic14":
                        from pymcu.hal._uart.pic14 import uart_write
                        uart_write(data)
                    case "pic18":
                        from pymcu.hal._uart.pic18 import uart_write
                        uart_write(data)
                    case _:
                        raise CompileError("UART not supported on this architecture")

    @inline
    def read(self) -> uint8:
        """Receive one byte, blocking until data is available."""
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_read
                return uart_read()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_read
                return uart_read()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_read
                        return uart_read()
                    case "pic14":
                        from pymcu.hal._uart.pic14 import uart_read
                        return uart_read()
                    case "pic18":
                        from pymcu.hal._uart.pic18 import uart_read
                        return uart_read()
                    case _:
                        raise CompileError("UART not supported on this architecture")

    @inline
    def write_str(self, s: const[str]):
        """Transmit a compile-time constant string byte by byte."""
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write_str
                uart_write_str(s)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write_str
                uart_write_str(s)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write_str
                        uart_write_str(s)

    @inline
    def println(self, s: const[str]):
        """Transmit a string followed by a newline character."""
        self.write_str(s)
        self.write(10)  # '\n'

    @inline
    def write_hex(self, byte: uint8):
        """Transmit a byte as two uppercase hex digits (e.g. 0x2F -> "2F")."""
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
    def print_byte(self, value: uint8):
        """Transmit a uint8 value as ASCII decimal digits, followed by a newline."""
        # Print a uint8 value as decimal digits followed by a newline.
        # For float values: use print_fixed(int_part, dec_part) when available.
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write_decimal_u8
                uart_write_decimal_u8(value)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write_decimal_u8
                uart_write_decimal_u8(value)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write_decimal_u8
                        uart_write_decimal_u8(value)
        self.write(10)  # '\n'

    @inline
    def print_uint16(self, value: uint16):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write_decimal_u16
                uart_write_decimal_u16(value)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write_decimal_u16
                uart_write_decimal_u16(value)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write_decimal_u16
                        uart_write_decimal_u16(value)
        self.write(10)  # '\n'

    @inline
    def print_int16(self, value: int16):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write_decimal_i16
                uart_write_decimal_i16(value)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write_decimal_i16
                uart_write_decimal_i16(value)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write_decimal_i16
                        uart_write_decimal_i16(value)
        self.write(10)  # '\n'

    @inline
    def print_uint32(self, value: uint32):
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_write_decimal_u32
                uart_write_decimal_u32(value)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_write_decimal_u32
                uart_write_decimal_u32(value)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_write_decimal_u32
                        uart_write_decimal_u32(value)
        self.write(10)  # '\n'

    @inline
    def read_blocking(self) -> uint8:
        """Receive one byte, blocking until data is available.

        Identical to read() but named explicitly to distinguish it from the
        non-blocking read_nb().
        """
        return self.read()

    @inline
    def read_line(self, buf: bytearray, max_len: uint8) -> uint8:
        """Read bytes into buf until newline or max_len-1 bytes received.

        CR (carriage-return, 13) is silently discarded so that CRLF line
        endings from terminal emulators are handled transparently.
        A null byte is appended at buf[count] when count < max_len.
        Returns the number of bytes stored (not counting the newline or null).
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_read_line
                return uart_read_line(buf, max_len)
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_read_line
                return uart_read_line(buf, max_len)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_read_line
                        return uart_read_line(buf, max_len)
        return 0

    @inline
    def available(self) -> uint8:
        """Return 1 if a byte is waiting in the receive buffer, 0 otherwise."""
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_available
                return uart_available()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_available
                return uart_available()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_available
                        return uart_available()
        return 0

    @inline
    def read_nb(self) -> uint8:
        """Non-blocking receive: return the next byte if available, else 0.

        Does not block. Check available() before calling to avoid reading
        stale or zero data.
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_read_nb
                return uart_read_nb()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_read_nb
                return uart_read_nb()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_read_nb
                        return uart_read_nb()
        return 0

    @inline
    def read_byte_isr(self) -> uint8:
        """Read one byte from the UART data register directly.

        ISR-safe variant. Must only be called from inside the USART
        receive interrupt handler.
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_read_byte_isr
                return uart_read_byte_isr()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_read_byte_isr
                return uart_read_byte_isr()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_read_byte_isr
                        return uart_read_byte_isr()
        return 0

    @inline
    def irq(self, handler: Callable):
        """Register an interrupt handler at the USART_RX vector.

        handler: top-level function to call on each received byte.
                 Typically calls uart.rx_isr() to fill the ring buffer,
                 but can contain any custom receive logic.

        Enables RXCIE0 and global interrupts (SEI) automatically.
        No @interrupt decorator or asm("SEI") needed.

        Example (ring-buffer mode)::

            def on_rx():
                uart.rx_isr()

            uart.irq(on_rx)
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.chips.attiny2313 import UCSRB, SREG
                UCSRB[7] = 1           # RXCIE
                SREG[7] = 1            # SEI
                compile_isr(handler, 0x0016)
            case "atmega32u4":
                from pymcu.chips.atmega32u4 import UCSR1B, SREG
                UCSR1B[7] = 1          # RXCIE1
                SREG[7] = 1            # SEI
                compile_isr(handler, 0x002C)
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.chips.atmega328p import UCSR0B, SREG
                        UCSR0B[7] = 1          # RXCIE0
                        SREG[7] = 1            # SEI
                        compile_isr(handler, 0x0024)

    @inline
    def enable_rx_interrupt(self):
        """Enable the USART receive interrupt so the RX ISR fires on each received byte.

        After calling this, define an @interrupt handler at the USART_RX
        vector that calls rx_isr().
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_enable_rx_interrupt
                uart_enable_rx_interrupt()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_enable_rx_interrupt
                        uart_enable_rx_interrupt()

    @inline
    def rx_isr(self):
        """Buffer handler for the USART_RX interrupt.

        Call from inside the USART_RX @interrupt handler. Reads the
        incoming byte into the 16-byte ring buffer and advances the head
        index. Bytes are dropped silently if the buffer is full.
        """
        uart_rx_isr()

    @inline
    def rx_available(self) -> uint8:
        """Return 1 if at least one byte is in the ring buffer, 0 otherwise.

        Use after enable_rx_interrupt() to check before calling rx_read().
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_rx_available
                return uart_rx_available()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_rx_available
                return uart_rx_available()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_rx_available
                        return uart_rx_available()
        return 0

    @inline
    def rx_read(self) -> uint8:
        """Read one byte from the ring buffer and advance the tail pointer.

        Returns 0 if the buffer is empty. Check rx_available() before calling.
        """
        match __CHIP__.name:
            case "attiny2313" | "attiny4313":
                from pymcu.hal._uart.attiny2313 import uart_rx_read
                return uart_rx_read()
            case "atmega32u4":
                from pymcu.hal._uart.atmega32u4 import uart_rx_read
                return uart_rx_read()
            case _:
                match __CHIP__.arch:
                    case "avr":
                        from pymcu.hal._uart.avr import uart_rx_read
                        return uart_rx_read()
        return 0


# ---------------------------------------------------------------------------
# Module-level ring-buffer ISR helper
# ---------------------------------------------------------------------------
# Use this function as the handler for uart.irq() when you want the built-in
# 16-byte ring buffer. Define it as a top-level function in your module:
#
#   from pymcu.hal.uart import UART, uart_rx_isr
#
#   def on_rx():
#       uart_rx_isr()
#
#   def main():
#       uart = UART(9600)
#       uart.irq(on_rx)

@inline
def uart_rx_isr():
    """Ring-buffer filler. Call from within a uart.irq() handler."""
    match __CHIP__.name:
        case "attiny2313" | "attiny4313":
            from pymcu.hal._uart.attiny2313 import uart_rx_isr as _impl
            _impl()
        case "atmega32u4":
            from pymcu.hal._uart.atmega32u4 import uart_rx_isr as _impl
            _impl()
        case _:
            match __CHIP__.arch:
                case "avr":
                    from pymcu.hal._uart.avr import uart_rx_isr as _impl
                    _impl()
