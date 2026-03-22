# PyMCU -- slice-ops: Compile-time array slice indexing (PEP 197, F8)
#
# Demonstrates:
#   - arr[start:stop]      -- extract a sub-array
#   - arr[start:stop:step] -- every Nth element
#
# Output on UART (9600 baud):
#   "SL\n"    -- boot banner
#   "A:0A\n"  -- first[0] = 10
#   "B:50\n"  -- last[3]  = 80 (0x50)
#   "C:0A\n"  -- even[0]  = 10
#   "D:1E\n"  -- even[1]  = 30 (0x1E)
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart = UART(9600)
    uart.println("SL")

    src: uint8[8] = [10, 20, 30, 40, 50, 60, 70, 80]

    # Slice: first 4 elements  -> [10, 20, 30, 40]
    first: uint8[4] = src[0:4]
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(first[0]))
    uart.write(nibble_lo(first[0]))
    uart.write('\n')

    # Slice: last 4 elements -> [50, 60, 70, 80]
    last: uint8[4] = src[4:8]
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hi(last[3]))
    uart.write(nibble_lo(last[3]))
    uart.write('\n')

    # Slice: every other element (step=2) -> [10, 30, 50, 70]
    even: uint8[4] = src[0:8:2]
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hi(even[0]))
    uart.write(nibble_lo(even[0]))
    uart.write('\n')

    uart.write('D')
    uart.write(':')
    uart.write(nibble_hi(even[1]))
    uart.write(nibble_lo(even[1]))
    uart.write('\n')

    while True:
        pass
