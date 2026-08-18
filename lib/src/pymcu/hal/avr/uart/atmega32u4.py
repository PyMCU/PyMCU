# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATmega32U4 UART HAL -- hardware USART1
#
# ATmega32U4 UART pins (Arduino Leonardo mapping):
#   TX = PD3  (Arduino pin 1) -- set as output
#   RX = PD2  (Arduino pin 0) -- set as input
#
# Register map (all > 0x5F -- LDS/STS):
#   UBRR1H = 0xCD  -- Baud Rate Register (high byte)
#   UBRR1L = 0xCC  -- Baud Rate Register (low byte)
#   UCSR1A = 0xC8  -- Control/Status A: RXC1(7), UDRE1(5)
#   UCSR1B = 0xC9  -- Control/Status B: RXEN1(4), TXEN1(3)
#   UCSR1C = 0xCA  -- Control/Status C: UCSZ11(2), UCSZ10(1) -> 8-bit frame
#   UDR1   = 0xCE  -- UART Data Register
#
# Pre-computed UBRR values for F_CPU = 16 MHz (U2X=0, 16x oversampling):
#   9600   -> 103  (0.16% error)
#   19200  -> 51   (0.16% error)
#   38400  -> 25   (0.16% error)
#   57600  -> 16   (2.08% error)
#   115200 -> 8    (3.54% error)
#
# USART1 RX interrupt vector: 0x002C (word 0x0016)
# -----------------------------------------------------------------------------

from pymcu.chips.atmega32u4 import UBRR1H, UBRR1L, UCSR1A, UCSR1B, UCSR1C, UDR1, DDRD, SREG
from pymcu.types import uint8, uint16, int16, uint32, int32, inline, const, compile_isr, Callable

_rx_buf:  uint8[16] = bytearray(16)
_rx_head: uint8 = 0
_rx_tail: uint8 = 0


@inline
def uart_init(baud: const[uint16]):
    # Set PD3 as output (TX), PD2 as input (RX)
    DDRD[3] = 1
    DDRD[2] = 0

    if baud == 9600:
        UBRR1L.value = 103
        UBRR1H.value = 0
    elif baud == 19200:
        UBRR1L.value = 51
        UBRR1H.value = 0
    elif baud == 38400:
        UBRR1L.value = 25
        UBRR1H.value = 0
    elif baud == 57600:
        UBRR1L.value = 16
        UBRR1H.value = 0
    elif baud == 115200:
        UBRR1L.value = 8
        UBRR1H.value = 0

    # 8N1 frame format
    UCSR1C.value = 0x06
    # Enable transmitter (TXEN1=1) and receiver (RXEN1=1)
    UCSR1B.value = 0x18


@inline
def uart_write(data: uint8):
    while UCSR1A[5] == 0:
        pass
    UDR1.value = data


@inline
def uart_read() -> uint8:
    while UCSR1A[7] == 0:
        pass
    result: uint8 = UDR1.value
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
    if UCSR1A[7]:
        return 1
    return 0


@inline
def uart_read_nb() -> uint8:
    if UCSR1A[7]:
        result: uint8 = UDR1.value
        return result
    return 0


@inline
def uart_read_byte_isr() -> uint8:
    result: uint8 = UDR1.value
    return result


@inline
def uart_enable_rx_interrupt():
    UCSR1B[7] = 1


@inline
def uart_rx_isr():
    global _rx_head, _rx_tail, _rx_buf
    next_head: uint8 = (_rx_head + 1) & 0x0F
    if next_head != _rx_tail:
        _rx_buf[_rx_head] = UDR1.value
        _rx_head = next_head


@inline
def uart_rx_available() -> uint8:
    global _rx_head, _rx_tail
    if _rx_head != _rx_tail:
        return 1
    return 0


@inline
def uart_rx_read() -> uint8:
    global _rx_head, _rx_tail, _rx_buf
    if _rx_head == _rx_tail:
        return 0
    data: uint8 = _rx_buf[_rx_tail]
    _rx_tail = (_rx_tail + 1) & 0x0F
    return data


@inline
def uart_rx_irq_setup():
    # USART1 RX complete interrupt vector: byte 0x002C, word 0x0016
    UCSR1B[7] = 1
    SREG[7] = 1
    compile_isr(uart_rx_isr, 0x002C)


