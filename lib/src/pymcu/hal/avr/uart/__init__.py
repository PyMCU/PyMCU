# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR UART facade -- pymcu.hal.avr.uart
#
# Module-level conditional imports select the correct chip implementation at
# compile time. The ConditionalImportExtractor resolves these if/elif chains
# before the dependency graph is built, so only the winning chip module loads.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.exceptions import CompileError

# The ATtiny 2313 family shares one USART at UCSRA 0x0B / UDR 0x0C in I/O space. The 4313 is
# the same part with twice the memory and was missing from this list, so it fell through to
# the else and compiled the ATmega328P USART: writes to UCSR0A 0xC0 and UDR0 0xC6, addresses
# that do not exist on a part with 256 bytes of SRAM. Clean build, and the UART simply never
# spoke.
if __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny4313":
    from pymcu.hal.avr.uart.attiny2313 import (
        uart_init, uart_write, uart_read,
        uart_available, uart_read_nb, uart_read_byte_isr,
        uart_enable_rx_interrupt, uart_rx_isr as _uart_rx_isr_impl,
        uart_rx_available, uart_rx_read,
        uart_read_line,
        uart_write_fmt,
        uart_rx_count, uart_rx_buffer_size, uart_rx_irq_setup,
        uart_rx_read_timeout, uart_read_timeout,
        uart_tx_empty, uart_deinit,
    )
elif __CHIP__.name == "atmega32u4":
    from pymcu.hal.avr.uart.atmega32u4 import (
        uart_init, uart_write, uart_read,
        uart_available, uart_read_nb, uart_read_byte_isr,
        uart_enable_rx_interrupt, uart_rx_isr as _uart_rx_isr_impl,
        uart_rx_available, uart_rx_read,
        uart_read_line,
        uart_write_fmt,
        uart_rx_count, uart_rx_buffer_size, uart_rx_irq_setup,
        uart_rx_read_timeout, uart_read_timeout,
        uart_tx_empty, uart_deinit,
    )
elif (__CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a"
      or __CHIP__.name == "attiny25" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny85"
      or __CHIP__.name == "attiny24" or __CHIP__.name == "attiny44" or __CHIP__.name == "attiny84"):
    # These parts have NO hardware USART at all. They used to fall through to the else and
    # compile the ATmega328P one, so a program built clean, ran, and wrote every byte into
    # address space the chip does not have. Refusing by name is the honest answer; PyMCU has
    # no software serial to offer instead.
    raise CompileError(
        "this chip has no hardware UART. The ATtiny 13/25/45/85 and 24/44/84 families have "
        "no USART peripheral, so pymcu.hal.uart cannot drive one. Use a part that has one "
        "(the ATtiny 2313/4313 do), or carry the data over another peripheral this chip has.")
else:
    from pymcu.hal.avr.uart.avr import (
        uart_init, uart_write, uart_read,
        uart_available, uart_read_nb, uart_read_byte_isr,
        uart_enable_rx_interrupt, uart_rx_isr as _uart_rx_isr_impl,
        uart_rx_available, uart_rx_read,
        uart_read_line,
        uart_write_fmt,
        uart_rx_count, uart_rx_buffer_size, uart_rx_irq_setup,
        uart_rx_read_timeout, uart_read_timeout,
        uart_tx_empty, uart_deinit,
    )


from pymcu.hal.uart_text import (
    uart_write_str, uart_write_decimal_u8, uart_write_decimal_u16,
    uart_write_decimal_i16, uart_write_decimal_u32, uart_write_decimal_i32,
)

# uart_write_float (the correct formatter: MicroPython's 7-significant-digit
# float32 policy) pulls in _f32_repr + _f32_scale, ~7.3 KB together -- more
# than the entire flash of a 4 KB part. attiny2313 (2 KB) was already routed
# to the compact one-decimal writer for the same reason before this pair
# existed; attiny4313 and atmega48/48p are 4 KB parts that fit the OLD,
# wrong, fixed-two-decimals formatter but cannot fit the correct one either.
# The threshold is flash size, not a chip list: any AVR part with 4 KB or
# less of flash cannot hold the correct writer, whichever chip it is.
if __CHIP__.flash_size <= 4096:
    from pymcu.hal.uart_text import uart_write_float_compact as uart_write_float
else:
    from pymcu.hal.uart_text import uart_write_float


@inline
def uart_rx_isr():
    _uart_rx_isr_impl()


