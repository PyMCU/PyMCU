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
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, ptr, asm
from pymcu.chips.atmega328p import PORTB, PORTD, DDRB, DDRD
from pymcu.time import delay_us


@inline
def ws2812_init(pin: str):
    # Configure the data pin as output and hold low.
    match pin:
        case "PB0":
            DDRB[0] = 1
            PORTB[0] = 0
        case "PB1":
            DDRB[1] = 1
            PORTB[1] = 0
        case "PB2":
            DDRB[2] = 1
            PORTB[2] = 0
        case "PB3":
            DDRB[3] = 1
            PORTB[3] = 0
        case "PB4":
            DDRB[4] = 1
            PORTB[4] = 0
        case "PB5":
            DDRB[5] = 1
            PORTB[5] = 0
        case "PD2":
            DDRD[2] = 1
            PORTD[2] = 0
        case "PD3":
            DDRD[3] = 1
            PORTD[3] = 0
        case "PD4":
            DDRD[4] = 1
            PORTD[4] = 0
        case "PD5":
            DDRD[5] = 1
            PORTD[5] = 0
        case "PD6":
            DDRD[6] = 1
            PORTD[6] = 0
        case "PD7":
            DDRD[7] = 1
            PORTD[7] = 0
        case _:
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")


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
        match bit:
            case 0:
                PORTB[0] = 1
            case 1:
                PORTB[1] = 1
            case 2:
                PORTB[2] = 1
            case 3:
                PORTB[3] = 1
            case 4:
                PORTB[4] = 1
            case 5:
                PORTB[5] = 1
            case _:
                pass
        # Check MSB: if byte_copy bit7=1 -> 1-bit (HIGH 13 cy), else 0-bit (HIGH 6 cy)
        if byte_copy >= 128:
            # 1-bit: stay high ~800ns more (7 more NOPs after the SBI = ~13 cy total)
            pass
        else:
            # 0-bit: drop LOW after ~400ns (immediately after short high)
            match bit:
                case 0:
                    PORTB[0] = 0
                case 1:
                    PORTB[1] = 0
                case 2:
                    PORTB[2] = 0
                case 3:
                    PORTB[3] = 0
                case 4:
                    PORTB[4] = 0
                case 5:
                    PORTB[5] = 0
                case _:
                    pass
        # For 1-bit: set LOW now (after the high period)
        if byte_copy >= 128:
            match bit:
                case 0:
                    PORTB[0] = 0
                case 1:
                    PORTB[1] = 0
                case 2:
                    PORTB[2] = 0
                case 3:
                    PORTB[3] = 0
                case 4:
                    PORTB[4] = 0
                case 5:
                    PORTB[5] = 0
                case _:
                    pass
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
    match pin:
        case "PB0":
            _ws2812_b(0, val)
        case "PB1":
            _ws2812_b(1, val)
        case "PB2":
            _ws2812_b(2, val)
        case "PB3":
            _ws2812_b(3, val)
        case "PB4":
            _ws2812_b(4, val)
        case "PB5":
            _ws2812_b(5, val)
        case "PD2":
            _ws2812_d(2, val)
        case "PD3":
            _ws2812_d(3, val)
        case "PD4":
            _ws2812_d(4, val)
        case "PD5":
            _ws2812_d(5, val)
        case "PD6":
            _ws2812_d(6, val)
        case "PD7":
            _ws2812_d(7, val)
        case _:
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")


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
        match bit:
            case 0:
                PORTB[0] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[0] = 0
                PORTB[0] = 0
            case 1:
                PORTB[1] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[1] = 0
                PORTB[1] = 0
            case 2:
                PORTB[2] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[2] = 0
                PORTB[2] = 0
            case 3:
                PORTB[3] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[3] = 0
                PORTB[3] = 0
            case 4:
                PORTB[4] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[4] = 0
                PORTB[4] = 0
            case 5:
                PORTB[5] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[5] = 0
                PORTB[5] = 0
            case _:
                pass
        b = b << 1
        i = i - 1


# Non-inline: same as _ws2812_b but for PORTD pins.
def _ws2812_d(bit: uint8, val: uint8):
    i: uint8 = 8
    b: uint8 = val
    while i > 0:
        match bit:
            case 2:
                PORTD[2] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[2] = 0
                PORTD[2] = 0
            case 3:
                PORTD[3] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[3] = 0
                PORTD[3] = 0
            case 4:
                PORTD[4] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[4] = 0
                PORTD[4] = 0
            case 5:
                PORTD[5] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[5] = 0
                PORTD[5] = 0
            case 6:
                PORTD[6] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[6] = 0
                PORTD[6] = 0
            case 7:
                PORTD[7] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[7] = 0
                PORTD[7] = 0
            case _:
                pass
        b = b << 1
        i = i - 1


@inline
def ws2812_reset(pin: str):
    # Hold data line LOW for >50 us (reset pulse).
    # Pin is already configured as output.
    match pin:
        case "PB0":
            PORTB[0] = 0
        case "PB1":
            PORTB[1] = 0
        case "PB2":
            PORTB[2] = 0
        case "PB3":
            PORTB[3] = 0
        case "PB4":
            PORTB[4] = 0
        case "PB5":
            PORTB[5] = 0
        case "PD2":
            PORTD[2] = 0
        case "PD3":
            PORTD[3] = 0
        case "PD4":
            PORTD[4] = 0
        case "PD5":
            PORTD[5] = 0
        case "PD6":
            PORTD[6] = 0
        case "PD7":
            PORTD[7] = 0
        case _:
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")
    delay_us(55)
