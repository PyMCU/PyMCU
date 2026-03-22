# ATmega328P: PWM LED fade in/out
# Tests: PWM HAL (set_duty), uint8 counter arithmetic, match/case branching,
#        augmented assignment with Add (+= 1) and Sub (-= 1)
#
# Hardware: Arduino Uno
#   - LED (with resistor) on PD6 (Arduino pin 6, OC0A — Timer0 Fast PWM)
#
from whisnake.types import uint8
from whisnake.hal.pwm import PWM
from whisnake.time import delay_ms


def main():
    # PD6 = OC0A: Timer0 Fast PWM, initial duty = 0
    pwm = PWM("PD6", 0)
    pwm.start()

    duty: uint8 = 0
    going_up: uint8 = 1

    while True:
        pwm.set_duty(duty)
        delay_ms(5)

        match going_up:
            case 1:
                if duty == 255:
                    going_up = 0
                else:
                    duty += 1
            case _:
                if duty == 0:
                    going_up = 1
                else:
                    duty -= 1
