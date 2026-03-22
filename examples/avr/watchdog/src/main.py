# ATmega328P: Watchdog Timer demo
# Tests: Watchdog.enable(), Watchdog.feed(), Watchdog.disable()
#
# Hardware: Arduino Uno (any AVR)
# Behavior: prints "WDT INIT" on start, feeds watchdog every loop iteration,
#           prints "FEED" on each feed, disables WDT after 10 feeds, prints "DONE"
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART
from whipsnake.hal.watchdog import Watchdog

def main():
    uart = UART(9600)
    wdt  = Watchdog(timeout_ms=500)

    uart.println("WDT INIT")

    wdt.enable()

    i: uint8 = 0
    while i < 10:
        wdt.feed()
        uart.println("FEED")
        i += 1

    wdt.disable()

    uart.println("DONE")

    while True:
        pass
