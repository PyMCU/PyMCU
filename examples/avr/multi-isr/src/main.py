# ATmega328P: Two simultaneous ISRs
#
# ISR 1: TIMER0_OVF (byte vector 0x0020)
#   Timer0 overflow ~4ms at 16MHz/1024. Sets GPIOR0[0] flag.
#   Main loop counts 250 overflows (~1s), toggles PB5 LED, sends 'T'\n.
#
# ISR 2: INT0 (byte vector 0x0002, falling edge on PD2)
#   Sets GPIOR0[1] flag. Main loop sends incrementing count byte + '\n'.
#
# Tests:
#   - Two @interrupt decorators in same program (Timer0_OVF + INT0)
#   - GPIOR0[0] and GPIOR0[1] as separate ISR flags
#   - uint16 tick counter updated from ISR flag
#   - Timer0 + INT0 coexistence, SEI via asm("SEI")
#
# Hardware: Arduino Uno
#   LED:    PB5 (Arduino pin 13)
#   Button: PD2 (Arduino pin 2), active low
#   UART TX on PD1 at 9600 baud
#
# ATmega328P vector table (byte addresses):
#   INT0 = 0x0002, TIMER0_OVF = 0x0020
#
from pymcu.types import uint8, uint16, interrupt
from pymcu.chips.atmega328p import PORTB, DDRB
from pymcu.chips.atmega328p import TCCR0B, TIMSK0
from pymcu.chips.atmega328p import EICRA, EIMSK
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.uart import UART
from pymcu.types import asm


@interrupt(0x0020)
def timer0_ovf_isr():
    GPIOR0[0] = 1


@interrupt(0x0002)
def int0_isr():
    GPIOR0[1] = 1


def main():
    # LED output
    DDRB[5] = 1

    # Timer0: normal mode, prescaler 1024 (CS02=1, CS00=1 = 5)
    TCCR0B.value = 5
    # Enable Timer0 overflow interrupt (TOIE0 = bit 0)
    TIMSK0[0] = 1

    # INT0: falling edge (ISC01=1, ISC00=0 = 2)
    EICRA.value = 2
    # Enable INT0 (bit 0 of EIMSK)
    EIMSK[0] = 1

    # Clear flags before enabling interrupts
    GPIOR0[0] = 0
    GPIOR0[1] = 0

    uart = UART(9600)
    uart.println("MULTI ISR")

    # Enable global interrupts
    asm("SEI")

    # Timer0 at 1024 prescaler, 16MHz: 16e6/1024 = 15625 overflows/s
    # 250 overflows ~ 16ms -> not quite. Actually:
    # overflow every 256 * 1024 / 16e6 = 16.384ms. 61 overflows ~ 1s.
    tick:      uint16 = 0
    int_count: uint8  = 0

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            tick = tick + 1
            if tick == 61:
                tick = 0
                PORTB[5] = PORTB[5] ^ 1   # toggle LED
                uart.write('T')
                uart.write('\n')

        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            int_count = int_count + 1
            uart.write(int_count)
            uart.write('\n')
