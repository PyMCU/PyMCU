# ATmega328P: Timer1 overflow interrupt — ~1 Hz LED blink
# Tests: @interrupt on TIMER1_OVF vector (0x001A), TCCR1B/TIMSK1 setup,
#        GPIOR0 atomic flag pattern, SEI, led.toggle() driven by ISR
#
# Hardware: Arduino Uno
#   - LED on PB5 (Arduino pin 13, built-in) — blinks every ~1 second
#   - Serial terminal at 9600 baud — prints 'T\n' on each toggle
#
# Timer1 (16-bit) at prescaler 256, F_CPU = 16 MHz:
#   overflow period = 65536 * 256 / 16_000_000 ≈ 1.049 s
#
# TCCR1B prescaler bits CS1[2:0] = 0b100 (bit 2 only) → prescaler 256
# TIMSK1 bit 0 = TOIE1 (Timer1 Overflow Interrupt Enable)
# TIMER1_OVF vector = 0x001A (13 * 2 = 26 = 0x1A)
#
from pymcu.types import uint8, interrupt, asm
from pymcu.chips.atmega328p import TCCR1B, TIMSK1, GPIOR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART


@interrupt(0x001A)
def timer1_ovf_isr():
    # One overflow ≈ 1.049 s; set flag for main loop to act on
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    # Timer1: prescaler 256 — CS1[2:0] = 100 → TCCR1B = 0x04
    TCCR1B.value = 0x04
    # Enable Timer1 overflow interrupt
    TIMSK1.value = 0x01

    GPIOR0[0] = 0    # Clear flag
    asm("SEI")       # Enable global interrupts

    uart.println("TIMER1 IRQ BLINK")

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            led.toggle()
            uart.write('T')
            uart.write('\n')
