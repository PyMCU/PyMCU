# ATmega328P: Sensor Dashboard
#
# Samples ADC0 (PC0 / A0) every ~256ms via Timer0_OVF (prescaler 256, 64 ticks).
# Tracks lifetime min/max and a simple EMA. Blinks PB5 LED on each sample.
# INT0 (PD2, falling edge) toggles verbose <-> compact display mode.
#
# Tests:
#   - Timer0_OVF ISR (GPIOR0[0]) driving periodic ADC sampling (~64-tick cadence)
#   - INT0 ISR (GPIOR0[1]) toggling display mode via flag
#   - ADC0 polling (ADCSRA[6] start/wait/read)
#   - Lifetime minimum (lo) and maximum (hi) tracking
#   - nibble_to_hex helper called from main (3-level call depth)
#   - Two UART output formats selected at runtime
#
# Hardware: Arduino Uno
#   ADC input: PC0 (A0)
#   LED:       PB5 (Arduino pin 13)
#   Button:    PD2 (Arduino pin 2), active-low (INT0 falling edge)
#   UART TX on PD1 at 9600 baud
#
# Verbose:  "R:HH A:HH L:HH H:HH\n"  (HH = 2 hex digits)
# Compact:  "HH\n"
#
# Timer0 at prescaler 256, 16 MHz:
#   overflow every 256 * 256 / 16e6 = 4.096 ms
#   64 overflows => ADC sample every ~262 ms
#
from pymcu.types import uint8, interrupt
from pymcu.chips.atmega328p import PORTB, DDRB
from pymcu.chips.atmega328p import TCCR0B, TIMSK0
from pymcu.chips.atmega328p import EICRA, EIMSK
from pymcu.chips.atmega328p import ADCSRA, ADMUX, ADCL
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.uart import UART
from pymcu.types import asm


@interrupt(0x0020)
def timer0_ovf_isr():
    GPIOR0[0] = 1


@interrupt(0x0002)
def int0_isr():
    GPIOR0[1] = 1


# Convert a 4-bit nibble (0-15) to its ASCII hex character.
def nibble_to_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48        # '0'-'9' = 48-57
    else:
        return n + 55        # 'A'-'F' = 65-70


def main():
    # LED output on PB5
    DDRB[5] = 1

    # Timer0: normal mode, prescaler 256 (CS02=1, CS01=0, CS00=0 -> value 4)
    TCCR0B.value = 4
    # Enable Timer0 overflow interrupt (TOIE0 = bit 0)
    TIMSK0[0] = 1

    # INT0: falling edge (ISC01=1, ISC00=0 -> EICRA = 2)
    EICRA.value = 2
    # Enable INT0 (bit 0 of EIMSK)
    EIMSK[0] = 1

    # ADC: AVCC reference, channel ADC0 (ADMUX = 0x40 = 64)
    ADMUX.value = 64
    # ADC enable + prescaler /128 (ADCSRA = 0x87 = 135: ADEN|ADPS2|ADPS1|ADPS0)
    ADCSRA.value = 135

    # Clear ISR flags before enabling interrupts
    GPIOR0[0] = 0
    GPIOR0[1] = 0

    uart = UART(9600)
    uart.println("SENSOR DASHBOARD")

    # Enable global interrupts
    asm("SEI")

    raw:     uint8 = 0
    avg:     uint8 = 0
    lo:      uint8 = 255   # lifetime minimum (starts at max)
    hi:      uint8 = 0     # lifetime maximum (starts at min)
    verbose: uint8 = 1     # 1 = verbose, 0 = compact
    tick:    uint8 = 0     # Timer0 overflow counter

    while True:
        # Timer0 OVF handler: count ticks, sample ADC every 64
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            tick = tick + 1
            if tick == 64:
                tick = 0

                # Sample ADC0: start conversion, wait, read low byte
                ADCSRA[6] = 1              # ADSC: start conversion
                while ADCSRA[6] == 1:
                    pass
                raw = ADCL[0]              # low byte of 10-bit ADC result

                # Update lifetime min and max
                if raw < lo:
                    lo = raw
                if raw > hi:
                    hi = raw

                # Exponential moving average (EMA): avg = (avg + raw) >> 1
                avg = (avg + raw) >> 1

                # Blink LED on each sample
                PORTB[5] = PORTB[5] ^ 1

                # Output frame in selected format
                if verbose == 1:
                    uart.write_str("R:")
                    uart.write(nibble_to_hex((raw >> 4) & 0x0F))
                    uart.write(nibble_to_hex(raw & 0x0F))
                    uart.write_str(" A:")
                    uart.write(nibble_to_hex((avg >> 4) & 0x0F))
                    uart.write(nibble_to_hex(avg & 0x0F))
                    uart.write_str(" L:")
                    uart.write(nibble_to_hex((lo >> 4) & 0x0F))
                    uart.write(nibble_to_hex(lo & 0x0F))
                    uart.write_str(" H:")
                    uart.write(nibble_to_hex((hi >> 4) & 0x0F))
                    uart.write(nibble_to_hex(hi & 0x0F))
                    uart.write('\n')
                else:
                    uart.write(nibble_to_hex((avg >> 4) & 0x0F))
                    uart.write(nibble_to_hex(avg & 0x0F))
                    uart.write('\n')

        # INT0 handler: toggle verbose/compact mode
        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            if verbose == 1:
                verbose = 0
                uart.write_str("MODE:COMPACT\n")
            else:
                verbose = 1
                uart.write_str("MODE:VERBOSE\n")
