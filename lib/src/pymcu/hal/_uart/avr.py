# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
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

from pymcu.chips.atmega328p import UBRR0H, UBRR0L, UCSR0A, UCSR0B, UCSR0C, UDR0, DDRD
from pymcu.types import uint8, uint16, inline, const


@inline
def uart_init(baud: const[uint16]):
    # Set PD1 as output (TX), PD0 as input (RX)
    DDRD[1] = 1
    DDRD[0] = 0

    # Pre-computed UBRR for 16 MHz — avoids runtime division
    if baud == 9600:
        UBRR0L.value = 103
        UBRR0H.value = 0
    elif baud == 19200:
        UBRR0L.value = 51
        UBRR0H.value = 0
    elif baud == 38400:
        UBRR0L.value = 25
        UBRR0H.value = 0
    elif baud == 57600:
        UBRR0L.value = 16
        UBRR0H.value = 0
    elif baud == 115200:
        UBRR0L.value = 8
        UBRR0H.value = 0

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
    # Emit a UARTSendString IR instruction — AVR backend stores the string in
    # flash and sends it via a shared LPM+Z loop (much smaller than inline unrolling)
    uart_send_string(s)


@inline
def uart_available() -> uint8:
    # Returns 1 if a byte is waiting in the UART receive buffer (RXC0, bit 7 of UCSR0A)
    if UCSR0A[7]:
        return 1
    return 0


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
