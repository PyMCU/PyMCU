from pymcu.chips.atmega328p import TCCR0A, TCCR0B, TCNT0, TIMSK0, TIFR0, OCR0A
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, TCNT1L, TCNT1H, TIMSK1, TIFR1, OCR1A
from pymcu.chips.atmega328p import TCCR2A, TCCR2B, TCNT2, TIMSK2, TIFR2, OCR2A
from pymcu.chips.atmega328p import SREG
from pymcu.types import uint8, uint16, uint32, inline, asm, compile_isr, Callable

# ---- millis() / micros() counter (Timer0 overflow, prescaler 64) ----
#
# Timer0 at 16 MHz, prescaler 64: overflow every 256 * 64 / 16000000 = 1024 us ~= 1 ms.
# _millis_count is incremented once per overflow by _millis_ovf_isr().
# millis() reads it under CLI/SEI guard so the 4-byte read is atomic.
# micros() approximates using the current TCNT0 value (each tick = 4 us).

_millis_count: uint32 = 0


def _millis_ovf_isr():
    # Non-inline: compiled once, placed at Timer0 OVF vector by millis_init().
    # Increment the global overflow counter; main reads it via millis().
    _millis_count = _millis_count + 1

# ---- Timer0 (8-bit, shared with delay_ms / PWM OC0A/OC0B) ----

@inline
def timer0_init(prescaler: uint16):
    TCCR0A.value = 0x00
    if prescaler == 1:
        TCCR0B.value = 0x01
    elif prescaler == 8:
        TCCR0B.value = 0x02
    elif prescaler == 64:
        TCCR0B.value = 0x03
    elif prescaler == 256:
        TCCR0B.value = 0x04
    elif prescaler == 1024:
        TCCR0B.value = 0x05

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
def timer1_init(prescaler: uint16):
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
def timer2_init(prescaler: uint16):
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

# ---- timer_irq_setup: register a handler at the OVF vector via compile_isr ----
# compile_isr takes the BYTE address of the vector table entry (same as @interrupt).
# Timer0 OVF vector: byte 0x0020 (word 0x0010)
# Timer1 OVF vector: byte 0x001A (word 0x000D)
# Timer2 OVF vector: byte 0x0012 (word 0x0009)

@inline
def timer0_irq_setup(handler: Callable):
    TIMSK0[0] = 1
    SREG[7] = 1
    compile_isr(handler, 0x0020)

@inline
def timer1_irq_setup(handler: Callable):
    TIMSK1[0] = 1
    SREG[7] = 1
    compile_isr(handler, 0x001A)

@inline
def timer2_irq_setup(handler: Callable):
    TIMSK2[0] = 1
    SREG[7] = 1
    compile_isr(handler, 0x0012)

# ---- timer_irq_compa_setup: register a handler at the COMPA vector via compile_isr ----
# Timer0 COMPA vector: byte 0x001C (word 0x000E)
# Timer1 COMPA vector: byte 0x0016 (word 0x000B)
# Timer2 COMPA vector: byte 0x000E (word 0x0007)

@inline
def timer0_irq_compa_setup(handler: Callable):
    TIMSK0[1] = 1        # OCIE0A
    SREG[7] = 1          # SEI
    compile_isr(handler, 0x001C)

@inline
def timer1_irq_compa_setup(handler: Callable):
    TIMSK1[1] = 1        # OCIE1A
    SREG[7] = 1          # SEI
    compile_isr(handler, 0x0016)

@inline
def timer2_irq_compa_setup(handler: Callable):
    TIMSK2[1] = 1        # OCIE2A
    SREG[7] = 1          # SEI
    compile_isr(handler, 0x000E)


# ---- millis_init / millis / micros ----
#
# millis_init() configures Timer0 with prescaler 64 (normal mode) and registers
# _millis_ovf_isr at the Timer0 OVF vector (byte 0x0020).  Call once from main().
# Do NOT call if Timer0 is already in use for PWM or another purpose.
#
# millis() returns elapsed milliseconds since millis_init() was called.
# The count wraps to 0 after 2^32 - 1 ms (~49 days).
#
# micros() returns elapsed microseconds.  It combines the 32-bit overflow counter
# with the current TCNT0 value (each TCNT0 tick = 4 us at prescaler 64, 16 MHz).
# Accuracy: +/-4 us from the TCNT0 granularity.

@inline
def millis_init():
    # Normal mode: WGM0[2:0] = 000. CS0[2:0] = 011 -> prescaler 64.
    TCCR0A.value = 0x00
    TCCR0B.value = 0x03   # prescaler 64
    TCNT0.value  = 0
    TIMSK0[0]    = 1      # TOIE0: enable Timer0 overflow interrupt
    SREG[7]      = 1      # SEI: enable global interrupts
    compile_isr(_millis_ovf_isr, 0x0020)   # Timer0 OVF vector (byte address)


@inline
def millis() -> uint32:
    # Read the 4-byte counter atomically by disabling interrupts briefly.
    asm("CLI")
    t: uint32 = _millis_count
    asm("SEI")
    return t


@inline
def micros() -> uint32:
    # Approximate: millis * 1024 (each overflow = 1024 us) + TCNT0 * 4.
    # CLI/SEI guard prevents the overflow counter incrementing mid-read.
    asm("CLI")
    t: uint32 = _millis_count
    tc: uint8 = TCNT0.value
    asm("SEI")
    ticks: uint32 = uint32(tc)
    return (t << 10) + (ticks << 2)

