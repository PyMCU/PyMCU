# ATmega328P: Button press counter with software debounce
# Tests: uint16 variable arithmetic, 16-bit JumpIfEqual (== 0, == 1000),
#        edge detection, UART low/high byte output
#
# Hardware: Arduino Uno
#   - Button on PD2 (active low, internal pull-up)
#   - LED on PB5 (built-in Arduino LED)
#   - Serial terminal at 9600 baud
#
# Each button press toggles the LED and increments a 16-bit counter.
# When the counter reaches 1000 it resets to 0 and sends 'R' over UART.
# Each press sends the counter value as two bytes (big-endian).
#
from whisnake.types import uint8, uint16
from whisnake.hal.gpio import Pin
from whisnake.hal.uart import UART
from pymcu.time import delay_ms


def main():
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    count: uint16 = 0
    prev:  uint8  = 1    # last raw button state (1 = released)

    while True:
        cur: uint8 = btn.value()

        # Detect falling edge (button press)
        if cur == 0 and prev == 1:
            count += 1
            led.toggle()

            # Send count as big-endian uint16
            uart.write((count >> 8) & 0xFF)
            uart.write(count & 0xFF)

            # Roll over at 1000 — exercises 16-bit JumpIfEqual with immediate
            if count == 1000:
                count = 0
                uart.write('R')   # 'R' reset

        prev = cur
        delay_ms(10)    # 10 ms debounce window
