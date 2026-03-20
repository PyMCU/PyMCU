from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A, OCR0B, TCCR2A, TCCR2B, OCR2A, OCR2B, DDRD, DDRB
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, OCR1AL, OCR1BL
from pymcu.types import uint8, uint16, inline

@inline
def pwm_init(pin: str, duty: uint8):
    if pin == "PD6":
        # Timer0 OC0A: Fast PWM non-inverting (COM0A1=1), WGM01:00=11 -> TCCR0A=0x83
        DDRD[6] = 1
        OCR0A.value = duty
        TCCR0A.value = 0x83
        TCCR0B.value = 0x03
    elif pin == "PD5":
        # Timer0 OC0B: Fast PWM non-inverting (COM0B1=1), WGM01:00=11 -> TCCR0A=0x23
        DDRD[5] = 1
        OCR0B.value = duty
        TCCR0A.value = 0x23
        TCCR0B.value = 0x03
    elif pin == "PB1":
        # Timer1 OC1A: Fast PWM 8-bit (WGM1=0101), COM1A1=1 -> TCCR1A=0x82, TCCR1B=0x0A
        # Prescaler 8 (CS1[2:0]=010)
        DDRB[1] = 1
        OCR1AL.value = duty
        TCCR1A.value = 0x82
        TCCR1B.value = 0x0A
    elif pin == "PB2":
        # Timer1 OC1B: Fast PWM 8-bit, COM1B1=1 -> TCCR1A=0x22, TCCR1B=0x0A
        DDRB[2] = 1
        OCR1BL.value = duty
        TCCR1A.value = 0x22
        TCCR1B.value = 0x0A
    elif pin == "PB3":
        # Timer2 OC2A: Fast PWM non-inverting (COM2A1=1), WGM21:20=11 -> TCCR2A=0x83
        DDRB[3] = 1
        OCR2A.value = duty
        TCCR2A.value = 0x83
        TCCR2B.value = 0x04
    elif pin == "PD3":
        # Timer2 OC2B: Fast PWM non-inverting (COM2B1=1), WGM21:20=11 -> TCCR2A=0x23
        DDRD[3] = 1
        OCR2B.value = duty
        TCCR2A.value = 0x23
        TCCR2B.value = 0x04

@inline
def pwm_set_duty(pin: str, duty: uint8):
    if pin == "PD6":
        OCR0A.value = duty
    elif pin == "PD5":
        OCR0B.value = duty
    elif pin == "PB1":
        OCR1AL.value = duty
    elif pin == "PB2":
        OCR1BL.value = duty
    elif pin == "PB3":
        OCR2A.value = duty
    elif pin == "PD3":
        OCR2B.value = duty

@inline
def pwm_start(pin: str):
    if pin == "PD6" or pin == "PD5":
        TCCR0B.value = 0x03
    elif pin == "PB1" or pin == "PB2":
        TCCR1B.value = 0x0A
    elif pin == "PB3" or pin == "PD3":
        TCCR2B.value = 0x04

@inline
def pwm_stop(pin: str):
    if pin == "PD6" or pin == "PD5":
        TCCR0B.value = 0x00
    elif pin == "PB1" or pin == "PB2":
        TCCR1B.value = 0x00
    elif pin == "PB3" or pin == "PD3":
        TCCR2B.value = 0x00
