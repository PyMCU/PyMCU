# PyMCU -- slice-ops: Compile-time array slice indexing (PEP 197, F8)
#
# Demonstrates:
#   - arr[start:stop]   -- extract a sub-array
#   - arr[start:stop:step] -- every Nth element
#   - arr[:stop]        -- from beginning
#   - arr[start:]       -- to end
#
# Output on UART (9600 baud):
#   "SL\n"    -- boot banner
#   "A:0A\n"  -- first[0] = 10
#   "B:28\n"  -- last[3]  = 40 (0x28)
#   "C:0A\n"  -- even[0]  = 10
#   "D:1E\n"  -- even[1]  = 30 (0x1E)
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART


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

    src: uint8[8]
    src__0 = 10
    src__1 = 20
    src__2 = 30
    src__3 = 40
    src__4 = 50
    src__5 = 60
    src__6 = 70
    src__7 = 80

    # Slice: first 4 elements
    first: uint8[4] = src[0:4]
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(first__0))
    uart.write(nibble_lo(first__0))
    uart.write('\n')

    # Slice: last 4 elements
    last: uint8[4] = src[4:8]
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hi(last__3))
    uart.write(nibble_lo(last__3))
    uart.write('\n')

    # Slice: every other element
    even: uint8[4] = src[0:8:2]
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hi(even__0))
    uart.write(nibble_lo(even__0))
    uart.write('\n')

    uart.write('D')
    uart.write(':')
    uart.write(nibble_hi(even__1))
    uart.write(nibble_lo(even__1))
    uart.write('\n')

    while True:
        pass
