# ATmega328P: 74HC595 shift register — bit-bang SPI, running light
# Tests: variable-amount right shift (non-constant RShift path in codegen),
#        bitwise AND with variable, uint8 MSB extraction, rotation arithmetic,
#        while-loop with uint8 counter, nested conditionals
#
# Hardware: 74HC595 connected to ATmega328P:
#   SER   (pin 14) -> PB0   data
#   SRCLK (pin 11) -> PB1   shift clock
#   RCLK  (pin 12) -> PB2   latch/storage clock
#   OE    (pin 13) -> GND   (always enabled)
#   MR    (pin 10) -> VCC   (no reset)
#   Q0-Q7 -> 8 LEDs with resistors
#
from pymcu.types import uint8
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def main():
    data  = Pin("PB0", Pin.OUT)
    clock = Pin("PB1", Pin.OUT)
    latch = Pin("PB2", Pin.OUT)
    uart  = UART(9600)

    # Start with single bit at position 0: running light
    pattern: uint8 = 0x01

    while True:
        # Pull latch low before clocking data
        latch.low()

        # Shift out 8 bits, MSB first
        bit: uint8 = 7
        while bit < 8:           # counts 7, 6, ..., 0 (wraps at 255 after 0)
            # Extract bit at position `bit` using variable shift (non-constant path)
            shifted: uint8 = pattern >> bit
            if shifted & 1:
                data.high()
            else:
                data.low()

            # Rising edge on shift clock
            clock.high()
            clock.low()

            bit = bit - 1

        # Rising edge on latch: push shift register -> storage register
        latch.high()

        # Send current pattern over UART for debugging
        uart.write(pattern)

        # Rotate left: msb -> lsb
        msb: uint8 = (pattern >> 7) & 1
        pattern = (pattern << 1) | msb

        delay_ms(150)
