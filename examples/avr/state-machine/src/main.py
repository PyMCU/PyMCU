# ATmega328P: Traffic-light finite state machine
# Demonstrates: TrafficLight ZCA with @property state setter, Timer HAL, match/case FSM
#
# Hardware: Arduino Uno
#   Red    LED on PB0 (with resistor)
#   Yellow LED on PB1 (with resistor)
#   Green  LED on PB2 (with resistor)
#   Serial terminal at 9600 baud -- prints state name on each transition
#
# Timer0 at prescaler 256, F_CPU = 16 MHz:
#   1 overflow = 256 * 256 / 16_000_000 = 4.096 ms
#   244 overflows = 1 second
#
# State sequence (UK-style):
#   RED (3 s) -> RED+YEL (1 s) -> GREEN (3 s) -> YELLOW (1 s) -> RED ...
#
# Design: TrafficLight encapsulates the hardware pins; FSM state lives in main().
# light.state = X drives the LEDs via the property setter (pin control); the
# FSM dispatch uses the local state variable (main owns the logical state).
#
from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer
from pymcu.chips.atmega328p import TIFR0


class State:
    RED        = 0
    RED_YELLOW = 1
    GREEN      = 2
    YELLOW     = 3


DUR_RED    = 732   # 3 seconds  (244 * 3)
DUR_RY     = 244   # 1 second
DUR_GREEN  = 732   # 3 seconds
DUR_YELLOW = 244   # 1 second


class TrafficLight:
    """Zero-cost pin abstraction for a 3-LED traffic light.

    Assigning light.state drives the correct LEDs via the property setter.
    The FSM state is owned by the caller -- TrafficLight only controls pins.
    """

    def __init__(self, red_pin: str, yellow_pin: str, green_pin: str):
        self._red    = Pin(red_pin,    Pin.OUT)
        self._yellow = Pin(yellow_pin, Pin.OUT)
        self._green  = Pin(green_pin,  Pin.OUT)

    @property
    def state(self) -> uint8:
        return 0

    @state.setter
    def state(self, s: uint8):
        match s:
            case State.RED:
                self._red.high()
                self._yellow.low()
                self._green.low()
            case State.RED_YELLOW:
                self._red.high()
                self._yellow.high()
                self._green.low()
            case State.GREEN:
                self._red.low()
                self._yellow.low()
                self._green.high()
            case _:
                self._red.low()
                self._yellow.high()
                self._green.low()


def main():
    light = TrafficLight("PB0", "PB1", "PB2")
    uart  = UART(9600)
    timer = Timer(0, 256)

    state: uint8  = State.RED
    ticks: uint16 = 0
    dur:   uint16 = DUR_RED

    light.state = State.RED
    uart.println("RED")

    while True:
        if timer.overflow():
            TIFR0[0] = 1
            ticks = ticks + 1

            if ticks >= dur:
                ticks = 0

                match state:
                    case State.RED:
                        state = State.RED_YELLOW
                        dur   = DUR_RY
                        light.state = State.RED_YELLOW
                        uart.println("RED+YEL")

                    case State.RED_YELLOW:
                        state = State.GREEN
                        dur   = DUR_GREEN
                        light.state = State.GREEN
                        uart.println("GREEN")

                    case State.GREEN:
                        state = State.YELLOW
                        dur   = DUR_YELLOW
                        light.state = State.YELLOW
                        uart.println("YELLOW")

                    case _:
                        state = State.RED
                        dur   = DUR_RED
                        light.state = State.RED
                        uart.println("RED")
