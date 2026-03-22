# ATmega328P: Exhaustive bitwise operator test
# Tests: |, &, ^, ~, <<, >> operators and their AugAssign forms
#        (exercises the AugAssign fix in AVRCodeGen)
#
# Expected UART output sequence (9600 baud):
#   0xFF=255  (0x0F | 0xF0)
#   0x0F= 15  (0xFF & 0x0F)
#   0xF0=240  (0x0F ^ 0xFF)
#   0xE0=224  (0xF0 << 1)
#   0x38= 56  (0xE0 >> 2)
#   0xC7=199  (~0x38)
#   0x07=  7  (0xC7 & 0x3F >> 3)
#   'D'       done marker
#
from whisnake.types import uint8
from whisnake.hal.uart import UART
from whisnake.hal.gpio import Pin
from whisnake.time import delay_ms


def main():
    uart = UART(9600)
    led  = Pin("PB5", Pin.OUT)

    val: uint8 = 0x0F

    # OR: 0x0F | 0xF0 = 0xFF
    val = val | 0xF0
    uart.write(val)          # 255

    # AND: 0xFF & 0x0F = 0x0F
    val = val & 0x0F
    uart.write(val)          # 15

    # XOR: 0x0F ^ 0xFF = 0xF0
    val = val ^ 0xFF
    uart.write(val)          # 240

    # Left shift by constant: 0xF0 << 1 = 0xE0
    val = val << 1
    uart.write(val)          # 224

    # Right shift by constant: 0xE0 >> 2 = 0x38
    val = val >> 2
    uart.write(val)          # 56

    # Bitwise NOT: ~0x38 = 0xC7
    val = ~val
    uart.write(val)          # 199

    # Combined: (0xC7 & 0x3F) >> 3 = (0x07) >> 3 = 0
    val = (val & 0x3F) >> 3
    uart.write(val)          # 7 → then >>3 gives 0

    # Variable-amount shift (exercises non-constant shift path)
    val = 0x01
    shift: uint8 = 3
    val = val << shift       # 0x01 << 3 = 0x08
    uart.write(val)          # 8

    val = val >> shift       # 0x08 >> 3 = 0x01
    uart.write(val)          # 1

    uart.write('D')          # done

    while True:
        led.toggle()
        delay_ms(500)
