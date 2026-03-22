# PyMCU -- ffi-arduino: Arduino-compatible utility functions via C interop
#
# Demonstrates @extern calling Arduino-style C utility functions:
#   arduino_map(x, in_max, out_max)   -- scales x from [0,in_max] to [0,out_max]
#   arduino_constrain(x, lo, hi)      -- clamps x to [lo, hi]
#   adc_to_pwm(adc_val)               -- maps 10-bit ADC to 8-bit PWM
#
# These mirror Arduino's famous map() and constrain() macros, implemented
# in portable C and linked via avr-gcc + avr-ld.
#
# Expected UART output (9600 baud, 16 MHz):
#   "ARDUINO\n"   -- boot banner
#   "M:7F\n"      -- arduino_map(512, 1023, 255)    = 127 = 0x7F
#   "F:FF\n"      -- arduino_map(1023, 1023, 255)   = 255 = 0xFF
#   "Z:00\n"      -- arduino_map(0, 1023, 255)      =   0 = 0x00
#   "H:C8\n"      -- arduino_constrain(300, 10, 200)= 200 = 0xC8  (clamped hi)
#   "L:0A\n"      -- arduino_constrain(5, 10, 200)  =  10 = 0x0A  (clamped lo)
#   "P:7F\n"      -- adc_to_pwm(512)                = 127 = 0x7F
#   "T:FF\n"      -- adc_to_pwm(1023)               = 255 = 0xFF
#   "OK\n"        -- done
#
# Math:
#   map(512, 1023, 255)  = 512*255/1023 = 127
#   constrain(300,10,200) = 200  (300 > 200)
#   constrain(5, 10, 200) = 10   (5 < 10)
#   adc_to_pwm(512)       = 512*255/1023 = 127

from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.uart import UART
from whipsnake.ffi import extern


# --- Arduino utility functions (c_src/arduino_utils.c) ---

@extern("arduino_map")
def arduino_map(x: uint16, in_max: uint16, out_max: uint16) -> uint16:
    pass

@extern("arduino_constrain")
def arduino_constrain(x: uint16, lo: uint16, hi: uint16) -> uint16:
    pass

@extern("adc_to_pwm")
def adc_to_pwm(adc_val: uint16) -> uint8:
    pass


# --- UART helpers ---

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
def print_u8(uart: UART, tag: uint8, val: uint8):
    uart.write(tag)
    uart.write(':')
    uart.write(nibble_hi(val))
    uart.write(nibble_lo(val))
    uart.write('\n')

@inline
def print_u16_lo(uart: UART, tag: uint8, val: uint16):
    lo: uint8 = val & 0xFF
    print_u8(uart, tag, lo)


def main():
    uart = UART(9600)
    uart.println("ARDUINO")

    # arduino_map(512, 1023, 255) = 127 = 0x7F
    m1: uint16 = arduino_map(512, 1023, 255)
    print_u16_lo(uart, 'M', m1)

    # arduino_map(1023, 1023, 255) = 255 = 0xFF
    m2: uint16 = arduino_map(1023, 1023, 255)
    print_u16_lo(uart, 'F', m2)

    # arduino_map(0, 1023, 255) = 0 = 0x00
    m3: uint16 = arduino_map(0, 1023, 255)
    print_u16_lo(uart, 'Z', m3)

    # arduino_constrain(300, 10, 200) = 200 = 0xC8  (upper clamp)
    c1: uint16 = arduino_constrain(300, 10, 200)
    print_u16_lo(uart, 'H', c1)

    # arduino_constrain(5, 10, 200) = 10 = 0x0A  (lower clamp)
    c2: uint16 = arduino_constrain(5, 10, 200)
    print_u16_lo(uart, 'L', c2)

    # adc_to_pwm(512) = 127 = 0x7F
    p1: uint8 = adc_to_pwm(512)
    print_u8(uart, 'P', p1)

    # adc_to_pwm(1023) = 255 = 0xFF
    p2: uint8 = adc_to_pwm(1023)
    print_u8(uart, 'T', p2)

    uart.println("OK")

    while True:
        pass
