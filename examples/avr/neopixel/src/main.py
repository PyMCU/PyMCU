# ATmega328P: NeoPixel (WS2812B) single-pixel color cycle
#
# Demonstrates the NeoPixel driver with a single WS2812B on PD6.
# Cycles through Red -> Green -> Blue, reporting each color via UART.
#
# WS2812B data pin: PD6 (Arduino pin 6)
# UART TX: PD1 at 9600 baud
#
# Protocol: 800 kHz (1.25 us/bit), GRB byte order, reset pulse >50 us.
# Global interrupts must be off during transmission for correct timing.
# This example disables interrupts only during pixel write + show.
#
from whipsnake.types import uint8, asm
from whipsnake.hal.uart import UART
from whipsnake.time import delay_ms
from whipsnake.drivers.neopixel import NeoPixel


def main():
    uart  = UART(9600)
    strip = NeoPixel("PD6", 1)

    uart.println("NEO")

    phase: uint8 = 0

    while True:
        asm("CLI")

        if phase == 0:
            # Red
            strip.set_pixel(255, 0, 0)
            strip.show()
        elif phase == 1:
            # Green
            strip.set_pixel(0, 255, 0)
            strip.show()
        elif phase == 2:
            # Blue
            strip.set_pixel(0, 0, 255)
            strip.show()

        asm("SEI")

        uart.write(phase)
        uart.write('\n')

        phase += 1
        if phase >= 3:
            phase = 0

        delay_ms(500)
