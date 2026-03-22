# ATmega328P: Timer1 CTC (Clear Timer on Compare) mode -- ~1 Hz LED blink
#
# Timer1 in CTC mode with prescaler 256, compare value 62499:
#   period = (62499 + 1) * 256 / 16_000_000 = 1.0 s exactly
#
# TCCR1B WGM12=1 (CTC), CS1[2:0]=100 (prescaler 256)
# TIMSK1 bit 1 = OCIE1A (Timer1 Compare Match A interrupt enable)
# TIMER1_COMPA vector: byte 0x0016, word 0x000B
#
# Uses Timer.set_compare() which sets WGM12 + OCR1A + OCIE1A.
#
# Hardware: Arduino Uno
#   - LED on PB5 (built-in, pin 13)
#   - UART TX 9600 baud: sends "CTC\n" on boot, "C\n" on each 1 Hz tick
#
from whisnake.types import uint8, interrupt, asm
from whisnake.chips.atmega328p import GPIOR0
from whisnake.hal.gpio import Pin
from whisnake.hal.uart import UART
from whisnake.hal.timer import Timer


@interrupt(0x0016)
def timer1_compa_isr():
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    # Timer1: prescaler 256 (CS1[2:0]=100 -> TCCR1B bits 0-2 = 0x04)
    # set_compare sets WGM12=1 and OCR1A=62499 then enables OCIE1A.
    t = Timer(1, 256)
    t.set_compare(62499)

    GPIOR0[0] = 0
    asm("SEI")

    uart.println("CTC")

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            led.toggle()
            uart.write('C')
            uart.write('\n')
