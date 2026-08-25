# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny2313/4313 UART HAL -- hardware USART
#
# TX = PD1, RX = PD0 (I/O port D, same physical pins as ATmega328P)
#
# Register map (I/O addresses -- all < 0x60, so IN/OUT or SBI/CBI):
#   UBRRH  = I/O 0x22 (data 0x42) -- Baud Rate Register high byte
#   UCSRC  = I/O 0x23 (data 0x43) -- Control/Status C (frame format)
#   UBRRL  = I/O 0x09 (data 0x29) -- Baud Rate Register low byte
#   UCSRB  = I/O 0x0A (data 0x2A) -- Control/Status B: RXCIE(7) TXEN(3) RXEN(4)
#   UCSRA  = I/O 0x0B (data 0x2B) -- Control/Status A: RXC(7) UDRE(5)
#   UDR    = I/O 0x0C (data 0x2C) -- UART Data Register
#
# Pre-computed UBRR values for F_CPU = 8 MHz (U2X=0, 16x oversampling):
#   UBRR = F_CPU / (16 * baud) - 1
#   9600   ->  51  (0.16% error)
#   19200  ->  25  (0.16% error)
#   38400  ->  12  (0.16% error)
#   57600  ->   8  (3.5% error)
#   115200 ->   3  (8.5% error)
#
# USART_RX interrupt vector: byte 0x0016 (word 0x000B)
# -----------------------------------------------------------------------------

from pymcu.chips import __FREQ__
from pymcu.chips.attiny2313 import UCSRA, UCSRB, UCSRC, UBRRL, UBRRH, UDR, DDRD, SREG
from pymcu.types import uint8, uint16, int16, uint32, int32, inline, const, compile_isr, Callable

# Ring buffer for interrupt-driven UART receive (16 bytes, power-of-two)
_rx_buf:  uint8[16] = bytearray(16)
_rx_head: uint8 = 0
_rx_tail: uint8 = 0


