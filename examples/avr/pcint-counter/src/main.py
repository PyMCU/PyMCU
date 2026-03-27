# ATmega328P: Pin Change Interrupt (PCINT0) button press counter
#
# Hardware: Arduino Uno
#   - Button on PB0 (Arduino pin 8), active-low with internal pull-up
#   - Serial terminal at 9600 baud: prints "COUNT:NN\n" on each press
#
# PCINT0 fires on any edge of PB0. btn.irq() sets PCMSK0[0], PCICR[0],
# and SEI automatically. The ISR sets a GPIOR0 flag; main reads PB0 to
# distinguish press (low) from release (high).
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART


def pcint0_isr():
    GPIOR0[0] = 1


def main():
    btn  = Pin("PB0", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)
    btn.irq(3, pcint0_isr)

    GPIOR0[0] = 0
    uart.println("PCINT COUNTER")

    count: uint8 = 0

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            # Only count falling edges (button pressed = low)
            if btn.value() == 0:
                count += 1
                uart.write_str("COUNT:")
                uart.write((count / 10) + 48)
                uart.write((count % 10) + 48)
                uart.write('\n')
