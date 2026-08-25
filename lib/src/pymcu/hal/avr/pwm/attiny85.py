from pymcu.chips.attiny85 import DDRB, PORTB, OCR0A, OCR0B, TCCR0A, TCCR0B, TCCR1
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
            return 0x67   # Timer1 PWM1B|COM1B1|prescaler 64


# ATtiny85 prescaler lookup (8 MHz internal oscillator assumed).
# Five discrete PWM frequencies per timer:
#   Timer0 (8 MHz / prescaler / 256):
#     /1   ->  31250 Hz   CS=0x01
#     /8   ->   3906 Hz   CS=0x02
#     /64  ->    488 Hz   CS=0x03
#     /256 ->    122 Hz   CS=0x04
#     /1024->     30 Hz   CS=0x05
#   Timer1 (TCCR1 layout, PWM1B|COM1B1 = 0x60, CS1 in bits 3:0):
#     /1   ->  31250 Hz   0x61
#     /2   ->  15625 Hz   0x62
#     /4   ->   7812 Hz   0x63
#     /8   ->   3906 Hz   0x64
#     /16  ->   1953 Hz   0x65
#     /32  ->    976 Hz   0x66
#     /64  ->    488 Hz   0x67 (default)
#     /128 ->    244 Hz   0x68
#     /256 ->    122 Hz   0x69
#     /512 ->     61 Hz   0x6A
#     /1024->     30 Hz   0x6B
@inline
def pwm_prescaler_for_freq(pin: str, freq: uint16) -> uint8:
    match pin:
        case "PB0" | "PB1":
            # Timer0: CS[2:0] in TCCR0B
            if freq > 3906:
                return 0x01
            elif freq > 488:
                return 0x02
            elif freq > 122:
                return 0x03
            elif freq > 30:
                return 0x04
            else:
                return 0x05
        case "PB4":
            # Timer1: TCCR1 = PWM1B(0x40)|COM1B1(0x20)|CS1[3:0]
            if freq > 15625:
                return 0x61
            elif freq > 7812:
                return 0x62
            elif freq > 3906:
                return 0x63
            elif freq > 1953:
                return 0x64
            elif freq > 976:
                return 0x65
            elif freq > 488:
                return 0x66
            elif freq > 244:
                return 0x67
            elif freq > 122:
                return 0x68
            elif freq > 61:
                return 0x69
            elif freq > 30:
                return 0x6A
            else:
                return 0x6B


@inline
def pwm_init(pin: str, duty: uint8, prescaler: uint8):
    match pin:
        case "PB0":
            # Timer0 OC0A: Fast PWM non-inverting
            # TCCR0A = COM0A1 | WGM01 | WGM00 = 0x83
            DDRB[0] = 1
            OCR0A.value = duty
            TCCR0A.value = 0x83
            TCCR0B.value = prescaler
        case "PB1":
            # Timer0 OC0B: Fast PWM non-inverting
            # TCCR0A = COM0B1 | WGM01 | WGM00 = 0x23
            DDRB[1] = 1
            OCR0B.value = duty
            TCCR0A.value = 0x23
            TCCR0B.value = prescaler
        case "PB4":
            # Timer1 OC1B: Fast PWM mode via PWM1B bit and COM1B1
            # TCCR1: PWM1B=bit6, COM1B1=bit5, COM1B0=bit4, CS1[3:0]=prescaler
            DDRB[4] = 1
            OCR0A.value = duty   # OCR1B shares physical register with OCR0A
            TCCR1.value = prescaler
    if duty == 0:
        pwm_disconnect(pin)


# duty 0 means off, and OCRx = BOTTOM is not off: in fast PWM the output is set at
# BOTTOM and cleared on compare match, so OCRx = 0 leaves a one-prescaled-clock
# pulse in every 256. Off is the compare output disconnected and the pin driven
# low. duty 255 needs nothing: OCRx = MAX holds the output constantly high.
#
# Timer0 keeps its COM bits in TCCR0A (OC0A bits 7:6, OC0B bits 5:4); Timer1 keeps
# COM1B1:0 in TCCR1 bits 5:4, the same register as its prescaler, so start() after
# a stop() reconnects OC1B -- call set_duty() again to leave it off.
@inline
def pwm_disconnect(pin: str):
    match pin:
        case "PB0":
            TCCR0A.value = TCCR0A.value & 0x3F
            PORTB[0] = 0
        case "PB1":
            TCCR0A.value = TCCR0A.value & 0xCF
            PORTB[1] = 0
        case "PB4":
            TCCR1.value = TCCR1.value & 0xCF
            PORTB[4] = 0


@inline
def pwm_connect(pin: str):
    match pin:
        case "PB0":
            TCCR0A.value = TCCR0A.value | 0x80
        case "PB1":
            TCCR0A.value = TCCR0A.value | 0x20
        case "PB4":
            TCCR1.value = TCCR1.value | 0x20
