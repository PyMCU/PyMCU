# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR UART HAL -- ATmega328P hardware USART0
#
# ATmega328P UART pins (Arduino Uno mapping):
#   TX = PD1  (Arduino pin 1) -- set as output
#   RX = PD0  (Arduino pin 0) -- set as input
#
# Register map (all > 0x5F -> LDS/STS):
#   UBRR0H = 0xC5  -- Baud Rate Register (high byte)
#   UBRR0L = 0xC4  -- Baud Rate Register (low byte)
#   UCSR0A = 0xC0  -- Control/Status A: RXC0(7), UDRE0(5)
#   UCSR0B = 0xC1  -- Control/Status B: RXEN0(4), TXEN0(3)
#   UCSR0C = 0xC2  -- Control/Status C: UCSZ01(2), UCSZ00(1) -> 8-bit frame
#   UDR0   = 0xC6  -- UART Data Register (send/receive byte)
#
# Pre-computed UBRR values for F_CPU = 16 MHz (U2X=0, 16x oversampling):
#   UBRR = round(F_CPU / (16 * baud)) - 1
#   9600   -> 103   (0.16% error)
#   19200  -> 51    (0.16% error)
#   38400  -> 25    (0.16% error)
#   57600  -> 16    (2.08% error)
#   115200 -> 8     (3.54% error)
# -----------------------------------------------------------------------------

from pymcu.chips.atmega328p import UBRR0H, UBRR0L, UCSR0A, UCSR0B, UCSR0C, UDR0, DDRD, SREG
from pymcu.types import uint8, uint16, int16, uint32, int32, inline, const, compile_isr, Callable

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

    # Pre-computed UBRR for 16 MHz -- avoids runtime division
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


def uart_write_decimal_u16(value: uint16):
    # Print uint16 value as decimal digits (0-65535).
    # Standard digit-extraction: peel least-significant digits into a buffer,
    # then emit them in reverse. One division per digit (vs the unrolled
    # divide-by-powers-of-ten form, which costs several divisions per call).
    if value == 0:
        uart_write(48)  # '0'
        return
    buf: uint8[5] = [0, 0, 0, 0, 0]
    n: uint8 = 0
    while value > 0:
        buf[n] = uint8(value % 10) + 48
        value = value // 10
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


def uart_write_decimal_i16(value: int16):
    # Print int16 value as decimal digits with optional minus sign (-32768 to 32767).
    if value < 0:
        uart_write(45)  # '-'
        abs_val: uint16 = uint16(0 - value)
        uart_write_decimal_u16(abs_val)
    else:
        uart_write_decimal_u16(uint16(value))


def uart_write_decimal_u32(value: uint32):
    # Print uint32 value as decimal digits (0-4294967295). Peel least-significant digits into a
    # buffer, then emit them in reverse (same shape as uart_write_decimal_u16). The accumulator
    # stays uint32 throughout: a uint16 intermediate would truncate any value whose low group is
    # 65536..99999 (e.g. 83647 -> 18111), corrupting large values.
    if value == 0:
        uart_write(48)  # '0'
        return
    buf: uint8[10] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    n: uint8 = 0
    while value > 0:
        buf[n] = uint8(value % 10) + 48
        value = value // 10
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


def uart_write_decimal_i32(value: int32):
    # Print int32 value as decimal digits with optional minus sign (-2147483648 to 2147483647).
    # value == INT32_MIN: 0 - value wraps to 0x80000000, whose uint32 reading (2147483648) is the
    # correct magnitude, so "-2147483648" still prints correctly.
    if value < 0:
        uart_write(45)  # '-'
        abs_val: uint32 = uint32(0 - value)
        uart_write_decimal_u32(abs_val)
    else:
        uart_write_decimal_u32(uint32(value))


def uart_write_float(value: float):
    # Print a float with one decimal place (e.g. 23.5, -5.0).
    # Precision: one decimal digit, sufficient for DHT22 sensor values.
    # Uses __fp_* soft-float routines and __div16/__mod16 from the AVR math runtime.
    # Decimal conversion is inlined to avoid pulling in uart_write_decimal_u8 when
    # a program only prints floats (not integers).
    if value < 0.0:
        uart_write(45)
        value = 0.0 - value
    tenths: uint16 = uint16(value * 10.0)
    int_part: uint8 = uint8(tenths // 10)
    frac: uint8 = uint8(tenths % 10)
    if int_part >= 100:
        hundreds: uint8 = int_part // 100
        uart_write(hundreds + 48)
        tens: uint8 = (int_part // 10) % 10
        uart_write(tens + 48)
        units: uint8 = int_part % 10
        uart_write(units + 48)
    elif int_part >= 10:
        tens: uint8 = int_part // 10
        uart_write(tens + 48)
        units: uint8 = int_part % 10
        uart_write(units + 48)
    else:
        uart_write(int_part + 48)
    uart_write(46)
    uart_write(frac + 48)


def uart_write_str(s: const[str]):
    # Non-@inline on purpose: as a real subroutine, the compiler passes the string
    # by reference (its flash address) and this single loop is emitted once and
    # shared by every print() call site, instead of the whole loop being inlined
    # per call. s[i] reads from flash at (s + i) via LPM (FlashLoadPtr).
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i = i + 1
        b = s[i]


def uart_write_fmt(value: int32, base: uint8, width: uint8, flags: uint8):
    # Generic integer formatter backing f-string format specs ({x:02x}, {x:b}, {x:04d}, {x:5d}...).
    # Emits `value` in `base` (2/8/10/16) right-justified to at least `width` chars. `flags` packs
    # the options into one byte (fewer call args = robust AVR argument passing):
    #   bit 0 = upper-case hex digits, bit 1 = signed (emit '-' for negatives), bit 2 = zero-pad.
    # Minimal digits when width <= 1. Only linked when a format spec is actually used. A non-signed
    # call reinterprets the bits as unsigned, so hex/bin print the raw bit pattern.
    zero_pad: uint8 = flags & 0x04
    pad: uint8 = 32          # ' '
    if zero_pad != 0:
        pad = 48             # '0'
    neg: uint8 = 0
    mag: uint32 = 0
    if (flags & 0x02) and value < 0:
        neg = 1
        mag = uint32(0 - value)
    else:
        mag = uint32(value)
    # Zero-padded negatives put the sign first, then zeros fill the field ('-' + '008').
    if neg != 0 and zero_pad != 0:
        uart_write(45)       # '-'
        if width > 0:
            width = width - 1
    buf: uint8[32] = [0] * 32
    n: uint8 = 0
    if mag == 0:
        buf[0] = 48  # '0'
        n = 1
    else:
        while mag > 0:
            d: uint8 = uint8(mag % base)
            if d < 10:
                buf[n] = d + 48        # '0'..'9'
            elif flags & 0x01:
                buf[n] = d - 10 + 65   # 'A'..'F'
            else:
                buf[n] = d - 10 + 97   # 'a'..'f'
            mag = mag // base
            n = n + 1
    # Space-padded negatives keep the sign next to the digits, inside the padded field ('   -8').
    if neg != 0 and zero_pad == 0:
        buf[n] = 45          # '-'
        n = n + 1
    while n < width and n < 32:
        buf[n] = pad
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


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
    if _rx_head != _rx_tail:
        return 1
    return 0


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
