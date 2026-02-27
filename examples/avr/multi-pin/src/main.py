# ATmega328P: Multi-pin GPIO — 6 LEDs + 2 buttons
# Tests: many Pin instances, pin.high()/low()/toggle(), multiple input reads,
#        match/case for LED pattern dispatch, uint8 step counter, button control
#
# Hardware: Arduino Uno
#   LEDs (with resistors) on PB0-PB5
#   Button A on PD2 (active low, pull-up) — advances pattern step
#   Button B on PD3 (active low, pull-up) — resets pattern to step 0
#   Serial terminal at 9600 baud shows current step
#
from pymcu.types import uint8
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def main():
    led0 = Pin("PB0", Pin.OUT)
    led1 = Pin("PB1", Pin.OUT)
    led2 = Pin("PB2", Pin.OUT)
    led3 = Pin("PB3", Pin.OUT)
    led4 = Pin("PB4", Pin.OUT)
    led5 = Pin("PB5", Pin.OUT)

    btn_a = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    btn_b = Pin("PD3", Pin.IN, pull=Pin.PULL_UP)

    uart = UART(9600)

    step: uint8 = 0
    prev_a: uint8 = 1
    prev_b: uint8 = 1

    while True:
        cur_a: uint8 = btn_a.value()
        cur_b: uint8 = btn_b.value()

        # Button A: advance step on falling edge
        if cur_a == 0 and prev_a == 1:
            step = step + 1
            if step == 6:
                step = 0

        # Button B: reset to step 0 on falling edge
        if cur_b == 0 and prev_b == 1:
            step = 0

        prev_a = cur_a
        prev_b = cur_b

        # All LEDs off, then light exactly one based on current step
        led0.low()
        led1.low()
        led2.low()
        led3.low()
        led4.low()
        led5.low()

        match step:
            case 0:
                led0.high()
            case 1:
                led1.high()
            case 2:
                led2.high()
            case 3:
                led3.high()
            case 4:
                led4.high()
            case 5:
                led5.high()

        uart.write(step)
        delay_ms(20)
