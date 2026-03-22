# ATmega328P: Traffic-light finite state machine
# Tests: match/case as FSM dispatcher, uint8 state variables, uint16 tick counter,
#        Timer0 overflow polling for timing, UART state-change announcements
#
# Hardware: Arduino Uno
#   Red    LED on PB0 (with resistor)
#   Yellow LED on PB1 (with resistor)
#   Green  LED on PB2 (with resistor)
#   Serial terminal at 9600 baud — prints state name on each transition
#
# Timer0 at prescaler 256, F_CPU = 16 MHz:
#   1 overflow = 256 * 256 / 16_000_000 ≈ 4.096 ms
#   244 overflows ≈ 1 second
#
# State sequence (UK-style):
#   RED (3 s) → RED+YEL (1 s) → GREEN (3 s) → YELLOW (1 s) → RED …
#
from whipsnake.types import uint8, uint16
from whipsnake.hal.gpio import Pin
from whipsnake.hal.uart import UART
from whipsnake.chips.atmega328p import TIFR0, TCCR0B


# FSM state identifiers — use as dotted names in match: case State.RED
class State:
    RED        = 0
    RED_YELLOW = 1
    GREEN      = 2
    YELLOW     = 3


# Timing: overflows at ~4.096 ms each
DUR_RED    = 732   # 3 seconds  (244 * 3)
DUR_RY     = 244   # 1 second
DUR_GREEN  = 732   # 3 seconds
DUR_YELLOW = 244   # 1 second


def main():
    red    = Pin("PB0", Pin.OUT)
    yellow = Pin("PB1", Pin.OUT)
    green  = Pin("PB2", Pin.OUT)
    uart   = UART(9600)

    # Timer0 prescaler 256: CS0[2:0] = 100 → TCCR0B bit 2 only
    TCCR0B[2] = 1    # CS02=1 → prescaler 256

    state: uint8  = State.RED
    ticks: uint16 = 0
    dur:   uint16 = DUR_RED

    # Initial output: RED on
    red.high()
    yellow.low()
    green.low()
    uart.println("RED")

    while True:
        # Poll Timer0 overflow flag (bit 0 of TIFR0)
        if TIFR0[0] == 1:
            TIFR0[0] = 1      # Clear TOV0 by writing 1 (AVR convention)
            ticks = ticks + 1

            if ticks >= dur:
                ticks = 0

                # Advance state and update outputs
                match state:
                    case State.RED:
                        state  = State.RED_YELLOW
                        dur    = DUR_RY
                        red.high()
                        yellow.high()
                        green.low()
                        uart.println("RED+YEL")

                    case State.RED_YELLOW:
                        state  = State.GREEN
                        dur    = DUR_GREEN
                        red.low()
                        yellow.low()
                        green.high()
                        uart.println("GREEN")

                    case State.GREEN:
                        state  = State.YELLOW
                        dur    = DUR_YELLOW
                        red.low()
                        yellow.high()
                        green.low()
                        uart.println("YELLOW")

                    case _:
                        state  = State.RED
                        dur    = DUR_RED
                        red.high()
                        yellow.low()
                        green.low()
                        uart.println("RED")
