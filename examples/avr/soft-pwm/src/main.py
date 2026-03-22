# ATmega328P: Software PWM via Timer0 overflow + duty-cycle counter
#
# Timer0 OVF ISR (~4.1ms at 16MHz/1024) sets GPIOR0[0] flag.
# Main loop maintains a 0-99 counter; while counter < duty the LED is on.
# Duty cycle bounces: 0 -> 25 -> 50 -> 75 -> 100 -> 75 -> 50 -> 25 -> 0.
# Each step lasts 100 timer ticks (~0.41 s).
#
# Tests:
#   - Timer0 OVF ISR as a flag source (GPIOR0[0])
#   - uint8 counter with >= wrap-around: counter >= 100 -> counter = 0
#   - uint8 >= and < comparisons in tight loop
#   - Direct PORTB bit write from main loop: PORTB[5] = 0 or 1
#   - Multi-line case bodies in match/case (multi-statement is not tested,
#     but each case body returns from a helper function)
#   - Bouncing duty cycle via helper functions with multiple return paths
#   - UART sends duty value (0/25/50/75/100) + '\n' on each step change
#
# Hardware: Arduino Uno
#   LED: PB5 (Arduino pin 13, built-in)
#   UART TX on PD1 at 9600 baud
#
from whisnake.types import uint8, interrupt
from whisnake.chips.atmega328p import PORTB, DDRB, GPIOR0
from whisnake.chips.atmega328p import TCCR0B, TIMSK0
from whisnake.hal.uart import UART
from whisnake.types import asm


@interrupt(0x0020)  # TIMER0_OVF word addr 0x10
def timer0_ovf_isr():
    GPIOR0[0] = 1


# Returns the duty cycle percentage (0-100) for a given bounce phase (0-7).
# phase: 0=0%, 1=25%, 2=50%, 3=75%, 4=100%, 5=75%, 6=50%, 7=25%
def duty_value(phase: uint8) -> uint8:
    if phase == 0:
        return 0
    elif phase == 1:
        return 25
    elif phase == 2:
        return 50
    elif phase == 3:
        return 75
    elif phase == 4:
        return 100
    elif phase == 5:
        return 75
    elif phase == 6:
        return 50
    else:
        return 25


def main():
    DDRB[5] = 1

    # Timer0: normal mode, prescaler 1024 (CS02=1, CS00=1 = 0b101)
    TCCR0B.value = 5
    TIMSK0[0] = 1
    GPIOR0[0] = 0

    uart = UART(9600)
    uart.println("SOFT PWM")

    asm("SEI")

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
                PORTB[5] = 1
            else:
                PORTB[5] = 0

            step_count = step_count + 1
            if step_count >= 100:
                step_count = 0
                phase = phase + 1
                if phase >= 8:
                    phase = 0
                duty = duty_value(phase)
                uart.write(duty)
                uart.write('\n')
