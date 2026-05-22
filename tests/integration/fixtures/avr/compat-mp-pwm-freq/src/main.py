# MicroPython machine.PWM integration test fixture — freq parameter
#
# Verifies that:
#   1. machine.PWM(pin, freq=1000) selects prescaler/8 (CS=0x02) for Timer0
#   2. pwm.freq(100) re-selects prescaler/256 (CS=0x04) for Timer0
#
# ATmega328P Timer0 fast-PWM freq = 16MHz / (prescaler * 256):
#   CS=0x01: /1   = 62500 Hz
#   CS=0x02: /8   =  7812 Hz
#   CS=0x03: /64  =   976 Hz
#   CS=0x04: /256 =   244 Hz
#   CS=0x05: /1024=    61 Hz
#
# After setup sends 0x46 ('F') via machine.UART to signal completion.
#
from machine import PWM, UART


def main():
    uart = UART(0, 9600)
    pwm = PWM("PD6", freq=1000)   # freq > 976 -> CS=0x02 (prescaler /8)
    pwm.init()
    pwm.freq(100)                  # freq > 61, <= 244 -> CS=0x04 (prescaler /256)
    uart.write(0x46)               # 'F' done marker
    while True:
        pass
