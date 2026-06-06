# MicroPython machine.PWM integration test fixture
#
# Verifies that machine.PWM configures Timer0 Fast PWM on PD6 (OC0A)
# and writes the correct duty cycle to OCR0A.
#
# Expected hardware state after setup:
#   TCCR0A: WGM01|WGM00 = Fast PWM; COM0A1 = non-inverted output
#   OCR0A:  128  (duty() called with 128 = 50%)
#
# After PWM setup, sends 0x44 ('D') via machine.UART to signal completion.
#
from machine import Pin, PWM, UART


def main():
    uart = UART(0, 9600)
    pwm = PWM(Pin("PD6"))   # PD6 = OC0A
    pwm.init()        # start Fast PWM output on OC0A
    pwm.duty(128)     # set OCR0A = 128 (50% duty cycle)
    uart.write(0x44)  # 'D' done marker
    while True:
        pass
