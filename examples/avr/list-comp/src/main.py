# PyMCU -- list-comp: compile-time list comprehension + for-in unrolling
#
# Demonstrates:
#   - List comprehension over range(N):   [x * 2 for x in range(4)]
#   - List comprehension over list:       [v + 1 for v in [10, 20, 30]]
#   - for x in [v0, v1, ...] unrolled:    sum of constant list
#   - for ch in "ABC" already works       (string iteration, not re-tested here)
#
# Output on UART (9600 baud):
#   "LC\n"           -- boot banner
#   "R:HH\n"         -- result of comprehension-over-range  (expect 0+2+4+6=12 = 0x0C)
#   "L:HH\n"         -- result of comprehension-over-list   (11+21+31=63 = 0x3F)
#   "F:HH\n"         -- result of for-in-list sum           (1+3+5+7=16 = 0x10)
#
from pymcu.types import uint8
from pymcu.hal.uart import UART

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

    # Boot banner
    uart.write(76)   # L
    uart.write(67)   # C
    uart.write(10)   # \n

    # -- 1. List comprehension over range(4): [x*2 for x in range(4)] = [0,2,4,6] --
    doubled: uint8[4] = [x * 2 for x in range(4)]
    sum_r: uint8 = 0
    sum_r = sum_r + doubled[0]
    sum_r = sum_r + doubled[1]
    sum_r = sum_r + doubled[2]
    sum_r = sum_r + doubled[3]
    uart.write(82)   # R
    uart.write(58)   # :
    uart.write(nibble_hex_hi(sum_r))
    uart.write(nibble_hex_lo(sum_r))
    uart.write(10)   # \n

    # -- 2. List comprehension over constant list: [v+1 for v in [10,20,30]] = [11,21,31] --
    incremented: uint8[3] = [v + 1 for v in [10, 20, 30]]
    sum_l: uint8 = 0
    sum_l = sum_l + incremented[0]
    sum_l = sum_l + incremented[1]
    sum_l = sum_l + incremented[2]
    uart.write(76)   # L
    uart.write(58)   # :
    uart.write(nibble_hex_hi(sum_l))
    uart.write(nibble_hex_lo(sum_l))
    uart.write(10)   # \n

    # -- 3. for x in constant list: sum of [1, 3, 5, 7] = 16 --
    sum_f: uint8 = 0
    for x in [1, 3, 5, 7]:
        sum_f = sum_f + x
    uart.write(70)   # F
    uart.write(58)   # :
    uart.write(nibble_hex_hi(sum_f))
    uart.write(nibble_hex_lo(sum_f))
    uart.write(10)   # \n

    while True:
        pass
