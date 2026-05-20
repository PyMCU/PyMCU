# gpio-overhead: measure HAL cycle cost via BREAK checkpoints
#
# BREAK layout (order of execution):
#   B1/B2 -- Pin.high()           -> 2 cycles  (SBI 0x05,5)
#   B3/B4 -- Pin.low()            -> 2 cycles  (CBI 0x05,5)
#   B5/B6 -- delay_ms(1)          -> ~16 000 cycles  (1 ms at 16 MHz)
#   B7/B8 -- full blink iteration -> ~32 000 000 cycles (2000 ms at 16 MHz)
#
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms
from pymcu.types import asm


def main():
    led = Pin("PB5", Pin.OUT)

    asm("BREAK")    # B1: before Pin.high()
    led.high()
    asm("BREAK")    # B2: after  Pin.high()

    asm("BREAK")    # B3: before Pin.low()
    led.low()
    asm("BREAK")    # B4: after  Pin.low()

    asm("BREAK")    # B5: before delay_ms(1)
    delay_ms(1)
    asm("BREAK")    # B6: after  delay_ms(1)

    asm("BREAK")    # B7: start of full blink iteration
    led.high()
    delay_ms(1000)
    led.low()
    delay_ms(1000)
    asm("BREAK")    # B8: end of full blink iteration

    while True:
        pass
