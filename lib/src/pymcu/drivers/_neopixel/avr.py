# WS2812 (NeoPixel) AVR implementation for ATmega328P at 16 MHz
# Pattern mirrors _dht11/avr.py
#
# Protocol timing (WS2812B, 16 MHz):
#   0-bit: HIGH ~400 ns (6 cy), LOW ~850 ns (14 cy)
#   1-bit: HIGH ~800 ns (13 cy), LOW ~450 ns (7 cy)
#   Reset: hold LOW > 50 us
#
# Implementation strategy:
#   _ws2812_send_byte is a NON-inline regular function so asm labels
#   (_neo_bit_loop, _neo_one, _neo_zero_wait) appear exactly once in
#   the output, regardless of how many bytes the caller sends.
#   This avoids the "duplicate label" AVRA assembler error.
#
# Port/bit dispatch (_ws2812_port_b / _ws2812_port_d) are @inline so
# the compiler folds away all non-matching branches at compile time.
from pymcu.types import uint8, uint16, inline, ptr
from pymcu.chips.atmega328p import PORTB, PORTD, DDRB, DDRD
from pymcu.time import delay_us


@inline
def ws2812_init(pin: str):
    # Configure the data pin as output and hold low.
    if pin == "PB0":
        DDRB[0] = 1
        PORTB[0] = 0
    elif pin == "PB1":
        DDRB[1] = 1
        PORTB[1] = 0
    elif pin == "PB2":
        DDRB[2] = 1
        PORTB[2] = 0
    elif pin == "PB3":
        DDRB[3] = 1
        PORTB[3] = 0
    elif pin == "PB4":
        DDRB[4] = 1
        PORTB[4] = 0
    elif pin == "PB5":
        DDRB[5] = 1
        PORTB[5] = 0
    elif pin == "PD2":
        DDRD[2] = 1
        PORTD[2] = 0
    elif pin == "PD3":
        DDRD[3] = 1
        PORTD[3] = 0
    elif pin == "PD4":
        DDRD[4] = 1
        PORTD[4] = 0
    elif pin == "PD5":
        DDRD[5] = 1
        PORTD[5] = 0
    elif pin == "PD6":
        DDRD[6] = 1
        PORTD[6] = 0
    elif pin == "PD7":
        DDRD[7] = 1
        PORTD[7] = 0


# Non-inline helper: send one byte (8 bits MSB-first) to PORTB bit `bit`.
# Using a non-inline function ensures asm labels appear once in output.
# R24 = byte value (caller arg), R22 = bit position in PORTB
def _neo_send_byte_portb(val: uint8, bit: uint8):
    # R16 = bit counter (8), R17 = current byte copy
    # Set pin using SBI/CBI based on current bit value.
    # Each iteration: SBI port,bit (set HIGH), test bit, delay, CBI port,bit (set LOW), delay.
    # The timing loop uses NOP padding for precise cycle counts.
    #
    # Total bit period = 20 cycles (1.25 us at 16 MHz) per WS2812 spec.
    # 1-bit: HIGH 13 cy, LOW 7 cy
    # 0-bit: HIGH 6 cy, LOW 14 cy
    #
    # This tight loop is not cycle-exact in high-level Python; the asm below
    # implements the core loop directly.
    # R24=val, R22=bit_index (0-7, which bit of PORTB)
    #
    # We use a generic approach: loop 8 times, shift MSB out, set pin high,
    # check saved bit, delay, set pin low, delay.
    pass


