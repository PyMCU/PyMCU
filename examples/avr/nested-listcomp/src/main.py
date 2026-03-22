# PyMCU -- nested-listcomp: nested list comprehension, if-filter, and bytearray
#
# Demonstrates:
#   1. Nested list comprehension (constant-index access):
#        [x + y for x in [1,2,3] for y in [10,20,30]]
#        = [11,21,31, 12,22,32, 13,23,33]
#        sum = 11+21+31+12+22+32+13+23+33 = 198 = 0xC6
#   2. if-filter list comprehension:
#        [x for x in [1,2,3,4,5,6] if x > 3]
#        = [4,5,6]  sum=15=0x0F
#   3. bytearray mutable buffer:
#        buf = bytearray(4), buf[0]=0xAA, buf[3]=0xBB
#        result = buf[0]+buf[3] = 0xAA+0xBB = 0x165, low byte = 0x65
#
# UART output (9600 baud):
#   "NLC\n"    -- boot banner
#   "N:C6\n"   -- nested listcomp sum 198=0xC6
#   "F:0F\n"   -- filtered listcomp sum 15=0x0F
#   "B:65\n"   -- bytearray write/read test
#
from whipsnake.types import uint8, uint16
from whipsnake.hal.uart import UART

def nibble_hex_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55

def nibble_hex_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55

def main():
    uart = UART(9600)

    uart.println("NLC")

    # 1. Nested list comprehension: [x+y for x in [1,2,3] for y in [10,20,30]]
    # = [11,21,31, 12,22,32, 13,23,33]  sum = 198 = 0xC6
    nested: uint8[9] = [x + y for x in [1, 2, 3] for y in [10, 20, 30]]
    sum_n: uint16 = 0
    sum_n = sum_n + nested[0]
    sum_n = sum_n + nested[1]
    sum_n = sum_n + nested[2]
    sum_n = sum_n + nested[3]
    sum_n = sum_n + nested[4]
    sum_n = sum_n + nested[5]
    sum_n = sum_n + nested[6]
    sum_n = sum_n + nested[7]
    sum_n = sum_n + nested[8]
    lo: uint8 = sum_n & 0xFF
    uart.write('N')
    uart.write(':')
    uart.write(nibble_hex_hi(lo))
    uart.write(nibble_hex_lo(lo))
    uart.write('\n')

    # 2. if-filter list comprehension: [x for x in [1,2,3,4,5,6] if x > 3]
    # = [4, 5, 6]  sum = 15 = 0x0F
    filtered: uint8[3] = [x for x in [1, 2, 3, 4, 5, 6] if x > 3]
    sum_f: uint8 = 0
    sum_f = sum_f + filtered[0]
    sum_f = sum_f + filtered[1]
    sum_f = sum_f + filtered[2]
    uart.write('F')
    uart.write(':')
    uart.write(nibble_hex_hi(sum_f))
    uart.write(nibble_hex_lo(sum_f))
    uart.write('\n')

    # 3. bytearray mutable buffer
    buf: bytearray = bytearray(4)
    buf[0] = 0xAA
    buf[3] = 0xBB
    result: uint8 = buf[0] + buf[3]
    # 0xAA(170) + 0xBB(187) = 357 = 0x165, low byte = 0x65
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hex_hi(result))
    uart.write(nibble_hex_lo(result))
    uart.write('\n')

    while True:
        pass
