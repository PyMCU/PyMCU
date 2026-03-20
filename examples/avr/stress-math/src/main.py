# PyMCU -- stress-math: intensive uint8/uint16 arithmetic
# Tests: overflow wrapping, all binary ops, 4-arg calls, deeply nested
from pymcu.types import uint8, uint16
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def clamp_add(a: uint8, b: uint8, lo: uint8, hi: uint8) -> uint8:
    result: uint8 = a + b
    if result < lo:
        return lo
    if result > hi:
        return hi
    return result

def poly(x: uint8) -> uint8:
    # Returns (x*x + 3*x + 7) & 0xFF
    x2: uint8 = x * x
    t1: uint8 = 3 * x
    return (x2 + t1 + 7) & 0xFF

def nibble_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48
    return n + 55

def main():
    uart = UART(9600)
    uart.println("STRESS")

    # Test overflow wraparound: 255 + 1 = 0
    a: uint8 = 255
    b: uint8 = a + 1
    uart.write('O')
    uart.write(':')
    uart.write(nibble_hex(b))
    uart.write('\n')

    # Test 4-arg clamp_add: clamp_add(200, 40, 10, 230) = 230 (clamped to hi)
    # 200 + 40 = 240 which is > 230, so result is clamped to 230 = 0xE6
    r1: uint8 = clamp_add(200, 40, 10, 230)
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hex(r1 >> 4))
    uart.write(nibble_hex(r1 & 0x0F))
    uart.write('\n')

    # Test polynomial: poly(5) = 25 + 15 + 7 = 47 = 0x2F
    p: uint8 = poly(5)
    uart.write('P')
    uart.write(':')
    uart.write(nibble_hex(p >> 4))
    uart.write(nibble_hex(p & 0x0F))
    uart.write('\n')

    # 16-bit overflow: 65535 + 1 = 0
    w: uint16 = 65535
    w2: uint16 = w + 1
    uart.write('W')
    uart.write(':')
    uart.write(nibble_hex((w2 >> 12) & 0xF))
    uart.write(nibble_hex((w2 >> 8) & 0xF))
    uart.write(nibble_hex((w2 >> 4) & 0xF))
    uart.write(nibble_hex(w2 & 0xF))
    uart.write('\n')

    while True:
        delay_ms(1000)
