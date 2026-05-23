# PyMCU -- compat-mp-timer: machine.Timer MicroPython compatibility test fixture
#
# Verifies that machine.Timer(1, period=100, callback=fn) correctly:
#   1. Initialises Timer1 with prescaler 1024 and CTC compare for 100 ms
#   2. Fires the COMPA interrupt every ~100 ms
#   3. The callback toggles PB5 (Arduino D13 = onboard LED)
#
# Expected behaviour:
#   LED (PB5) starts LOW; after each 100 ms timer interrupt it toggles.
#   The test checks pin state at several simulation time points.
#
from machine import Timer, Pin
from pymcu.chips.atmega328p import PORTB, PINB


_led_state: int = 0


def on_timer():
    global _led_state
    if _led_state == 0:
        PORTB[5] = 1
        _led_state = 1
    else:
        PORTB[5] = 0
        _led_state = 0


def main():
    from pymcu.chips.atmega328p import DDRB
    DDRB[5] = 1    # PB5 as output
    PORTB[5] = 0   # start LOW

    tim = Timer(1, 100, on_timer)

    while True:
        pass
