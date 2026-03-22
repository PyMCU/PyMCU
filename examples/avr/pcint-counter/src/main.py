# ATmega328P: Pin Change Interrupt (PCINT0) button press counter
#
# Tests: @interrupt on PCINT0 vector (0x0006 / word 0x0003),
#        PCMSK0 / PCICR setup, GPIOR0 atomic flag, Pin.value() read
#
# Hardware: Arduino Uno
#   - Button on PB0 (Arduino pin 8), active-low with internal pull-up
#   - Serial terminal at 9600 baud: prints "COUNT:NN\n" on each press
#
# PCINT0 fires on any edge of any PB pin enabled in PCMSK0.
# PCMSK0 = 0x01 enables only PB0 (bit 0 = PCINT0 pin).
# PCICR[0] = 1 enables the PCINT0 group (Port B).
# The ISR sets a flag; main reads PB0 to distinguish press (low) from release (high).
#
# PCINT0 vector: byte 0x0006, word 0x0003
#
from whipsnake.types import uint8, interrupt, asm
from whipsnake.chips.atmega328p import PCICR, PCMSK0, GPIOR0
from whipsnake.hal.gpio import Pin
from whipsnake.hal.uart import UART


@interrupt(0x0006)
def pcint0_isr():
    GPIOR0[0] = 1


def main():
    btn  = Pin("PB0", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)

    # Enable PCINT0 for PB0 only: set bit 0 of PCMSK0
    PCMSK0.value = 0x01
    # Enable PCINT0 group (Port B): PCICR[0] = PCIE0 = 1
    PCICR[0] = 1

    GPIOR0[0] = 0
    asm("SEI")

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
