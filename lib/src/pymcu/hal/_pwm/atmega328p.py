from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A, OCR0B
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, OCR1AL, OCR1BL
from pymcu.chips.atmega328p import TCCR2A, TCCR2B, OCR2A, OCR2B
from pymcu.chips.atmega328p import DDRD, DDRB
from pymcu.types import uint8, uint16, inline, ptr


# Compile-time pin -> OCR register pointer.
# The result is stored as self._ocr so set_duty() is a single register write.
@inline
def pwm_select_ocr(pin: str) -> ptr[uint8]:
    match pin:
        case "PD6":
            return OCR0A
        case "PD5":
            return OCR0B
        case "PB1":
            return OCR1AL
        case "PB2":
            return OCR1BL
        case "PB3":
            return OCR2A
        case "PD3":
            return OCR2B


# Compile-time pin -> TCCRxB register pointer (for start/stop).
@inline
def pwm_select_tccr_b(pin: str) -> ptr[uint8]:
    match pin:
        case "PD6" | "PD5":
            return TCCR0B
        case "PB1" | "PB2":
            return TCCR1B
        case "PB3" | "PD3":
            return TCCR2B


# Compile-time pin -> TCCRxB value that starts (enables) the PWM.
@inline
def pwm_select_start_val(pin: str) -> uint8:
    match pin:
        case "PD6" | "PD5":
            return 0x03
        case "PB1" | "PB2":
            return 0x0A
        case "PB3" | "PD3":
            return 0x04


@inline
def pwm_init(pin: str, duty: uint8):
    match pin:
        case "PD6":
            # Timer0 OC0A: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x83
            DDRD[6] = 1
            OCR0A.value = duty
            TCCR0A.value = 0x83
            TCCR0B.value = 0x03
        case "PD5":
            # Timer0 OC0B: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x23
            DDRD[5] = 1
            OCR0B.value = duty
            TCCR0A.value = 0x23
            TCCR0B.value = 0x03
        case "PB1":
            # Timer1 OC1A: Fast PWM 8-bit (WGM=0101), COM1A1=1
            DDRB[1] = 1
            OCR1AL.value = duty
            TCCR1A.value = 0x82
            TCCR1B.value = 0x0A
        case "PB2":
            # Timer1 OC1B: Fast PWM 8-bit, COM1B1=1
            DDRB[2] = 1
            OCR1BL.value = duty
            TCCR1A.value = 0x22
            TCCR1B.value = 0x0A
        case "PB3":
            # Timer2 OC2A: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x83
            DDRB[3] = 1
            OCR2A.value = duty
            TCCR2A.value = 0x83
            TCCR2B.value = 0x04
        case "PD3":
            # Timer2 OC2B: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x23
            DDRD[3] = 1
            OCR2B.value = duty
            TCCR2A.value = 0x23
            TCCR2B.value = 0x04
