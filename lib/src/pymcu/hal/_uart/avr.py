# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR UART HAL — ATmega328P hardware USART0
#
# ATmega328P UART pins (Arduino Uno mapping):
#   TX = PD1  (Arduino pin 1) — set as output
#   RX = PD0  (Arduino pin 0) — set as input
#
# Register map (all > 0x5F → LDS/STS):
#   UBRR0H = 0xC5  — Baud Rate Register (high byte)
#   UBRR0L = 0xC4  — Baud Rate Register (low byte)
#   UCSR0A = 0xC0  — Control/Status A: RXC0(7), UDRE0(5)
#   UCSR0B = 0xC1  — Control/Status B: RXEN0(4), TXEN0(3)
#   UCSR0C = 0xC2  — Control/Status C: UCSZ01(2), UCSZ00(1) → 8-bit frame
#   UDR0   = 0xC6  — UART Data Register (send/receive byte)
#
# Pre-computed UBRR values for F_CPU = 16 MHz (U2X=0, 16x oversampling):
#   UBRR = round(F_CPU / (16 * baud)) - 1
#   9600   → 103   (0.16% error)
#   19200  → 51    (0.16% error)
#   38400  → 25    (0.16% error)
#   57600  → 16    (2.08% error)
#   115200 → 8     (3.54% error)
# -----------------------------------------------------------------------------

from pymcu.chips.atmega328p import UBRR0H, UBRR0L, UCSR0A, UCSR0B, UCSR0C, UDR0, DDRD, SREG
from pymcu.types import uint8, uint16, inline, const, compile_isr, Callable

# Ring buffer for interrupt-driven UART receive (16 bytes, power-of-two)
# _rx_buf: circular storage; _rx_head: write index (ISR advances);
# _rx_tail: read index (main loop advances).
# Full condition: ((head + 1) & 0x0F) == tail (drop on overflow).
_rx_buf:  uint8[16] = bytearray(16)
_rx_head: uint8 = 0
_rx_tail: uint8 = 0


@inline
def uart_init(baud: const[uint16]):
    # Set PD1 as output (TX), PD0 as input (RX)
    DDRD[1] = 1
    DDRD[0] = 0

    # Pre-computed UBRR for 16 MHz — avoids runtime division.
    # High byte is always 0 for all supported baud rates.
    UBRR0H.value = 0
    if baud == 9600:
        UBRR0L.value = 103
    elif baud == 19200:
        UBRR0L.value = 51
    elif baud == 38400:
        UBRR0L.value = 25
    elif baud == 57600:
        UBRR0L.value = 16
    elif baud == 115200:
        UBRR0L.value = 8

    # 8N1 frame format (UCSZ01=1, UCSZ00=1, async, no parity, 1 stop)
    UCSR0C.value = 0x06
    # Enable transmitter (TXEN0=1) and receiver (RXEN0=1)
    UCSR0B.value = 0x18


@inline
def uart_write(data: uint8):
    # Wait until transmit buffer is empty (UDRE0, bit 5 of UCSR0A)
    while UCSR0A[5] == 0:
        pass
    # Write full byte to data register
    UDR0.value = data


@inline
def uart_read() -> uint8:
    # Wait until a byte is received (RXC0, bit 7 of UCSR0A)
    while UCSR0A[7] == 0:
        pass
    # Read full byte from data register
    result: uint8 = UDR0.value
    return result


def uart_write_decimal_u8(value: uint8):
    # Print uint8 value as decimal digits (0-255).
    # Uses __div8 / __mod8 from the AVR math runtime.
    # Variable names (hundreds, tens, units) are re-declared in each branch
    # because the compiler requires declarations at the point of first use.
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


@inline
def uart_write_str(s: const[str]):
    # s is a compile-time string constant. The compiler interns it as FlashData
    # and iterates byte-by-byte via ArrayLoadFlash.
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i += 1
        b = s[i]


@inline
def uart_available() -> uint8:
    # Returns 1 if a byte is waiting in the UART receive buffer (RXC0, bit 7 of UCSR0A)
    return 1 if UCSR0A[7] else 0


@inline
def uart_read_nb() -> uint8:
    # Non-blocking read: if a byte is available (RXC0=1) return it, otherwise return 0.
    if UCSR0A[7]:
        result: uint8 = UDR0.value
        return result
    return 0


@inline
def uart_read_byte_isr() -> uint8:
    # ISR-safe read: reads directly from UDR0 without polling UCSR0A.
    # Call this only when invoked from a USART_RX interrupt (RXC0 is guaranteed set).
    result: uint8 = UDR0.value
    return result


@inline
def uart_enable_rx_interrupt():
    # Enable RXCIE0 (bit 7 of UCSR0B) to fire USART_RX ISR on each received byte.
    # UCSR0B already has RXEN0=1, TXEN0=1 (0x18) set by uart_init.
    # Set bit 7 (RXCIE0) without disturbing other bits by OR-ing the full byte.
    UCSR0B[7] = 1


@inline
def uart_rx_isr():
    # Called from the USART_RX ISR (vector 0x0024 / word 0x0012).
    # Reads UDR0 and stores in ring buffer at _rx_head; advances head with wrap.
    # Drops byte silently if buffer is full (head+1 == tail).
    global _rx_head, _rx_tail, _rx_buf
    next_head: uint8 = (_rx_head + 1) & 0x0F
    if next_head != _rx_tail:
        _rx_buf[_rx_head] = UDR0.value
        _rx_head = next_head


@inline
def uart_rx_available() -> uint8:
    # Returns 1 if at least one byte is waiting in the ring buffer.
    global _rx_head, _rx_tail
    return 1 if _rx_head != _rx_tail else 0


@inline
def uart_rx_read() -> uint8:
    # Non-blocking ring-buffer read. Returns the next byte from the ring buffer
    # and advances tail. Returns 0 if the buffer is empty (check available() first).
    global _rx_head, _rx_tail, _rx_buf
    if _rx_head == _rx_tail:
        return 0
    data: uint8 = _rx_buf[_rx_tail]
    _rx_tail = (_rx_tail + 1) & 0x0F
    return data


@inline
def uart_rx_irq_setup():
    # Enable RXCIE0 (UCSR0B bit 7) + SEI and register uart_rx_isr at the
    # USART_RX vector (byte 0x0024, word 0x0012).
    # After this call, received bytes are automatically stored in the ring buffer.
    UCSR0B[7] = 1        # RXCIE0: enable USART RX complete interrupt
    SREG[7] = 1          # SEI: enable global interrupts
    compile_isr(uart_rx_isr, 0x0024)
