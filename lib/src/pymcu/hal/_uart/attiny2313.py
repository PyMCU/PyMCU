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

from pymcu.chips.attiny2313 import UCSRA, UCSRB, UCSRC, UBRRL, UBRRH, UDR, DDRD, SREG
from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable

# Ring buffer for interrupt-driven UART receive (16 bytes, power-of-two)
_rx_buf:  uint8[16] = bytearray(16)
_rx_head: uint8 = 0
_rx_tail: uint8 = 0


@inline
def uart_init(baud: const[uint16]):
    # Set PD1 as output (TX), PD0 as input (RX)
    DDRD[1] = 1
    DDRD[0] = 0

    # Pre-computed UBRR for 8 MHz -- avoids runtime division
    if baud == 9600:
        UBRRL.value = 51
        UBRRH.value = 0
    elif baud == 19200:
        UBRRL.value = 25
        UBRRH.value = 0
    elif baud == 38400:
        UBRRL.value = 12
        UBRRH.value = 0
    elif baud == 57600:
        UBRRL.value = 8
        UBRRH.value = 0
    elif baud == 115200:
        UBRRL.value = 3
        UBRRH.value = 0

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


def uart_write_decimal_u8(value: uint8):
    # Print uint8 value as decimal digits (0-255).
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


@inline
def uart_write_str(s: const[str]):
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i = i + 1
        b = s[i]


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
