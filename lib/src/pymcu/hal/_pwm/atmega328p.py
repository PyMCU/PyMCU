from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A, OCR0B, TCCR2A, TCCR2B, OCR2A, OCR2B, DDRD, DDRB
from pymcu.types import uint8, inline

@inline
def pwm_init(pin: str, duty: uint8):
    if pin == "PD6":
        DDRD[6] = 1
        OCR0A.value = duty
        TCCR0A.value = 0x83
        TCCR0B.value = 0x03
    elif pin == "PD5":
        DDRD[5] = 1
        OCR0B.value = duty
        TCCR0A.value = 0x23
        TCCR0B.value = 0x03
    elif pin == "PB1":
        DDRB[1] = 1
    elif pin == "PB3":
        DDRB[3] = 1
        OCR2A.value = duty
        TCCR2A.value = 0x83
        TCCR2B.value = 0x04
    elif pin == "PD3":
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
    elif pin == "PB3":
        OCR2A.value = duty
    elif pin == "PD3":
        OCR2B.value = duty

@inline
def pwm_start(pin: str):
    if pin == "PD6" or pin == "PD5":
        TCCR0B.value = 0x03
    elif pin == "PB3" or pin == "PD3":
        TCCR2B.value = 0x04

@inline
def pwm_stop(pin: str):
    if pin == "PD6" or pin == "PD5":
        TCCR0B.value = 0x00
    elif pin == "PB3" or pin == "PD3":
        TCCR2B.value = 0x00
