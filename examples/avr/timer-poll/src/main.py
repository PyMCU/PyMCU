# ATmega328P: Timer0 overflow polling — software tick counter
# Tests: Timer0 HAL, direct register BitWrite (TIFR0 clear-by-write-one),
#        uint16 tick counter, 16-bit JumpIfEqual (== 244), BitCheck on TIFR0[0]
#
# Timer0 at prescaler=256, 16MHz:
#   overflow every 256 * 256 / 16_000_000 = 4.096 ms
#   244 overflows * 4.096 ms ≈ 1 second
#
# Hardware: Arduino Uno
#   LED on PB5 (built-in): toggles every ~1 second
#   Serial terminal at 9600 baud: sends 'T' on each toggle
#
from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer
from pymcu.chips.atmega328p import TIFR0


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    # Timer(0, prescaler=256) — Timer0, 8-bit, 256 prescaler
    timer = Timer(0, 256)
    timer.clear()

    ticks: uint16 = 0

    while True:
        # Poll Timer0 Overflow Flag (TOV0 = bit 0 of TIFR0, address 0x35)
        if TIFR0[0] == 1:
            # Clear flag by writing logic 1 (AVR convention for timer flag bits)
            TIFR0[0] = 1

            ticks = ticks + 1

            # ~1 second = 244 overflows at 4.096 ms each
            if ticks == 244:
                ticks = 0
                led.toggle()
                uart.write(84)    # 'T'
                uart.write(10)    # '\n'
