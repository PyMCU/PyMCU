from whipsnake.chips.atmega328p import TCCR0A, TCCR0B, TCNT0, TIMSK0, TIFR0, OCR0A
from whipsnake.chips.atmega328p import TCCR1A, TCCR1B, TCNT1L, TCNT1H, TIMSK1, TIFR1, OCR1A
from whipsnake.chips.atmega328p import TCCR2A, TCCR2B, TCNT2, TIMSK2, TIFR2, OCR2A
from whipsnake.types import uint8, uint16, inline

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

# Set Timer0 CTC compare value and enable CTC mode (WGM01=1 in TCCR0A).
# TIMSK0[1] = OCIE0A (compare match A interrupt enable).
# CTC vector: TIMER0_COMPA word 0x0E (byte 0x001C).
@inline
def timer0_set_compare(value: uint16):
    OCR0A.value = value & 0xFF
    TCCR0A.value = TCCR0A.value | 0x02   # WGM01 = 1 (CTC mode)
    TIMSK0[1] = 1                          # OCIE0A

@inline
def timer0_start_ctc():
    TIMSK0[1] = 1   # OCIE0A - compare match A interrupt enable

@inline
def timer0_stop_ctc():
    TIMSK0[1] = 0

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

# Set Timer1 CTC compare value and enable CTC mode (WGM12=1 in TCCR1B).
# TIMSK1[1] = OCIE1A (compare match A interrupt enable).
# CTC vector: TIMER1_COMPA word 0x0B (byte 0x0016).
@inline
def timer1_set_compare(value: uint16):
    OCR1A = value
    TCCR1B.value = TCCR1B.value | 0x08   # WGM12 = 1 (CTC mode)
    TIMSK1[1] = 1                          # OCIE1A

@inline
def timer1_start_ctc():
    TIMSK1[1] = 1   # OCIE1A

@inline
def timer1_stop_ctc():
    TIMSK1[1] = 0

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

# Set Timer2 CTC compare value and enable CTC mode (WGM21=1 in TCCR2A).
# TIMSK2[1] = OCIE2A (compare match A interrupt enable).
# CTC vector: TIMER2_COMPA word 0x07 (byte 0x000E).
@inline
def timer2_set_compare(value: uint16):
    OCR2A.value = value & 0xFF
    TCCR2A.value = TCCR2A.value | 0x02   # WGM21 = 1 (CTC mode)
    TIMSK2[1] = 1                          # OCIE2A

@inline
def timer2_start_ctc():
    TIMSK2[1] = 1   # OCIE2A

@inline
def timer2_stop_ctc():
    TIMSK2[1] = 0