@inline
def uart_init(baud: const[uint16]):
    # Set PD1 as output (TX), PD0 as input (RX)
    DDRD[1] = 1
    DDRD[0] = 0

    # UBRR is COMPUTED from the configured clock and the requested rate, not looked up in a
    # table. Both are compile-time constants, so the whole expression folds and what is emitted
    # is still two register writes, with no runtime division.
    #
    # This used to be an if/elif over five literal rates with NO else, so any other rate left
    # UBRR at its previous value (zero out of reset, which is the fastest the part can go), and
    # the table ignored `frequency` in pyproject.toml entirely. Both were silent: the emulator
    # does not model a baud mismatch, so only real hardware shows it.
    #
    #   UBRR = round(F_CPU / (16 * baud)) - 1        normal speed
    #   UBRR = round(F_CPU / (8  * baud)) - 1        U2X, double speed
    #
    # The half-divisor added before the division is the integer way to round to nearest.
    # Deliberately no annotated intermediates: an annotated local is materialised, and a
    # materialised uint32 inside an @inline body makes the expansion something the outliner
    # cannot share. Written as one expression per register write, the whole thing folds.
    if __FREQ__ * 2 // (16 * baud) - __FREQ__ // (16 * baud) * 2 != 0:
        # The normal-speed divisor loses more than half a step here, so double speed lands
        # closer. That is what saves 115200 at 16 MHz: normal speed is 3.5% off, which transmit
        # survives (the receiver resynchronizes on every start bit) and RECEIVE does not,
        # because the error accumulates across the frame and bytes drop on real silicon.
        UCSRA.value = 0x02
        UBRRH.value = uint8(((__FREQ__ + 4 * baud) // (8 * baud) - 1) >> 8)
        UBRRL.value = uint8((__FREQ__ + 4 * baud) // (8 * baud) - 1)
    else:
        UBRRH.value = uint8(((__FREQ__ + 8 * baud) // (16 * baud) - 1) >> 8)
        UBRRL.value = uint8((__FREQ__ + 8 * baud) // (16 * baud) - 1)

    # 8N1 frame format (UCSZ1=bit2, UCSZ0=bit1, async, no parity, 1 stop)
    UCSRC.value = 0x06
    # Enable transmitter (TXEN=bit3) and receiver (RXEN=bit4)
    UCSRB.value = 0x18


@inline
def uart_write(data: uint8):
    # Wait until transmit buffer is empty (UDRE, bit 5 of UCSRA)
    while UCSRA[5] == 0:
        pass
    UDR.value = data


@inline
def uart_write_byte(data: uint8):
    UDR.value = data


@inline
def uart_read() -> uint8:
    # Wait until a byte is received (RXC, bit 7 of UCSRA)
    while UCSRA[7] == 0:
        pass
    result: uint8 = UDR.value
    return result


@inline
def uart_read_line(buf: bytearray, max_len: uint8) -> uint8:
    # Read bytes into buf until '\n' (10) or max_len-1 bytes received.
    # CR ('\r' = 13) is silently discarded.
    # A null byte is appended at buf[count] if count < max_len.
    # Returns the number of bytes stored (not counting the newline or null).
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


def uart_write_fmt(value: int32, base: uint8, width: uint8, flags: uint8):
    # Generic integer formatter for f-string format specs. See avr.py for the full contract:
    # flags bit0=upper, bit1=signed, bit2=zero-pad; base 2/8/10/16; width-padded; minimal at width<=1.
    zero_pad: uint8 = flags & 0x04
    pad: uint8 = 32
    if zero_pad != 0:
        pad = 48
    neg: uint8 = 0
    mag: uint32 = 0
    if (flags & 0x02) and value < 0:
        neg = 1
        mag = uint32(0 - value)
    else:
        mag = uint32(value)
    if neg != 0 and zero_pad != 0:
        uart_write(45)
        if width > 0:
            width = width - 1
    buf: uint8[32] = [0] * 32
    n: uint8 = 0
    if mag == 0:
        buf[0] = 48
        n = 1
    else:
        while mag > 0:
            d: uint8 = uint8(mag % base)
            if d < 10:
                buf[n] = d + 48
            elif flags & 0x01:
                buf[n] = d - 10 + 65
            else:
                buf[n] = d - 10 + 97
            mag = mag // base
            n = n + 1
    if neg != 0 and zero_pad == 0:
        buf[n] = 45
        n = n + 1
    while n < width and n < 32:
        buf[n] = pad
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


@inline
def uart_available() -> uint8:
    # Returns 1 if a byte is waiting (RXC, bit 7 of UCSRA)
    if UCSRA[7]:
        return 1
    return 0


@inline
def uart_read_nb() -> uint8:
    # Non-blocking read: return the byte if available, else 0
    if UCSRA[7]:
        result: uint8 = UDR.value
        return result
    return 0


@inline
def uart_read_byte_isr() -> uint8:
    # ISR-safe read: reads directly from UDR without polling UCSRA
    result: uint8 = UDR.value
    return result


@inline
def uart_enable_rx_interrupt():
    # Enable RXCIE (bit 7 of UCSRB) to fire USART_RX ISR on each received byte
    UCSRB[7] = 1


@inline
def uart_rx_isr():
    # Called from the USART_RX ISR (vector byte 0x0016).
    # Reads UDR and stores in ring buffer at _rx_head; advances head with wrap.
    global _rx_head, _rx_tail, _rx_buf
    next_head: uint8 = (_rx_head + 1) & 0x0F
    if next_head != _rx_tail:
        _rx_buf[_rx_head] = UDR.value
        _rx_head = next_head


@inline
def uart_rx_available() -> uint8:
    # Returns 1 if at least one byte is waiting in the ring buffer
    global _rx_head, _rx_tail
    if _rx_head != _rx_tail:
        return 1
    return 0


@inline
def uart_rx_read() -> uint8:
    # Non-blocking ring-buffer read. Returns 0 if empty.
    global _rx_head, _rx_tail, _rx_buf
    if _rx_head == _rx_tail:
        return 0
    data: uint8 = _rx_buf[_rx_tail]
    _rx_tail = (_rx_tail + 1) & 0x0F
    return data


@inline
def uart_rx_irq_setup():
    # Enable RXCIE (UCSRB bit 7) + SEI and register uart_rx_isr at the
    # USART_RX vector (byte 0x0016, word 0x000B).
    UCSRB[7] = 1        # RXCIE: enable USART RX complete interrupt
    SREG[7] = 1         # SEI: enable global interrupts
    compile_isr(uart_rx_isr, 0x0016)


