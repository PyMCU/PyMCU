from pymcu.chips.attiny85 import TCCR0A, TCCR0B, TCNT0, OCR0A, OCR0B
from pymcu.chips.attiny85 import TCCR1, TCNT1, OCR1A, OCR1C, TIMSK, TIFR, SREG
from pymcu.types import uint8, uint16, inline, compile_isr, Callable
from pymcu.exceptions import CompileError

# ATtiny85/45/25 Timer HAL
#
# Timer0 -- 8-bit, same prescaler set as ATmega328P (1/8/64/256/1024)
#   TCCR0A: I/O 0x2A, data 0x4A
#   TCCR0B: I/O 0x33, data 0x53
#   TCNT0:  I/O 0x32, data 0x52
#   OCR0A:  I/O 0x29, data 0x49  (also == OCR1B)
#   OCR0B:  I/O 0x28, data 0x48  (also == OCR1C)
#   TIMSK:  I/O 0x39, data 0x59  (shared with Timer1)
#     TOIE0=bit1, OCIE0A=bit4, OCIE0B=bit3
#   TIFR:   I/O 0x38, data 0x58  (shared with Timer1)
#     TOV0=bit1, OCF0A=bit4, OCF0B=bit3
#   OVF vector:   word 0x0005, byte 0x000A
#   COMPA vector: word 0x000A, byte 0x0014
#
# Timer1 -- 8-bit, ATtiny85-specific single-register (TCCR1)
#   TCCR1:  I/O 0x30, data 0x50  (CS1[3:0] in bits [3:0])
#   TCNT1:  I/O 0x2F, data 0x4F
#   OCR1A:  I/O 0x2E, data 0x4E  (compare match A)
#   OCR1C:  I/O 0x28, data 0x48  (= OCR0B, TOP value in CTC mode)
#   TIMSK:  bit2=TOIE1, bit6=OCIE1A
#   TIFR:   bit2=TOV1, bit6=OCF1A
#   TCCR1 prescalers (CS1[3:0]): 0=off, 1=1, 2=2, 3=4, 4=8, 5=16, 6=32,
#     7=64, 8=128, 9=256, 10=512, 11=1024, 12=2048, 13=4096, 14=8192, 15=16384
#   OVF vector:   word 0x0004, byte 0x0008
#   COMPA vector: word 0x0003, byte 0x0006

# ---- Timer0 ----

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
    TIMSK[1] = 1   # TOIE0 = bit 1

@inline
def timer0_stop():
    TIMSK[1] = 0
    TCCR0B.value = 0x00

@inline
def timer0_clear():
    TCNT0.value = 0

@inline
def timer0_counter() -> uint8:
    return TCNT0.value

@inline
def timer0_overflow() -> uint8:
    return TIFR[1]   # TOV0 = bit 1

@inline
def timer0_set_compare(value: uint16):
    OCR0A.value = value & 0xFF
    TCCR0A.value = TCCR0A.value | 0x02   # WGM01 = 1 (CTC mode)
    TIMSK[4] = 1                          # OCIE0A = bit 4

@inline
def timer0_irq_setup(handler: Callable):
    TIMSK[1] = 1    # TOIE0
    SREG[7] = 1     # SEI
    compile_isr(handler, 0x000A)   # Timer0 OVF: word 0x0005, byte 0x000A

@inline
def timer0_irq_compa_setup(handler: Callable):
    TIMSK[4] = 1    # OCIE0A
    SREG[7] = 1     # SEI
    compile_isr(handler, 0x0014)   # Timer0 COMPA: word 0x000A, byte 0x0014

# ---- Timer1 ----
# Note: prescaler values are different from Timer0.
# Supported values: 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096,
#                   8192, 16384

