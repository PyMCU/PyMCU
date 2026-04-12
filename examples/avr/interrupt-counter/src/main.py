# ATmega328P: External interrupt counter — INT0 ISR + UART output
#
# Demonstrates:
#   - Pin.irq(): sets up INT0 (IRQ_FALLING), enables interrupt mask and SEI automatically
#   - GPIOR0 atomic flag pattern: ISR sets bit (SBI), main clears it (CBI)
#   - No manual EICRA/EIMSK register writes or asm("SEI") needed
#
# Hardware: Arduino Uno
#   - Button on PD2 (Arduino pin 2 = INT0), active low, with pull-up
#   - LED on PB5 (Arduino pin 13, built-in)
#   - Serial terminal at 9600 baud — receives count byte on each press
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART


def int0_isr():
    # Minimal ISR: set event flag with SBI (atomic, no registers corrupted)
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)

    GPIOR0[0] = 0
    btn.irq(Pin.IRQ_FALLING, int0_isr)   # configures INT0 + enables SEI

    count: uint8 = 0
    uart.println("INT COUNTER")

    while True:
        if GPIOR0[0] == 1:           # SBIS 0x1E, 0
            GPIOR0[0] = 0            # CBI 0x1E, 0
            count += 1
            led.toggle()
            uart.write(count)        # send raw count byte over UART
