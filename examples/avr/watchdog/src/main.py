# ATmega328P: Watchdog Timer demo
# Tests: Watchdog.enable(), Watchdog.feed(), Watchdog.disable()
#
# Hardware: Arduino Uno (any AVR)
# Behavior: prints "WDT INIT" on start, feeds watchdog every loop iteration,
#           prints "FEED" on each feed, disables WDT after 10 feeds, prints "DONE"
#
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.watchdog import Watchdog

def main():
    uart = UART(9600)
    wdt  = Watchdog(timeout_ms=500)

    uart.write_str("WDT INIT")
    uart.write(10)

    wdt.enable()

    i: uint8 = 0
    while i < 10:
        wdt.feed()
        uart.write_str("FEED")
        uart.write(10)
        i = i + 1

    wdt.disable()

    uart.write_str("DONE")
    uart.write(10)

    while True:
        pass
