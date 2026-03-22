# PIC16F84A: Knight Rider LED scanner
# Demonstrates: Direct register access, bit manipulation, delay, while loop
#
# Hardware: PIC16F84A @ 4 MHz
#   - 8 LEDs on PORTB (RB0–RB7), active high
#
# The pattern scans a single lit LED back and forth across PORTB,
# like the iconic Knight Rider KITT scanner. This example uses
# direct register manipulation instead of the Pin HAL to show
# how pymcu maps to raw hardware on constrained devices (68B RAM).
#
from whisnake.chips.pic16f84a import PORTB, TRISB
from whisnake.time import delay_ms
from whisnake.types import uint8

SPEED = 80  # milliseconds between steps

def main():
    # Set all PORTB pins as output
    TRISB.value = 0x00

    # Start with LED on RB0
    pos: uint8 = 0
    going_right: uint8 = 1

    while True:
        # Light only the LED at current position
        PORTB.value = 1 << pos

        delay_ms(SPEED)

        # Bounce at the edges
        if going_right == 1:
            if pos == 7:
                going_right = 0
                pos = pos - 1
            else:
                pos = pos + 1
        else:
            if pos == 0:
                going_right = 1
                pos = pos + 1
            else:
                pos = pos - 1