@inline
def timer1_init(prescaler: uint16):
    TCCR1.value = 0x00
    if prescaler == 1:
        TCCR1.value = 0x01
    elif prescaler == 2:
        TCCR1.value = 0x02
    elif prescaler == 4:
        TCCR1.value = 0x03
    elif prescaler == 8:
        TCCR1.value = 0x04
    elif prescaler == 16:
        TCCR1.value = 0x05
    elif prescaler == 32:
        TCCR1.value = 0x06
    elif prescaler == 64:
        TCCR1.value = 0x07
    elif prescaler == 128:
        TCCR1.value = 0x08
    elif prescaler == 256:
        TCCR1.value = 0x09
    elif prescaler == 512:
        TCCR1.value = 0x0A
    elif prescaler == 1024:
        TCCR1.value = 0x0B
    elif prescaler == 2048:
        TCCR1.value = 0x0C
    elif prescaler == 4096:
        TCCR1.value = 0x0D
    elif prescaler == 8192:
        TCCR1.value = 0x0E
    elif prescaler == 16384:
        TCCR1.value = 0x0F

@inline
def timer1_start():
    TIMSK[2] = 1   # TOIE1 = bit 2

@inline
def timer1_stop():
    TIMSK[2] = 0
    TCCR1.value = 0x00

@inline
def timer1_clear():
    TCNT1.value = 0

@inline
def timer1_counter() -> uint8:
    # ATtiny85 Timer1 is 8-bit (unlike ATmega328P Timer1 which is 16-bit).
    return TCNT1.value

@inline
def timer1_overflow() -> uint8:
    return TIFR[2]   # TOV1 = bit 2

@inline
def timer1_set_compare(value: uint16):
    # CTC mode: timer resets when TCNT1 == OCR1C.
    # Set both OCR1A (compare A match) and OCR1C (TOP) to the same value.
    # OCR1C shares the physical register with OCR0B (both at data 0x48).
    OCR1A.value = value & 0xFF
    OCR1C.value = value & 0xFF   # OCR1C = OCR0B (same register)
    TCCR1.value = TCCR1.value | 0x80   # CTC1 = bit 7
    TIMSK[6] = 1                        # OCIE1A = bit 6

@inline
def timer1_irq_setup(handler: Callable):
    TIMSK[2] = 1    # TOIE1
    SREG[7] = 1     # SEI
    compile_isr(handler, 0x0008)   # Timer1 OVF: word 0x0004, byte 0x0008

@inline
def timer1_irq_compa_setup(handler: Callable):
    TIMSK[6] = 1    # OCIE1A
    SREG[7] = 1     # SEI
    compile_isr(handler, 0x0006)   # Timer1 COMPA: word 0x0003, byte 0x0006

# millis is not supported on the ATtiny85/45/25 and these refuse instead of
# answering. They used to be no-op stubs returning 0, so that avr/timer.py could
# export the names, and a value of the right type is the one thing a caller cannot
# tell apart from a working clock:
#
#     t: uint32 = millis()
#     if t > 100:            # 0 > 100 folds to false
#         p.high()           # and the branch is correctly eliminated
#
# That compiles clean, links clean, and silently ships firmware with the clock
# branch missing. A refusal at the call is the only outcome the caller can act on,
# and it costs nothing at runtime: the body is only reached if the program uses it,
# so avr/timer.py can still export all three. Issue #234.
@inline
def millis_init():
    raise CompileError("millis_init(): the ATtiny85/45/25 have no millisecond time base in PyMCU. Timer0 here is 8-bit with no separate TIMSK0/TIFR0, so the ATmega counter does not port across unchanged. Pace a loop with pymcu.time.delay_ms() instead.")


@inline
def millis() -> uint32:
    raise CompileError("millis(): the ATtiny85/45/25 have no millisecond time base in PyMCU, and returning 0 would make every `millis() - last > interval` test silently never fire. Pace a loop with pymcu.time.delay_ms() instead.")


@inline
def micros() -> uint32:
    raise CompileError("micros(): the ATtiny85/45/25 have no microsecond time base in PyMCU. It also backs asyncio.ticks(), so an await on this part would never complete. Pace a loop with pymcu.time.delay_us() instead.")