class UART:
    """Hardware UART, zero-cost abstraction (all methods @inline)."""

    # bits, parity and stop describe the frame. They used to be nowhere: every layer above
    # accepted them from the caller and threw them away, so a program that asked for 7E1 ran
    # 8N1 and said nothing. The three are compile-time constants, so an 8N1 UART emits the
    # same single register write it always did, and a frame this part cannot send is refused
    # where the UART is constructed. Parity is 0 none, 1 even, 2 odd on every architecture.
    def __init__(self, baud: const[uint16] = 9600, bits: const[uint8] = 8,
                 parity: const[uint8] = 0, stop: const[uint8] = 1):
        uart_init(baud, bits, parity, stop)

    @inline
    def write(self, data: uint8):
        uart_write(data)

    @inline
    def write(self, buf: bytearray):
        # Overload: a bytes literal (uart.write(b"...")) or any other fixed buffer, sent
        # one byte at a time -- the MicroPython compat layer's machine.UART.write(buf)
        # already does this over its own _hw.write(); the native HAL lacked the buffer
        # overload entirely, so a literal bytes argument had no write() it could bind to.
        #
        # Named 'buf', not 'data' like the uint8 overload just above: bytearrayParams is
        # keyed by "<qualified function name>.<param name>" WITHOUT the per-overload
        # mangled suffix (Statements.cs), so two @inline overloads of the same method
        # that happen to share a parameter NAME collide in that one set -- the uint8
        # overload's "data" would read as a bytearray param too, and `uart.write(buf[j])`
        # (a scalar element read) through an intermediate inline expansion silently
        # picked THIS overload instead of the uint8 one, then failed on `len()` of a
        # value that was never an array (found via the MicroPython compat layer's own
        # machine.UART.write(uint8) forwarding to self._hw.write(buf), #p2avr-7).
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
        uart_write_str(s)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        self.write(10)

    @inline
    def print_byte(self, value: uint8):
        uart_write_decimal_u8(value)
        self.write(10)

    @inline
    def print_uint16(self, value: uint16):
        uart_write_decimal_u16(value)
        self.write(10)

    @inline
    def print_int16(self, value: int16):
        uart_write_decimal_i16(value)
        self.write(10)

    @inline
    def print_uint32(self, value: uint32):
        uart_write_decimal_u32(value)
        self.write(10)

    @inline
    def print_float(self, value: float):
        uart_write_float(value)
        self.write(10)

    @inline
    def read_line(self, buf, max_len: uint8) -> uint8:
        return uart_read_line(buf, max_len)

    @inline
    def available(self) -> uint8:
        return uart_available()

    # MicroPython's name for the same question. The capability was here under a name the
    # user's previous platform does not use, and the error only said the method did not exist.
    @inline
    def any(self) -> uint8:
        return uart_available()

    @inline
    def read_nb(self) -> uint8:
        return uart_read_nb()

    @inline
    def read_byte_isr(self) -> uint8:
        return uart_read_byte_isr()

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
        uart_enable_rx_interrupt()

    @inline
    def rx_isr(self):
        uart_rx_isr()

    @inline
    def rx_available(self) -> uint8:
        return uart_rx_available()

    @inline
    def rx_read(self) -> uint8:
        return uart_rx_read()

    # How many bytes are waiting in the ring. rx_available() answers "any at all"; a caller
    # sizing a read, or a layer reporting in_waiting, needs the count.
    @inline
    def rx_count(self) -> uint8:
        return uart_rx_count()

    # The ring's capacity, as a compile-time constant: what a layer taking a buffer size from
    # its caller can actually promise.
    @inline
    def rx_buffer_size(self) -> uint8:
        return uart_rx_buffer_size()

    # Turn on the interrupt-driven receive path: enable RXCIE and global interrupts, and
    # register the ISR that fills the ring. Until this is called the UART is polled and only
    # the hardware's own one-byte register holds anything.
    @inline
    def start_buffered_rx(self, size: const[uint16] = 0):
        uart_rx_irq_setup(size)

    # Read one byte, giving up after `ms` milliseconds; -1 means nothing arrived. Two forms
    # because there are two receive paths: the ring, and the hardware register when the
    # interrupt is off. A blocking read that never returns is not a timeout.
    @inline
    def rx_read_timeout(self, ms: uint16) -> int16:
        return uart_rx_read_timeout(ms)

    @inline
    def read_timeout(self, ms: uint16) -> int16:
        return uart_read_timeout(ms)

    # Reprogram the rate and frame of a running USART: the same register writes the
    # constructor makes.
    @inline
    def reinit(self, baud: const[uint16] = 9600, bits: const[uint8] = 8,
               parity: const[uint8] = 0, stop: const[uint8] = 1):
        uart_init(baud, bits, parity, stop)

    # Switch the transmitter and receiver off.
    @inline
    def deinit(self):
        uart_deinit()

    # 1 once the data register is empty: the last byte written is in the shift register
    # or gone. See uart_tx_empty.
    @inline
    def tx_empty(self) -> uint8:
        return uart_tx_empty()
