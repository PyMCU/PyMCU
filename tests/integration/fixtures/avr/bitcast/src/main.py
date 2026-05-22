# PyMCU -- bitcast: raw byte reinterpretation without value conversion
#
# Tests:
#   1. bitcast(uint32, float): get IEEE 754 bits of 1.0 = 0x3F800000
#   2. bitcast(float, uint32): reconstruct float from bits (round-trip)
#   3. bitcast(int8, uint8): 255 -> -1 (signed reinterpretation)
#
# UART output (9600 baud):
#   "BC\n"    -- boot banner
#   "F:3F\n"  -- MSB of IEEE 754 bits of 1.0 = 0x3F
#   "G:80\n"  -- byte2 of IEEE 754 bits of 1.0 = 0x80
#   "H:01\n"  -- bitcast(float, 0x3F800000) == 1.0 -> 1
#   "I:01\n"  -- bitcast(int8, 255) < 0 -> 1
#
from pymcu.types import uint8, uint32, int8
from pymcu.hal.uart import UART


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def b3(v: uint32) -> uint8:
    return (v >> 24) & 0xFF


def b2(v: uint32) -> uint8:
    return (v >> 16) & 0xFF


def main():
    uart = UART(9600)

    uart.println("BC")

    # Test 1: bitcast(uint32, float) -- get IEEE 754 bits of 1.0
    # 1.0f = 0x3F800000: MSB=0x3F, byte2=0x80
    x: float = 1.0
    bits: uint32 = bitcast(uint32, x)
    msb: uint8 = b3(bits)
    byt2: uint8 = b2(bits)
    uart.write('F')
    uart.write(':')
    uart.write(nibble_hi(msb))
    uart.write(nibble_lo(msb))
    uart.write('\n')
    uart.write('G')
    uart.write(':')
    uart.write(nibble_hi(byt2))
    uart.write(nibble_lo(byt2))
    uart.write('\n')

    # Test 2: bitcast(float, uint32) round-trip -- reconstruct 1.0 from bits
    bits2: uint32 = 0x3F800000
    back: float = bitcast(float, bits2)
    ok: uint8 = 1 if back == 1.0 else 0
    uart.write('H')
    uart.write(':')
    uart.write(nibble_hi(ok))
    uart.write(nibble_lo(ok))
    uart.write('\n')

    # Test 3: bitcast(int8, uint8) -- 255 reinterpreted as int8 = -1
    u: uint8 = 255
    s: int8 = bitcast(int8, u)
    neg: uint8 = 1 if s < 0 else 0
    uart.write('I')
    uart.write(':')
    uart.write(nibble_hi(neg))
    uart.write(nibble_lo(neg))
    uart.write('\n')

    while True:
        pass
