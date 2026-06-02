# servo -- RC servo sweep example
#
# Connects a standard RC servo (signal wire) to Arduino D9 (OC1A = PB1).
# The servo sweeps from 0 to 180 degrees and back continuously.
#
# Wiring:
#   Brown  / GND  -> Arduino GND
#   Red    / VCC  -> Arduino 5V (or external 5V for high-torque servos)
#   Orange / SIG  -> Arduino D9
#
# Uses Timer1 in Fast PWM mode 14 (50 Hz, 1--2 ms pulses).
# OC1A and OC1B (D9 / D10) are both activated; a second Servo("PB2") can
# run simultaneously on D10 without extra configuration.
from pymcu.types import uint8, uint16
from pymcu.hal.servo import Servo
from pymcu.time import delay_ms


def main():
    s = Servo("PB1")   # OC1A = PB1 = Arduino D9

    while True:
        pos: uint8 = 0
        while pos <= 180:
            s.write(pos)
            delay_ms(15)
            pos = pos + 1

        while pos > 0:
            pos = pos - 1
            s.write(pos)
            delay_ms(15)
