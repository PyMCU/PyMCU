# PyMCU -- zca-instance-array: arrays of ZCA instances, submodule-free regression.
#
# Exercises the full compile-time ZCA-instance-array chain that the CircuitPython
# digitalio idiom leans on, but with a plain pymcu class so the test does not depend
# on the compat submodule:
#   - list comprehension over a string tuple constructing ZCA instances
#   - for-in over the instance array calling a nested method (off -> _pin.low)
#   - enumerate over the array driving a PROPERTY SETTER with a runtime value
#     (level = (pattern >> bit) & 1 -> nested _pin.high/low)
#
# After main(), with pattern=1: PD5=HIGH (bit0), PD6=LOW (bit1), PD7=LOW (bit2),
# and DDRD bits 5-7 are outputs (Pin(..., OUT) in __init__).
#
# UART (9600): "ZA\n" boot banner, "DONE\n" after setup.
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin


class Led:
    @inline
    def __init__(self, pin_name):
        self._pin = Pin(pin_name, Pin.OUT)

    @inline
    def off(self):
        self._pin.low()

    @property
    def level(self) -> uint8:
        return 0

    @level.setter
    def level(self, v: uint8):
        if v:
            self._pin.high()
        else:
            self._pin.low()


def main():
    uart = UART(9600)
    uart.println("ZA")

    leds = [Led(p) for p in ("PD5", "PD6", "PD7")]

    for led in leds:
        led.off()

    pattern: uint8 = 1
    for bit, led in enumerate(leds):
        led.level = (pattern >> bit) & 1

    uart.println("DONE")

    while True:
        pass