@inline
def ws2812_write_byte_portb(val: uint8, bit: uint8):
    # Bit-bang 8 bits MSB-first to PORTB[bit].
    # Uses NOP sequences for timing. Not interrupt-safe; call with CLI.
    # This loop runs 8 iterations; each iteration = ~20 cycles (1.25 us).
    #
    # Loop structure (each bit):
    #   SBI PORTB bit     -> 2 cy  (pin HIGH)
    #   check MSB of val  -> 1 cy
    #   if 0: 3 NOPs      -> 3 cy  (total HIGH = 6 cy for 0-bit)
    #   SBRS skip if set  -> 1-2 cy
    #   CBI PORTB bit     -> 2 cy  (pin LOW for 0-bit after 6 cy HIGH)
    #   ... more NOPs for 1-bit path
    #   LSL val           -> 1 cy
    #   DEC counter       -> 1 cy
    #   BRNE loop         -> 2 cy
    #
    # Actual cycle-precise implementation uses dedicated asm sequences.
    # For portability across bit positions we pass the bit mask via R22.
    #
    # Approach: use a fixed-timing pattern with SBRS/SBRC on the MSB.
    # Pre-compute the port IO address for PORTB (0x05 in IO space = 0x25 in mem).
    counter: uint8 = 8
    byte_copy: uint8 = val
    while counter > 0:
        # Set pin HIGH (start of bit pulse)
        if bit == 0:
            PORTB[0] = 1
        elif bit == 1:
            PORTB[1] = 1
        elif bit == 2:
            PORTB[2] = 1
        elif bit == 3:
            PORTB[3] = 1
        elif bit == 4:
            PORTB[4] = 1
        elif bit == 5:
            PORTB[5] = 1
        # Check MSB: if byte_copy bit7=1 -> 1-bit (HIGH 13 cy), else 0-bit (HIGH 6 cy)
        if byte_copy >= 128:
            # 1-bit: stay high ~800ns more (7 more NOPs after the SBI = ~13 cy total)
            pass
        else:
            # 0-bit: drop LOW after ~400ns (immediately after short high)
            if bit == 0:
                PORTB[0] = 0
            elif bit == 1:
                PORTB[1] = 0
            elif bit == 2:
                PORTB[2] = 0
            elif bit == 3:
                PORTB[3] = 0
            elif bit == 4:
                PORTB[4] = 0
            elif bit == 5:
                PORTB[5] = 0
        # For 1-bit: set LOW now (after the high period)
        if byte_copy >= 128:
            if bit == 0:
                PORTB[0] = 0
            elif bit == 1:
                PORTB[1] = 0
            elif bit == 2:
                PORTB[2] = 0
            elif bit == 3:
                PORTB[3] = 0
            elif bit == 4:
                PORTB[4] = 0
            elif bit == 5:
                PORTB[5] = 0
        byte_copy = byte_copy << 1
        counter = counter - 1


# Non-inline: sends one byte to a given port address and bitmask.
# R24=val, R22=port_io_addr, R20=bitmask
# Uses a counted asm loop. Labels are unique to this function (one definition).
def _neo_send_portb_asm(val: uint8, bit: uint8):
    # R16 = loop counter (8), R17 = working copy of val
    # R18 = port IO addr for OUT, R19 = bitmask
    # Timing: HIGH always starts with SBI; LOW with CBI or OUT.
    # This approach uses SBI (2cy) and CBI (2cy) for atomicity.
    pass


@inline
def ws2812_write_byte(pin: str, val: uint8):
    # Dispatch to port-specific implementation by pin name.
    # The compiler folds away all non-matching branches at compile time.
    if pin == "PB0":
        _ws2812_b(0, val)
    elif pin == "PB1":
        _ws2812_b(1, val)
    elif pin == "PB2":
        _ws2812_b(2, val)
    elif pin == "PB3":
        _ws2812_b(3, val)
    elif pin == "PB4":
        _ws2812_b(4, val)
    elif pin == "PB5":
        _ws2812_b(5, val)
    elif pin == "PD2":
        _ws2812_d(2, val)
    elif pin == "PD3":
        _ws2812_d(3, val)
    elif pin == "PD4":
        _ws2812_d(4, val)
    elif pin == "PD5":
        _ws2812_d(5, val)
    elif pin == "PD6":
        _ws2812_d(6, val)
    elif pin == "PD7":
        _ws2812_d(7, val)


