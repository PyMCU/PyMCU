# PyMCU -- edge-cases: bit operations and comparison edge cases
from pymcu.types import uint8, uint16
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def nibble_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48
    return n + 55

def main():
    uart = UART(9600)
    uart.write(69)  # E
    uart.write(68)  # D
    uart.write(71)  # G
    uart.write(69)  # E
    uart.write(10)  # \n

    # Shift by 7: 1 << 7 = 128 = 0x80
    a: uint8 = 1 << 7
    uart.write(65)  # A
    uart.write(58)  # :
    uart.write(nibble_hex(a >> 4))
    uart.write(nibble_hex(a & 0xF))
    uart.write(10)  # \n

    # XOR self-cancellation: x ^ x = 0
    x: uint8 = 0xAB
    b: uint8 = x ^ x
    uart.write(66)  # B
    uart.write(58)  # :
    uart.write(nibble_hex(b))
    uart.write(10)  # \n

    # AND with 0xFF: identity
    c: uint8 = 0xCD & 0xFF
    uart.write(67)  # C
    uart.write(58)  # :
    uart.write(nibble_hex(c >> 4))
    uart.write(nibble_hex(c & 0xF))
    uart.write(10)  # \n

    # Zero comparison: 0 < 1 = true (send T)
    z: uint8 = 0
    if z < 1:
        uart.write(84)  # T
    else:
        uart.write(70)  # F
    uart.write(10)  # \n

    # uint16 bit 15: 0x8000 >> 15 = 1
    big: uint16 = 0x8000
    d: uint8 = (big >> 15) & 0x01
    uart.write(68)  # D
    uart.write(58)  # :
    uart.write(nibble_hex(d))
    uart.write(10)  # \n

    while True:
        delay_ms(1000)
