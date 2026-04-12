# ATmega328P: Timer1 CTC (Clear Timer on Compare) mode -- ~1 Hz LED blink
#
# Timer1 in CTC mode with prescaler 256, compare value 62499:
#   period = (62499 + 1) * 256 / 16_000_000 = 1.0 s exactly
#
# Timer.irq(handler, Timer.IRQ_COMPA) registers the handler at the
# TIMER1_COMPA vector and enables OCIE1A + SEI automatically.
# No @interrupt decorator or manual TIMSK/SEI writes needed.
#
# Hardware: Arduino Uno
#   - LED on PB5 (built-in, pin 13)
#   - UART TX 9600 baud: sends "CTC\n" on boot, "C\n" on each 1 Hz tick
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer


def timer1_compa_isr():
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    t = Timer(1, 256)
    t.set_compare(62499)
    t.irq(timer1_compa_isr, Timer.IRQ_COMPA)   # places ISR at TIMER1_COMPA vector

    GPIOR0[0] = 0

    uart.println("CTC")

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            led.toggle()
            uart.write('C')
            uart.write('\n')