# PyMCU -- inheritance-zca: ZCA class inheritance + function overloading
#
# Demonstrates:
#   - Single-level class inheritance: LED(GPIODevice) inherits on()/off()/read()
#   - Child class adding its own method (blink_code)
#   - Function overloading by type: encode(uint8) vs encode(uint16)
#
# Output on UART (9600 baud):
#   "IZ\n"         -- boot banner
#   "A:01\n"       -- LED.read() after on() returns 1 (output latch high)
#   "B:AB\n"       -- encode(uint8=0xAB) returns 0xAB
#   "C:1234\n"     -- encode(uint16=0x1234) high byte=0x12, low byte=0x34
#
from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.uart import UART
from whipsnake.hal.gpio import Pin


class GPIODevice:
    @inline
    def __init__(self, pin_name):
        self._pin = Pin(pin_name, Pin.OUT)

    @inline
    def on(self):
        self._pin.high()

    @inline
    def off(self):
        self._pin.low()

    @inline
    def read(self) -> uint8:
        return self._pin.value()


class LED(GPIODevice):
    @inline
    def blink_code(self, code: uint8):
        i: uint8 = 0
        while i < code:
            self.on()
            self.off()
            i += 1


# Function overloading: same name, different param types
@inline
def encode(val: uint8) -> uint8:
    return val

@inline
def encode(val: uint16) -> uint16:
    return val


def nibble_hex_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55

def nibble_hex_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart = UART(9600)

    uart.println("IZ")

    # -- Test inheritance: LED inherits on()/off()/read() from GPIODevice --
    led = LED("PB5")
    led.on()
    r: uint8 = led.read()
    led.off()
    # r should be 1 after on() on an output pin (output latch read = 1)
    uart.write('A')
    uart.write(':')
    uart.write(48 + r)  # '0' + r (expect '1')
    uart.write('\n')

    # -- Test function overloading: encode(uint8) --
    b: uint8 = encode(0xAB)
    uart.write('B')
    uart.write(':')
    uart.write(nibble_hex_hi(b))
    uart.write(nibble_hex_lo(b))
    uart.write('\n')

    # -- Test function overloading: encode(uint16) --
    w: uint16 = encode(0x1234)
    hi: uint8 = (w >> 8) & 0xFF
    lo: uint8 = w & 0xFF
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hex_hi(hi))
    uart.write(nibble_hex_lo(hi))
    uart.write(nibble_hex_hi(lo))
    uart.write(nibble_hex_lo(lo))
    uart.write('\n')

    while True:
        pass
