from pymcu.types import uint8, uint16, inline, compile_isr, Callable, const
from pymcu.exceptions import CompileError
from pymcu.chips.atmega328p import ADMUX, ADCSRA, ADCL, ADCH, SREG


# Returns the ADMUX register value for an analog input.
#
# The argument is whatever names the input on this part, exactly as pymcu.hal.gpio.Pin takes
# it: the register name ("PC0"), the Arduino analog name ("A0"), the Arduino board number
# (14) or the converter's own channel number (0). They were not all here before: "A1" and
# every other Arduino analog name fell through to the default and read channel 0, so a
# program that asked for A1 read A0 and said nothing.
#
# External channels: bits 7:6 = REFS1:0 = 01 (AVcc reference); bits 3:0 = MUX3:0 = channel.
# Internal temperature sensor (ch8): REFS1:0 = 11 (internal 1.1V); MUX = 1000.
# Folded at compile time; `const` is what the match needs to answer correctly.
@inline
def adc_channel_admux(channel) -> uint8:
    match channel:
        case "PC0" | "A0" | 0 | 14:
            return 0x40
        case "PC1" | "A1" | 1 | 15:
            return 0x41
        case "PC2" | "A2" | 2 | 16:
            return 0x42
        case "PC3" | "A3" | 3 | 17:
            return 0x43
        case "PC4" | "A4" | 4 | 18:
            return 0x44
        case "PC5" | "A5" | 5 | 19:
            return 0x45
        case "TEMP":
            # Internal temperature sensor: REFS1:0=11 (1.1V), MUX=1000 (ch8)
            # ADMUX = 0b11001000 = 0xC8
            return 0xC8
        case "ADC8" | 8:
            return 0xC8
        case "VBG":
            # Internal 1.1V bandgap measured against AVcc reference:
            # REFS1:0 = 01 (AVcc), MUX3:0 = 1110 (ch14). ADMUX = 0b01001110.
            # Used to compute Vcc = 1.1 * 1024 / ADCraw.
            return 0x4E
        case _:
            # An unknown name used to return ADMUX for channel 0, so a program that asked
            # for a pin with no converter behind it built clean and read A0 forever. The
            # only honest answer is to refuse where the AnalogPin is written.
            raise CompileError(
                "this pin has no ADC channel. On the ATmega 328P/168/48 the analog inputs "
                "are PC0 to PC5, spelled \"PC0\" to \"PC5\", \"A0\" to \"A5\", 14 to 19 (the "
                "Arduino board numbers) or 0 to 5 (the channel numbers); the internal "
                "sources are \"TEMP\" (the die temperature sensor) and \"VBG\" (the 1.1 V "
                "bandgap). Pass one of those, or read the signal through a pin that has "
                "a channel.")


@inline
def adc_init(admux_val: uint8):
    # Enable ADC with prescaler 128 (ADPS=111 -> 125 kHz at 16 MHz).
    # admux_val encodes both the reference (AVcc) and the channel.
    ADMUX.value = admux_val
    ADCSRA.value = 0x87


# Select the channel (and reference) this pin was built with. Every operation that STARTS a
# conversion has to do this: ADMUX is one register shared by every AnalogPin, so with more than
# one pin alive the last one constructed owned it and every read returned that channel.
@inline
def adc_select(admux_val: uint8):
    ADMUX.value = admux_val


@inline
def adc_start():
    ADCSRA[6] = 1


# Start a conversion with ADC Interrupt Enable (ADIE bit 3).
# The ADC complete ISR fires at vector byte 0x002A / word 0x0015.
@inline
def adc_start_int():
    ADCSRA[3] = 1
    ADCSRA[6] = 1


# Read the 10-bit result after conversion completes (ADIF set or from ISR).
@inline
def adc_read_result() -> uint16:
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result


# Register an ISR at the ADC Complete vector (byte 0x002A / word 0x0015).
# Enables ADIE (ADC interrupt enable) and global interrupts (SEI).
# The handler MUST read ADCL before ADCH to latch the result.
@inline
def adc_irq_setup(handler: Callable):
    ADCSRA[3] = 1        # ADIE: ADC interrupt enable
    SREG[7] = 1          # SEI: global interrupt enable
    compile_isr(handler, 0x002A)   # ADC Complete vector byte address


# Start conversion, poll ADSC until clear, return raw 10-bit result (0-1023).
@inline
def adc_read() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6]:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result


# Start conversion, poll, return the result scaled to the full 16-bit range (0-65535).
#
# Multiplying the 10-bit result by 64 topped out at 65472: full scale on the pin read 63
# counts short of full scale in the number, so a caller dividing by 65535 to get volts was
# always low and `value == 65535` never happened. Replicating the top bits into the bottom
# ones is the scaling every CircuitPython port uses, and it maps 1023 onto exactly 65535.
@inline
def adc_read_u16() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6]:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    raw: uint16 = lo + hi * 256
    return (raw << 6) | (raw >> 4)
