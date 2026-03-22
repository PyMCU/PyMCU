# PyMCU -- edge-cases: bit operations and comparison edge cases
# Tests: uint8/uint16 shifts, XOR, AND, comparisons
# Output on UART: "EDGE\n" banner, then A:HH, B:H, C:HH, T/F, D:H
from whisnake.types import uint8, uint16
from whisnake.hal.uart import UART
from pymcu.time import delay_ms

def nibble_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48
    return n + 55

def main():
    uart = UART(9600)
    uart.println("EDGE")

    # Shift by 7: 1 << 7 = 128 = 0x80
    a: uint8 = 1 << 7
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hex(a >> 4))
    uart.write(nibble_hex(a & 0xF))
    uart.write('\n')

    # XOR self-cancellation: x ^ x = 0
    x: uint8 = 0xAB
    b: uint8 = x ^ x
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hex(b))
    uart.write('\n')

    # AND with 0xFF: identity
    c: uint8 = 0xCD & 0xFF
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hex(c >> 4))
    uart.write(nibble_hex(c & 0xF))
    uart.write('\n')

    # Zero comparison: 0 < 1 = true (send T)
    z: uint8 = 0
    if z < 1:
        uart.write('T')
    else:
        uart.write('F')
    uart.write('\n')

    # uint16 bit 15: 0x8000 >> 15 = 1
    big: uint16 = 0x8000
    d: uint8 = (big >> 15) & 0x01
    uart.write('D')
    uart.write(':')
    uart.write(nibble_hex(d))
    uart.write('\n')

    while True:
        delay_ms(1000)
