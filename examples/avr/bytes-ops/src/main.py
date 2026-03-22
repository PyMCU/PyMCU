# PyMCU -- bytes-ops: bytes literal, int.from_bytes, enumerate on array
#
# Demonstrates:
#   - b"..." bytes literal treated as uint8 list
#   - for x in b"...": iteration (unrolled at compile time)
#   - uint8[N] = b"..." array initialisation
#   - uint8[N] = b"\x00" * N zero-fill
#   - int.from_bytes([lo, hi], 'little') -> uint16 (runtime)
#   - int.from_bytes(b"\x01\x02", 'big') -> uint16 (compile-time fold)
#   - for i, x in enumerate(arr): variable-index array unrolled
#
# Output on UART (9600 baud):
#   "BYTES\n"       -- boot banner
#   "F:8B\n"        -- for-in bytes sum 0xDE+0xAD = 0x18B; low byte = 0x8B
#   "A:1F\n"        -- array init: buf[1] = 0x1F
#   "Z:00\n"        -- zero-fill: zeros[0] = 0x00
#   "L:01\n"        -- from_bytes little [1,2] -> 0x0201; low = 0x01
#   "B:02\n"        -- from_bytes big [1,2] -> 0x0102; low = 0x02
#   "I:03\n"        -- enumerate idx_sum 0+1+2 = 3 = 0x03
#   "V:3C\n"        -- enumerate val_sum 10+20+30 = 60 = 0x3C
#
from whisnake.types import uint8, uint16
from whisnake.hal.uart import UART

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

    uart.println("BYTES")

    # -- 1. for x in b"\xDE\xAD" (unrolled, sum low byte = 0x8B) --
    sum_f: uint8 = 0
    for x in b"\xDE\xAD":
        sum_f = sum_f + x
    uart.write('F')
    uart.write(':')
    uart.write(nibble_hex_hi(sum_f))
    uart.write(nibble_hex_lo(sum_f))
    uart.write('\n')

    # -- 2. uint8[3] = b"\x0A\x1F\x2B"; buf[1] = 0x1F --
    buf: uint8[3] = b"\x0A\x1F\x2B"
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hex_hi(buf[1]))
    uart.write(nibble_hex_lo(buf[1]))
    uart.write('\n')

    # -- 3. uint8[4] = b"\x00" * 4; zeros[0] = 0x00 --
    zeros: uint8[4] = b"\x00" * 4
    uart.write('Z')
    uart.write(':')
    uart.write(nibble_hex_hi(zeros[0]))
    uart.write(nibble_hex_lo(zeros[0]))
    uart.write('\n')

    # -- 4. int.from_bytes runtime little [lo=1, hi=2] -> 0x0201; low = 0x01 --
    lo_b: uint8 = 1
    hi_b: uint8 = 2
    val_l: uint16 = int.from_bytes([lo_b, hi_b], 'little')
    low_l: uint8 = val_l & 0xFF
    uart.write('L')
    uart.write(':')
    uart.write(nibble_hex_hi(low_l))
    uart.write(nibble_hex_lo(low_l))
    uart.write('\n')

    # -- 5. int.from_bytes compile-time big b"\x01\x02" -> 0x0102; low = 0x02 --
    val_b2: uint16 = int.from_bytes(b"\x01\x02", 'big')
    low_b2: uint8 = val_b2 & 0xFF
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hex_hi(low_b2))
    uart.write(nibble_hex_lo(low_b2))
    uart.write('\n')

    # -- 6. enumerate on variable-index array [10, 20, 30] --
    data: uint8[3] = [10, 20, 30]
    idx_sum: uint8 = 0
    val_sum: uint8 = 0
    for i, x in enumerate(data):
        idx_sum = idx_sum + i
        val_sum = val_sum + x

    # idx_sum = 0+1+2 = 3 = 0x03
    uart.write('I')
    uart.write(':')
    uart.write(nibble_hex_hi(idx_sum))
    uart.write(nibble_hex_lo(idx_sum))
    uart.write('\n')

    # val_sum = 10+20+30 = 60 = 0x3C
    uart.write('V')
    uart.write(':')
    uart.write(nibble_hex_hi(val_sum))
    uart.write(nibble_hex_lo(val_sum))
    uart.write('\n')

    while True:
        pass
