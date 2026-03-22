# ATmega328P: External interrupt counter — INT0 ISR + UART output
# Tests: @interrupt decorator, EICRA/EIMSK config, GPIOR0 atomic flag,
#        asm("SEI") to enable global interrupts, led.toggle() in main loop
#
# Hardware: Arduino Uno
#   - Button on PD2 (Arduino pin 2 = INT0), active low, with pull-up
#   - LED on PB5 (Arduino pin 13, built-in)
#   - Serial terminal at 9600 baud — receives count byte on each press
#
# INT0 vector = 0x0002 (ATmega328P vector table, byte address)
# ISR uses GPIOR0 (general-purpose I/O register at 0x3E) as a shared flag:
#   GPIOR0[0] = 1  →  SBI 0x1E, 0  (single-cycle atomic bit-set, no registers used)
#   GPIOR0[0] = 0  →  CBI 0x1E, 0  (atomic bit-clear in main)
# This avoids register corruption since SBI/CBI touch no CPU registers.
#
from whipsnake.types import uint8, interrupt
from whipsnake.chips.atmega328p import EICRA, EIMSK, GPIOR0
from whipsnake.hal.gpio import Pin
from whipsnake.hal.uart import UART
from whipsnake.types import asm


@interrupt(0x0002)
def int0_isr():
    # Minimal ISR: set event flag with SBI (atomic, no registers corrupted)
    GPIOR0[0] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    # Configure INT0: falling-edge trigger (ISC01=1, ISC00=0)
    EICRA.value = 0x02   # ISC01=1, ISC00=0  (bits 1:0 of EICRA)
    EIMSK.value = 0x01   # INT0EN=1 (bit 0 of EIMSK)
    GPIOR0[0] = 0         # Clear flag before enabling interrupts

    asm("SEI")            # Enable global interrupts (I-flag in SREG)

    count: uint8 = 0
    uart.println("INT COUNTER")

    while True:
        if GPIOR0[0] == 1:           # Check event flag (SBIS 0x1E, 0)
            GPIOR0[0] = 0            # Clear flag (CBI 0x1E, 0)
            count += 1
            led.toggle()
            uart.write(count)        # Send raw count byte over UART
