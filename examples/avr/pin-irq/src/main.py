# ATmega328P: Pin.irq() -- INT0 falling-edge interrupt on PD2
#
# Demonstrates:
#   - Pin.irq(Pin.IRQ_FALLING, handler) to configure hardware INT0
#   - compile_isr() intrinsic auto-registers on_press at INT0 vector
#   - No @interrupt decorator required on the handler function
#   - EICRA[1]=1, EICRA[0]=0 -> falling edge; EIMSK[0]=1 -> INT0 enable
#   - SREG[7]=1 -> global interrupts enabled (sei)
#
# Hardware: Arduino Uno
#   Button on PD2 (Arduino pin 2 = INT0), active low, with pull-up
#   UART TX at 9600 baud
#
# Output:
#   "PIN IRQ\n"    -- boot banner
#   count byte     -- raw byte (1, 2, 3...) sent on each button press
#
from whisnake.types import uint8
from whisnake.chips.atmega328p import GPIOR0
from whisnake.hal.gpio import Pin
from whisnake.hal.uart import UART


def on_press():
    # Minimal ISR: set atomic flag using SBI (no register corruption)
    GPIOR0[0] = 1


def main():
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)

    GPIOR0[0] = 0

    # Configure INT0 for falling-edge trigger and enable global interrupts.
    # compile_isr() inside pin_irq_setup automatically places on_press at
    # the INT0 vector -- no @interrupt decorator needed.
    btn.irq(Pin.IRQ_FALLING, on_press)

    count: uint8 = 0
    uart.println("PIN IRQ")

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            count = count + 1
            uart.write(count)
