# machine.Timer freq= integration test fixture
#
# Verifies MicroPython-compatible freq= parameter for Timer.init() and Timer():
#   Timer(1, freq=10, callback=on_tick)  -- 10 Hz == 100 ms period
#
# Expected behaviour:
#   PB5 (D13 LED) starts LOW.
#   Timer1 fires at 10 Hz (every 100 ms); each tick toggles PB5.
#   First tick at ~100 ms: PB5 HIGH.
#   Second tick at ~200 ms: PB5 LOW.

from machine import Pin, Timer
from pymcu.types import uint8


led_state: uint8 = 0


def on_tick():
    global led_state
    if led_state:
        led_state = 0
    else:
        led_state = 1


def main():
    led = Pin("PB5", Pin.OUT)

    t = Timer(1, freq=10, callback=on_tick)

    while True:
        if led_state:
            led.high()
        else:
            led.low()
