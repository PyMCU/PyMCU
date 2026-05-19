from pymcu.chips.attiny85 import DDRB, OCR0A, OCR0B, TCCR0A, TCCR0B, TCCR1
from pymcu.types import uint8, uint16, inline, ptr

# ATtiny85/45/25 PWM HAL
#
# Available PWM output pins:
#   PB0 -- Timer0 OC0A (Fast PWM, COM0A1=1, WGM01:00=11)
#   PB1 -- Timer0 OC0B (Fast PWM, COM0B1=1, WGM01:00=11)
#   PB4 -- Timer1 OC1B (Fast PWM, PWM1B=1 in TCCR1, COM1B1=1)
#
# Note: PB1 can also be Timer1 OC1A, but OC0B and OC1A share the same pin.
#       Use Timer0 (PB1=OC0B) for simple 8-bit PWM; Timer1 OC1A uses PB1 too.
#
# TCCR0A bits:
#   COM0A1:COM0A0 = 10 (non-inverting on OC0A=PB0): TCCR0A |= 0x80
#   COM0B1:COM0B0 = 10 (non-inverting on OC0B=PB1): TCCR0A |= 0x20
#   WGM01:WGM00   = 11 (Fast PWM mode):             TCCR0A |= 0x03
# TCCR0B prescaler 64: 0x03
#
# For Timer1 OC1B on PB4:
#   TCCR1 bits: PWM1B=bit6=1, COM1B1=bit5=1, CS1[3:0]=prescaler
#   OCR1B shares register with OCR0A (data 0x49); use OCR0A or OCR1B (same)

@inline
def pwm_select_ocr(pin: str) -> ptr[uint8]:
    match pin:
        case "PB0":
            return OCR0A
        case "PB1":
            return OCR0B
        case "PB4":
            # OCR1B shares the physical register with OCR0A (data 0x49).
            return OCR0A

@inline
def pwm_select_tccr_b(pin: str) -> ptr[uint8]:
    match pin:
        case "PB0" | "PB1":
            return TCCR0B
        case "PB4":
            return TCCR1

@inline
def pwm_select_start_val(pin: str) -> uint8:
    match pin:
        case "PB0" | "PB1":
            return 0x03   # Timer0 prescaler 64
        case "PB4":
            return 0x07   # Timer1 prescaler 64

@inline
def pwm_init(pin: str, duty: uint8):
    match pin:
        case "PB0":
            # Timer0 OC0A: Fast PWM non-inverting
            # TCCR0A = COM0A1 | WGM01 | WGM00 = 0x83
            DDRB[0] = 1
            OCR0A.value = duty
            TCCR0A.value = 0x83
            TCCR0B.value = 0x03   # prescaler 64
        case "PB1":
            # Timer0 OC0B: Fast PWM non-inverting
            # TCCR0A = COM0B1 | WGM01 | WGM00 = 0x23
            DDRB[1] = 1
            OCR0B.value = duty
            TCCR0A.value = 0x23
            TCCR0B.value = 0x03   # prescaler 64
        case "PB4":
            # Timer1 OC1B: Fast PWM mode via PWM1B bit and COM1B1
            # TCCR1: PWM1B=bit6, COM1B1=bit5, COM1B0=bit4, CS1[3:0]=prescaler
            # COM1B1:COM1B0 = 10 -> 0x20; PWM1B=0x40; CS1=0x07 (prescaler 64)
            DDRB[4] = 1
            OCR0A.value = duty   # OCR1B shares physical register with OCR0A
            TCCR1.value = 0x67   # PWM1B | COM1B1 | prescaler 64 (CS1=0111)
