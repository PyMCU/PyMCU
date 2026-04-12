# ATmega328P: Sleep / Power Management demo
#
# Demonstrates:
#   - sleep_idle() from pymcu.hal.power
#   - Pin.irq(): sets up INT0 wake source, enables interrupt mask and SEI automatically
#   - GPIOR0 atomic flag: ISR sets bit on wake, main clears and acts
#
# Hardware: button on PD2 (INT0, pull-up), UART at 9600 baud
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin
from pymcu.hal.power import sleep_idle


def int0_isr():
    GPIOR0[0] = 1    # set wakeup flag


def main():
    uart = UART(9600)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)

    uart.println("SLEEP DEMO")

    GPIOR0[0] = 0
    btn.irq(Pin.IRQ_FALLING, int0_isr)   # configures INT0 + enables SEI

    count: uint8 = 0
    while count < 5:
        uart.println("SLEEP")
        sleep_idle()
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            count += 1
            uart.println("WAKE")

    uart.println("DONE")

    while True:
        pass
