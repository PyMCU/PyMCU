# NeoPixel (WS2812/WS2812B) driver
# Zero-cost abstraction -- mirrors the DHT11 driver pattern exactly.
#
# Usage:
#   from pymcu.drivers.neopixel import NeoPixel
#
#   strip = NeoPixel("PD6", 1)   # compile-time pin + count binding
#   strip.set_pixel(255, 0, 0)   # Red (GRB order sent to wire)
#   strip.show()                  # latch with reset pulse
#
# Protocol: WS2812 GRB order, 1.25 us per bit, reset >50 us.
# All timing is implemented in the arch-specific backend.
# Global interrupts must be disabled during transmission for correct timing;
# the user is responsible for calling asm("CLI") / asm("SEI") around show().
#
# Architecture dispatch:
#   match __CHIP__.arch  -- eliminates non-AVR targets at compile time
#   ws2812_write_byte(pin, byte) -- const[str] pin folds away non-matching pins
from pymcu.chips import __CHIP__
from pymcu.types import uint8, inline


class NeoPixel:

    @inline
    def _ws2812_init(self, pin: str):
        if __CHIP__.arch == "avr":
            from pymcu.drivers._neopixel.avr import ws2812_init
            ws2812_init(pin)

    @inline
    def _ws2812_write(self, pin: str, val: uint8):
        if __CHIP__.arch == "avr":
            from pymcu.drivers._neopixel.avr import ws2812_write_byte
            ws2812_write_byte(pin, val)

    @inline
    def _ws2812_show(self, pin: str):
        if __CHIP__.arch == "avr":
            from pymcu.drivers._neopixel.avr import ws2812_reset
            ws2812_reset(pin)

    @inline
    def __init__(self, pin: str, n: uint8):
        self._pin = pin
        self._n = n
        self._ws2812_init(pin)

    # Write one GRB byte sequence for a single pixel (r, g, b).
    # WS2812 wire order is Green, Red, Blue.
    @inline
    def set_pixel(self, r: uint8, g: uint8, b: uint8):
        self._ws2812_write(self._pin, g)
        self._ws2812_write(self._pin, r)
        self._ws2812_write(self._pin, b)

    # Write one raw byte to the strip (used for manual GRB sequencing).
    @inline
    def write_byte(self, val: uint8):
        self._ws2812_write(self._pin, val)

    # Send reset pulse (>50 us LOW) to latch the pixel data.
    @inline
    def show(self):
        self._ws2812_show(self._pin)
