# ATmega328P: Timer2 overflow interrupt -- ~1 Hz LED blink + UART counter
#
# Tests: @interrupt on TIMER2_OVF vector (0x0012 / word 0x0009),
#        TCCR2B / TIMSK2 setup, GPIOR0 atomic flag, SEI
#
# Hardware: Arduino Uno
#   - LED on PB5 (built-in Arduino LED, pin 13)
#   - Serial terminal at 9600 baud: prints "T2\n" on each ~1 s toggle
#
# Timer2 (8-bit) at prescaler 1024, F_CPU = 16 MHz:
#   overflow period = 256 * 1024 / 16_000_000 = 16.384 ms
#   61 overflows = 61 * 16.384 ms = 999.4 ms ~= 1 s
#
# TCCR2B CS2[2:0] = 0b111 -> prescaler 1024 -> TCCR2B = 0x07
# TIMSK2 bit 0 = TOIE2 (Timer2 Overflow Interrupt Enable)
# TIMER2_OVF vector: byte 0x0012, word 0x0009
#
from whipsnake.types import uint8, interrupt, asm
from whipsnake.chips.atmega328p import TCCR2B, TIMSK2, GPIOR0
from whipsnake.hal.gpio import Pin
from whipsnake.hal.uart import UART


@interrupt(0x0012)
def timer2_ovf_isr():
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    # Timer2: prescaler 1024 -- CS2[2:0] = 111 -> TCCR2B = 0x07
    TCCR2B.value = 0x07
    # Enable Timer2 overflow interrupt (TOIE2 = bit 0 of TIMSK2)
    TIMSK2.value = 0x01

    GPIOR0[0] = 0
    asm("SEI")

    uart.println("TIMER2 IRQ BLINK")

    ovf_count: uint8 = 0

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            ovf_count += 1
            if ovf_count == 61:
                ovf_count = 0
                led.toggle()
                uart.write('T')
                uart.write('2')
                uart.write('\n')
