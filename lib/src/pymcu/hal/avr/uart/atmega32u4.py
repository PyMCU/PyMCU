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
from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable

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


def uart_write_decimal_u8(value: uint8):
    if value >= 100:
        hundreds: uint8 = value // 100
        uart_write(hundreds + 48)
        tens: uint8 = (value // 10) % 10
        uart_write(tens + 48)
        units: uint8 = value % 10
        uart_write(units + 48)
    elif value >= 10:
        tens: uint8 = value // 10
        uart_write(tens + 48)
        units: uint8 = value % 10
        uart_write(units + 48)
    else:
        uart_write(value + 48)


def uart_write_decimal_u16(value: uint16):
    # Print uint16 value as decimal digits (0-65535).
    if value >= 10000:
        ten_k: uint8 = uint8(value // 10000)
        uart_write(ten_k + 48)
        thousands: uint8 = uint8((value // 1000) % 10)
        uart_write(thousands + 48)
        hundreds: uint8 = uint8((value // 100) % 10)
        uart_write(hundreds + 48)
        tens: uint8 = uint8((value // 10) % 10)
        uart_write(tens + 48)
        units: uint8 = uint8(value % 10)
        uart_write(units + 48)
    elif value >= 1000:
        thousands: uint8 = uint8(value // 1000)
        uart_write(thousands + 48)
        hundreds: uint8 = uint8((value // 100) % 10)
        uart_write(hundreds + 48)
        tens: uint8 = uint8((value // 10) % 10)
        uart_write(tens + 48)
        units: uint8 = uint8(value % 10)
        uart_write(units + 48)
    elif value >= 100:
        hundreds: uint8 = uint8(value // 100)
        uart_write(hundreds + 48)
        tens: uint8 = uint8((value // 10) % 10)
        uart_write(tens + 48)
        units: uint8 = uint8(value % 10)
        uart_write(units + 48)
    elif value >= 10:
        tens: uint8 = uint8(value // 10)
        uart_write(tens + 48)
        units: uint8 = uint8(value % 10)
        uart_write(units + 48)
    else:
        uart_write(uint8(value) + 48)


def uart_write_decimal_i16(value: int16):
    # Print int16 value as decimal digits with optional minus sign (-32768 to 32767).
    if value < 0:
        uart_write(45)  # '-'
        abs_val: uint16 = uint16(0 - value)
        uart_write_decimal_u16(abs_val)
    else:
        uart_write_decimal_u16(uint16(value))


def uart_write_decimal_u32(value: uint32):
    # Print uint32 value as decimal digits (0-4294967295).
    # Split into high group (value // 100000, printed without leading zeros)
    # and zero-padded low group (value % 100000, always 5 digits).
    if value < 100000:
        uart_write_decimal_u16(uint16(value))
    else:
        high: uint16 = uint16(value // 100000)
        low5: uint16 = uint16(value % 100000)
        uart_write_decimal_u16(high)
        d: uint8 = uint8(low5 // 10000)
        uart_write(d + 48)
        d = uint8((low5 // 1000) % 10)
        uart_write(d + 48)
        d = uint8((low5 // 100) % 10)
        uart_write(d + 48)
        d = uint8((low5 // 10) % 10)
        uart_write(d + 48)
        d = uint8(low5 % 10)
        uart_write(d + 48)


def uart_write_str(s: const[str]):
    # Non-@inline: shared subroutine, the string is passed by reference (its flash
    # address) so the byte-loop is emitted once instead of inlined per print() call.
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i = i + 1
        b = s[i]


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


def uart_write_float(value: float):
    if value < 0.0:
        uart_write(45)
        value = 0.0 - value
    tenths: uint16 = uint16(value * 10.0)
    int_part: uint8 = uint8(tenths // 10)
    frac: uint8 = uint8(tenths % 10)
    uart_write_decimal_u8(int_part)
    uart_write(46)
    uart_write(frac + 48)