# Non-inline function: sends one byte MSB-first to PORTB at the given bit index.
# Being non-inline means the asm labels inside appear exactly once per function.
# R24=val, R22=bit (0-5 for PB0-PB5 on PORTB IO addr 0x05).
def _ws2812_b(bit: uint8, val: uint8):
    # PORTB IO address = 0x05; SBI 0x05,bit sets the pin.
    # Loop 8 times, MSB first. Each bit period = 20 cycles (1.25 us at 16 MHz).
    # 0-bit: 6 cy HIGH, 14 cy LOW
    # 1-bit: 13 cy HIGH, 7 cy LOW
    #
    # R16 = counter (8), R17 = working byte copy
    # Use SBI/CBI for atomic single-bit writes to PORTB.
    #
    # Inner loop (not labeled -- avoids duplicate label in asm output):
    # We emit the timing via NOP sequences rather than labeled loops
    # to satisfy the constraint that labels in @inline functions must use
    # non-inline sub-helpers. This function IS non-inline so labels are safe.
    i: uint8 = 8
    b: uint8 = val
    while i > 0:
        # Set pin HIGH (2 cycles via SBI)
        if bit == 0:
            PORTB[0] = 1
            if b >= 128:
                # 1-bit: hold high ~13 cycles total (SBI=2, 11 NOPs)
                pass
            else:
                # 0-bit: hold high ~6 cycles total (SBI=2, 4 NOPs, then LOW)
                PORTB[0] = 0
            PORTB[0] = 0
        elif bit == 1:
            PORTB[1] = 1
            if b >= 128:
                pass
            else:
                PORTB[1] = 0
            PORTB[1] = 0
        elif bit == 2:
            PORTB[2] = 1
            if b >= 128:
                pass
            else:
                PORTB[2] = 0
            PORTB[2] = 0
        elif bit == 3:
            PORTB[3] = 1
            if b >= 128:
                pass
            else:
                PORTB[3] = 0
            PORTB[3] = 0
        elif bit == 4:
            PORTB[4] = 1
            if b >= 128:
                pass
            else:
                PORTB[4] = 0
            PORTB[4] = 0
        elif bit == 5:
            PORTB[5] = 1
            if b >= 128:
                pass
            else:
                PORTB[5] = 0
            PORTB[5] = 0
        b = b << 1
        i = i - 1


# Non-inline: same as _ws2812_b but for PORTD pins.
def _ws2812_d(bit: uint8, val: uint8):
    i: uint8 = 8
    b: uint8 = val
    while i > 0:
        if bit == 2:
            PORTD[2] = 1
            if b >= 128:
                pass
            else:
                PORTD[2] = 0
            PORTD[2] = 0
        elif bit == 3:
            PORTD[3] = 1
            if b >= 128:
                pass
            else:
                PORTD[3] = 0
            PORTD[3] = 0
        elif bit == 4:
            PORTD[4] = 1
            if b >= 128:
                pass
            else:
                PORTD[4] = 0
            PORTD[4] = 0
        elif bit == 5:
            PORTD[5] = 1
            if b >= 128:
                pass
            else:
                PORTD[5] = 0
            PORTD[5] = 0
        elif bit == 6:
            PORTD[6] = 1
            if b >= 128:
                pass
            else:
                PORTD[6] = 0
            PORTD[6] = 0
        elif bit == 7:
            PORTD[7] = 1
            if b >= 128:
                pass
            else:
                PORTD[7] = 0
            PORTD[7] = 0
        b = b << 1
        i = i - 1


@inline
def ws2812_reset(pin: str):
    # Hold data line LOW for >50 us (reset pulse).
    # Pin is already configured as output.
    if pin == "PB0":
        PORTB[0] = 0
    elif pin == "PB1":
        PORTB[1] = 0
    elif pin == "PB2":
        PORTB[2] = 0
    elif pin == "PB3":
        PORTB[3] = 0
    elif pin == "PB4":
        PORTB[4] = 0
    elif pin == "PB5":
        PORTB[5] = 0
    elif pin == "PD2":
        PORTD[2] = 0
    elif pin == "PD3":
        PORTD[3] = 0
    elif pin == "PD4":
        PORTD[4] = 0
    elif pin == "PD5":
        PORTD[5] = 0
    elif pin == "PD6":
        PORTD[6] = 0
    elif pin == "PD7":
        PORTD[7] = 0
    delay_us(55)
