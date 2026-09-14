from pymcu.chips.attiny85 import ADMUX, ADCSRA, ADCL, ADCH, SREG
from pymcu.types import uint8, uint16, inline, compile_isr, Callable, const
from pymcu.exceptions import CompileError

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

# Returns the ADMUX register value for an analog input.
#
# The argument names the input the way pymcu.hal.gpio.Pin takes it: the register name
# ("PB2"), the ADC channel name ("A1") or the channel number (1). The digital pin numbers
# 0 to 5 are NOT accepted here: on this part they mean PB0 to PB5, which is a different
# order from the channels (ADC0 is PB5, ADC1 is PB2), so an integer would mean two things.
# The channel numbering is the one pymcu.hal.adc uses on every other part, so it wins.
#
# ADMUX bits: REFS1:0 = 00 (VCC reference), ADLAR = 0, MUX3:0 = channel.
@inline
def adc_channel_admux(channel) -> uint8:
    match channel:
        case "PB5" | "A0" | 0:
            return 0x00   # ADC0, VCC ref (RESET pin -- needs a fuse change to use)
        case "PB2" | "A1" | 1:
            return 0x01   # ADC1, VCC ref
        case "PB4" | "A2" | 2:
            return 0x02   # ADC2, VCC ref
        case "PB3" | "A3" | 3:
            return 0x03   # ADC3, VCC ref
        case _:
            # An unknown name used to return ADC1, so asking for a pin with no converter
            # behind it built clean and read PB2 forever.
            raise CompileError(
                "this pin has no ADC channel. On the ATtiny 25/45/85 the analog inputs are "
                "PB2 (ADC1), PB4 (ADC2), PB3 (ADC3) and PB5 (ADC0, the RESET pin, which "
                "needs a fuse change), spelled by register name, as \"A0\" to \"A3\", or as "
                "the channel number 0 to 3. Pass one of those.")


@inline
def adc_init(admux_val: uint8):
    ADMUX.value = admux_val
    ADCSRA.value = 0x86   # ADEN | ADPS2 | ADPS1 (prescaler 64, enable ADC)

# Select the channel (and reference) this pin was built with. Every operation that STARTS a
# conversion has to do this: ADMUX is one register shared by every AnalogPin, so with more than
# one pin alive the last one constructed owned it and every read returned that channel.
@inline
def adc_select(admux_val: uint8):
    ADMUX.value = admux_val


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
    while ADCSRA[6]:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result

# The 10-bit result scaled to the full 16-bit range. Multiplying by 64 topped out at 65472,
# so full scale on the pin was 63 counts short of full scale in the number; replicating the
# top bits into the bottom ones maps 1023 onto exactly 65535.
@inline
def adc_read_u16() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6]:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    raw: uint16 = lo + hi * 256
    return (raw << 6) | (raw >> 4)
