# ATmega328P: Multi-channel PWM
#
# Demonstrates simultaneous PWM on three independent channels:
#   PD6 (OC0A, Timer0) - ramp 0->255
#   PB3 (OC2A, Timer2) - ramp 128->255->0 (phase offset)
#   PB1 (OC1A, Timer1) - ramp 64->255->0->63 (phase offset)
#
# All channels use Fast PWM mode. Duty cycle is updated together in the main loop.
# A UART banner confirms init, then "D\n" is sent each full cycle completion.
#
# Hardware: Arduino Uno
#   PD6 = Arduino pin 6 (OC0A)
#   PB3 = Arduino pin 11 (OC2A)
#   PB1 = Arduino pin 9  (OC1A)
#   UART TX on PD1 at 9600 baud
#
from pymcu.types import uint8
from pymcu.hal.pwm import PWM
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def main():
    uart = UART(9600)

    # Channel A: Timer0 OC0A on PD6
    ch_a = PWM("PD6", 0)
    ch_a.start()

    # Channel B: Timer2 OC2A on PB3 (phase offset = 128)
    ch_b = PWM("PB3", 128)
    ch_b.start()

    # Channel C: Timer1 OC1A on PB1 (phase offset = 64)
    ch_c = PWM("PB1", 64)
    ch_c.start()

    uart.println("PWM3")

    duty_a: uint8 = 0
    duty_b: uint8 = 128
    duty_c: uint8 = 64
    cycle:  uint8 = 0

    while True:
        ch_a.set_duty(duty_a)
        ch_b.set_duty(duty_b)
        ch_c.set_duty(duty_c)

        delay_ms(5)

        duty_a += 1
        duty_b += 1
        duty_c += 1

        cycle += 1
        if cycle == 0:
            uart.write('D')
            uart.write('\n')
