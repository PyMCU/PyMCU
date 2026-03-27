# ATmega328P: Sleep / Power Management demo
# Tests: sleep_idle(), sleep_power_down() from pymcu.hal.power
#
# Behavior: prints "SLEEP" before sleeping in idle mode, wakes on INT0 (PD2 button),
#           prints "WAKE" after each wake. Counts 5 wakes then prints "DONE".
#
# Hardware: button on PD2 (INT0, pull-up), UART at 9600 baud
#
from pymcu.types import uint8, interrupt, asm
from pymcu.chips.atmega328p import EICRA, EIMSK, GPIOR0
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin
from pymcu.hal.power import sleep_idle

@interrupt(0x0002)   # INT0 vector (word addr 1)
def int0_isr():
    GPIOR0[0] = 1    # set wakeup flag

def main():
    uart = UART(9600)
    btn  = Pin("PD2", Pin.IN)
    btn.init(Pin.IN, Pin.PULL_UP)

    uart.println("SLEEP DEMO")

    # Configure INT0: falling edge (EICRA ISC01=1, ISC00=0)
    EICRA.value = 0x02
    EIMSK.value = 0x01   # enable INT0
    GPIOR0[0] = 0
    asm("sei")

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
