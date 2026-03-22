# ATmega328P: Three-ISR stopwatch
#
# THREE simultaneous ISRs:
#   ISR 1: INT0  (PD2, byte 0x0002) - Start/Stop toggle (falling edge)
#   ISR 2: INT1  (PD3, byte 0x0004) - Reset (falling edge)
#   ISR 3: TIMER0_OVF (byte 0x0020) - Tick counter (~16.384ms per tick)
#
# 61 ticks ~= 1 second. Main loop tracks elapsed seconds and sends them
# over UART as a raw uint8 byte + '\n' whenever seconds increments.
#
# State machine via GPIOR0 bit flags:
#   GPIOR0[0] = Timer0 OVF flag
#   GPIOR0[1] = INT0 Start/Stop flag
#   GPIOR0[2] = INT1 Reset flag
#
# Tests:
#   - Three @interrupt decorators in the same program
#   - INT0 + INT1 + TIMER0_OVF coexistence
#   - GPIOR0 with 3 separate bit flags ([0], [1], [2])
#   - uint16 seconds elapsed time
#   - uint8 running flag (start/stop toggle)
#   - Complex main-loop state machine
#
# Hardware: Arduino Uno
#   Start/Stop button: PD2 (Arduino pin 2), active low
#   Reset button:      PD3 (Arduino pin 3), active low
#   LED:               PB5 (Arduino pin 13, built-in) - on while running
#   UART TX on PD1 at 9600 baud
#
# UART output:
#   Boot: "STOPWATCH\n"
#   On each second increment while running: raw byte = seconds mod 256, '\n'
#   On reset: sends 0, '\n'
#
from whipsnake.types import uint8, uint16, interrupt
from whipsnake.chips.atmega328p import PORTB, DDRB, GPIOR0
from whipsnake.chips.atmega328p import TCCR0B, TIMSK0
from whipsnake.chips.atmega328p import EICRA, EIMSK
from whipsnake.hal.uart import UART
from whipsnake.types import asm


@interrupt(0x0020)  # TIMER0_OVF word addr 0x10
def timer0_ovf_isr():
    GPIOR0[0] = 1


@interrupt(0x0002)  # INT0 word addr 0x01
def int0_isr():
    GPIOR0[1] = 1


@interrupt(0x0004)  # INT1 word addr 0x02
def int1_isr():
    GPIOR0[2] = 1


def main():
    DDRB[5] = 1

    # Timer0: normal mode, prescaler 1024
    TCCR0B.value = 5
    TIMSK0[0] = 1

    # INT0: falling edge (ISC01=1, ISC00=0 for INT0, bits 1:0 = 0b10)
    # INT1: falling edge (ISC11=1, ISC10=0 for INT1, bits 3:2 = 0b10)
    # EICRA bits: [ISC11|ISC10|ISC01|ISC00] = 0b00101000 | 0b00000010 = 0b00101010 = 0x0A
    EICRA.value = 10
    # Enable INT0 (bit 0) and INT1 (bit 1) in EIMSK
    EIMSK.value = 3

    # Buttons start high (pull-up via external resistor / internal)
    GPIOR0[0] = 0
    GPIOR0[1] = 0
    GPIOR0[2] = 0

    uart = UART(9600)
    uart.println("STOPWATCH")

    asm("SEI")

    ticks:   uint8  = 0
    seconds: uint16 = 0
    running: uint8  = 0

    while True:
        # Handle INT0: toggle start/stop
        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            if running == 0:
                running = 1
                PORTB[5] = 1   # LED on while running
            else:
                running = 0
                PORTB[5] = 0

        # Handle INT1: reset
        if GPIOR0[2] == 1:
            GPIOR0[2] = 0
            ticks   = 0
            seconds = 0
            running = 0
            PORTB[5] = 0
            uart.write(0)
            uart.write('\n')

        # Handle Timer0 tick while running
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            if running == 1:
                ticks += 1
                if ticks >= 61:
                    ticks = 0
                    seconds += 1
                    uart.write(seconds & 0xFF)
                    uart.write('\n')
