from pymcu.chips.atmega328p import TCCR0A, TCCR0B, TCNT0, TIMSK0, TIFR0
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, TCNT1L, TCNT1H, TIMSK1, TIFR1
from pymcu.chips.atmega328p import TCCR2A, TCCR2B, TCNT2, TIMSK2, TIFR2
from pymcu.types import uint8, inline

# ---- Timer0 (8-bit, shared with delay_ms / PWM OC0A/OC0B) ----

@inline
def timer0_init(prescaler: uint8):
    TCCR0A.value = 0x00
    if prescaler == 1:
        TCCR0B.value = 0x01
    elif prescaler == 8:
        TCCR0B.value = 0x02
    elif prescaler == 64:
        TCCR0B.value = 0x03
    elif prescaler == 256:
        TCCR0B.value = 0x04

@inline
def timer0_start():
    TIMSK0[0] = 1   # TOIE0 - overflow interrupt enable

@inline
def timer0_stop():
    TIMSK0[0] = 0
    TCCR0B.value = 0x00

@inline
def timer0_clear():
    TCNT0.value = 0

@inline
def timer0_overflow() -> uint8:
    return TIFR0[0]   # TOV0

# ---- Timer1 (16-bit, OC1A=PB1/D9, OC1B=PB2/D10) ----
# Prescalers: 1, 8, 64, 256, 1024
# OVF vector: 0x000d (word addr); ~0.5 Hz at 16 MHz, prescaler 1024, 16-bit wrap

@inline
def timer1_init(prescaler: uint8):
    TCCR1A.value = 0x00
    TCCR1B.value = 0x00
    if prescaler == 1:
        TCCR1B.value = 0x01
    elif prescaler == 8:
        TCCR1B.value = 0x02
    elif prescaler == 64:
        TCCR1B.value = 0x03
    elif prescaler == 256:
        TCCR1B.value = 0x04
    elif prescaler == 1024:
        TCCR1B.value = 0x05

@inline
def timer1_start():
    TIMSK1[0] = 1   # TOIE1 - overflow interrupt enable

@inline
def timer1_stop():
    TIMSK1[0] = 0
    TCCR1B.value = 0x00

@inline
def timer1_clear():
    TCNT1L.value = 0
    TCNT1H.value = 0

@inline
def timer1_overflow() -> uint8:
    return TIFR1[0]   # TOV1

# ---- Timer2 (8-bit async, OC2A=PB3/D11, OC2B=PD3/D3) ----
# Prescalers: 1, 8, 32, 64, 128, 256, 1024
# OVF vector: 0x0009 (word addr)

@inline
def timer2_init(prescaler: uint8):
    TCCR2A.value = 0x00
    TCCR2B.value = 0x00
    if prescaler == 1:
        TCCR2B.value = 0x01
    elif prescaler == 8:
        TCCR2B.value = 0x02
    elif prescaler == 32:
        TCCR2B.value = 0x03
    elif prescaler == 64:
        TCCR2B.value = 0x04
    elif prescaler == 128:
        TCCR2B.value = 0x05
    elif prescaler == 256:
        TCCR2B.value = 0x06
    elif prescaler == 1024:
        TCCR2B.value = 0x07

@inline
def timer2_start():
    TIMSK2[0] = 1   # TOIE2 - overflow interrupt enable

@inline
def timer2_stop():
    TIMSK2[0] = 0
    TCCR2B.value = 0x00

@inline
def timer2_clear():
    TCNT2.value = 0

@inline
def timer2_overflow() -> uint8:
    return TIFR2[0]   # TOV2
