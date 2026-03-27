# ATmega328P: Software PWM via Timer0 overflow ISR + duty-cycle counter
#
# Timer0 OVF ISR (~4.1ms at 16MHz/1024) sets GPIOR0[0] flag.
# Main loop maintains a 0-99 counter; while counter < duty the LED is on.
# Duty cycle bounces: 0 -> 25 -> 50 -> 75 -> 100 -> 75 -> 50 -> 25 -> 0.
# Each step lasts 100 timer ticks (~0.41 s).
#
# Hardware: Arduino Uno
#   LED: PB5 (Arduino pin 13, built-in)
#   UART TX on PD1 at 9600 baud
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer


def timer0_ovf_isr():
    GPIOR0[0] = 1


# Returns the duty cycle percentage (0-100) for a given bounce phase (0-7).
# phase: 0=0%, 1=25%, 2=50%, 3=75%, 4=100%, 5=75%, 6=50%, 7=25%
def duty_value(phase: uint8) -> uint8:
    match phase:
        case 0: return 0
        case 1: return 25
        case 2: return 50
        case 3: return 75
        case 4: return 100
        case 5: return 75
        case 6: return 50
        case _: return 25


def main():
    led   = Pin("PB5", Pin.OUT)
    uart  = UART(9600)
    timer = Timer(0, 1024)
    timer.irq(timer0_ovf_isr)

    GPIOR0[0] = 0
    uart.println("SOFT PWM")

    counter:    uint8 = 0
    duty:       uint8 = 0
    step_count: uint8 = 0
    phase:      uint8 = 0

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0

            counter = counter + 1
            if counter >= 100:
                counter = 0

            if counter < duty:
                led.high()
            else:
                led.low()

            step_count = step_count + 1
            if step_count >= 100:
                step_count = 0
                phase = phase + 1
                if phase >= 8:
                    phase = 0
                duty = duty_value(phase)
                uart.write(duty)
                uart.write('\n')
