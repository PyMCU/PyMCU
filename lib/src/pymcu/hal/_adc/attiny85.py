from pymcu.chips.attiny85 import ADMUX, ADCSRA, ADCL, ADCH, SREG
from pymcu.types import uint8, uint16, inline, compile_isr, Callable

# ATtiny85/45/25 ADC HAL
#
# ATtiny85 has 4 usable ADC channels (no hardware UART, ADC0 = PB5 = RESET):
#   ADC1 = PB2 (MUX3:0 = 0001) -- physical pin 7
#   ADC2 = PB4 (MUX3:0 = 0010) -- physical pin 3
#   ADC3 = PB3 (MUX3:0 = 0011) -- physical pin 2
#   ADC0 = PB5 (MUX3:0 = 0000) -- physical pin 1 (RESET; fuse change required)
#
# ADMUX bits (ATtiny85):
#   REFS1:0 = 00  -> VCC as reference (bits 7:6 = 0)
#   ADLAR  = 0    -> right-adjust result (bit 5 = 0)
#   MUX3:0        -> channel select (bits 3:0)
#
# ADCSRA prescaler: use 64 (0x06) for 8 MHz -> 125 kHz ADC clock.
#   ADCSRA = ADEN | ADPS2 | ADPS1 = 0x86
#
# ADC Complete vector: word 0x0008, byte 0x0010

@inline
def adc_channel_admux(channel: str) -> uint8:
    match channel:
        case "PB2":
            return 0x01   # ADC1, VCC ref
        case "PB4":
            return 0x02   # ADC2, VCC ref
        case "PB3":
            return 0x03   # ADC3, VCC ref
        case "PB5":
            return 0x00   # ADC0, VCC ref (RESET pin -- use with care)
        case _:
            return 0x01   # default: ADC1

@inline
def adc_init(admux_val: uint8):
    ADMUX.value = admux_val
    ADCSRA.value = 0x86   # ADEN | ADPS2 | ADPS1 (prescaler 64, enable ADC)

@inline
def adc_start():
    ADCSRA[6] = 1   # ADSC: start conversion

@inline
def adc_start_int():
    ADCSRA[3] = 1   # ADIE: ADC interrupt enable
    ADCSRA[6] = 1   # ADSC: start conversion

@inline
def adc_read_result() -> uint16:
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result

@inline
def adc_irq_setup(handler: Callable):
    ADCSRA[3] = 1           # ADIE
    SREG[7] = 1             # SEI
    compile_isr(handler, 0x0010)   # ADC Complete: word 0x0008, byte 0x0010

@inline
def adc_read() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6] == 1:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result

@inline
def adc_read_u16() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6] == 1:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result * 64
