# PyMCU -- array-ops: variable-index array access
# Tests: uint8[8] array, variable index loop, sum, min
# Output on UART: "ARRAY\n" banner, then S:HH (sum), M:HH (min)
from whisnake.types import uint8
from whisnake.hal.uart import UART
from pymcu.time import delay_ms

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
    uart.println("ARRAY")

    data: uint8[8] = [10, 20, 30, 40, 50, 60, 70, 80]

    # Sum all elements using variable index
    total: uint8 = 0
    i: uint8 = 0
    while i < 8:
        total += data[i]
        i += 1

    # Output sum as hex: 10+20+30+40+50+60+70+80 = 360 -> 0x168 -> low byte 0x68
    uart.write('S')
    uart.write(':')
    uart.write(nibble_hex_hi(total))
    uart.write(nibble_hex_lo(total))
    uart.write('\n')

    # Find minimum using variable index
    min_val: uint8 = data[0]
    i = 1
    while i < 8:
        if data[i] < min_val:
            min_val = data[i]
        i += 1

    uart.write('M')
    uart.write(':')
    uart.write(nibble_hex_hi(min_val))
    uart.write(nibble_hex_lo(min_val))
    uart.write('\n')

    while True:
        delay_ms(1000)
