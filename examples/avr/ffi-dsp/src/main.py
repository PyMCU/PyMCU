# PyMCU -- ffi-dsp: C interop with two C source files
#
# Demonstrates a more complex @extern / [tool.whip.ffi] setup:
#   - Two C files compiled with avr-gcc (math_utils.c, filter.c)
#   - Six @extern function declarations
#   - Functions with 2 and 3 arguments
#   - Results verified over UART
#
# Expected UART output (9600 baud, 16 MHz):
#   "FFIDSP\n"   -- boot banner
#   "C:64\n"     -- c_clamp8(200, 10, 100) = 100 = 0x64
#   "L:64\n"     -- c_lerp8(0, 200, 128)   = 100 = 0x64
#   "K:64\n"     -- c_scale8(128, 200)     = 100 = 0x64
#   "E:57\n"     -- c_smooth8(50, 200, 64) =  87 = 0x57
#   "D:00\n"     -- c_deadband8(30, 50)    =   0 = 0x00
#   "B:1E\n"     -- c_deadband8(80, 50)    =  30 = 0x1E
#   "OK\n"       -- done
#
# Math:
#   lerp(0,200,128)    = 0 + 200*128/255  = 100
#   scale(128,200)     = 128*200/255      = 100
#   smooth(50,200,64)  = 50 + 150*64/256 =  87
#   deadband(30,50)    = 0   (30 < 50)
#   deadband(80,50)    = 30  (80 - 50)

from whisnake.types import uint8, inline
from whisnake.hal.uart import UART
from pymcu.ffi import extern


# --- math_utils.c ---

@extern("c_clamp8")
def c_clamp8(val: uint8, lo: uint8, hi: uint8) -> uint8:
    pass

@extern("c_lerp8")
def c_lerp8(a: uint8, b: uint8, t: uint8) -> uint8:
    pass

@extern("c_scale8")
def c_scale8(val: uint8, scale: uint8) -> uint8:
    pass


# --- filter.c ---

@extern("c_smooth8")
def c_smooth8(prev: uint8, curr: uint8, alpha: uint8) -> uint8:
    pass

@extern("c_deadband8")
def c_deadband8(val: uint8, width: uint8) -> uint8:
    pass


# --- helpers ---

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

@inline
def print_hex(uart: UART, tag: uint8, val: uint8):
    uart.write(tag)
    uart.write(':')
    uart.write(nibble_hi(val))
    uart.write(nibble_lo(val))
    uart.write('\n')


def main():
    uart = UART(9600)
    uart.println("FFIDSP")

    # c_clamp8(200, 10, 100) = 100 = 0x64
    r1: uint8 = c_clamp8(200, 10, 100)
    print_hex(uart, 'C', r1)

    # c_lerp8(0, 200, 128) = 0 + 200*128/255 = 100 = 0x64
    r2: uint8 = c_lerp8(0, 200, 128)
    print_hex(uart, 'L', r2)

    # c_scale8(128, 200) = 128*200/255 = 100 = 0x64
    r3: uint8 = c_scale8(128, 200)
    print_hex(uart, 'K', r3)

    # c_smooth8(50, 200, 64) = 50 + 150*64/256 = 87 = 0x57
    r4: uint8 = c_smooth8(50, 200, 64)
    print_hex(uart, 'E', r4)

    # c_deadband8(30, 50) = 0 (30 < 50) = 0x00
    r5: uint8 = c_deadband8(30, 50)
    print_hex(uart, 'D', r5)

    # c_deadband8(80, 50) = 30 = 0x1E
    r6: uint8 = c_deadband8(80, 50)
    print_hex(uart, 'B', r6)

    uart.println("OK")

    while True:
        pass
