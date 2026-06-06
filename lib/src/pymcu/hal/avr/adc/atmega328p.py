from pymcu.types import uint8, uint16, inline, compile_isr, Callable
from pymcu.chips.atmega328p import ADMUX, ADCSRA, ADCL, ADCH, SREG


@inline
def adc_channel_admux(channel: str) -> uint8:
    # Returns the ADMUX register value for the given AVR pin name.
    # External channels: bits 7:6 = REFS1:0 = 01 (AVcc reference);
    #                    bits 3:0 = MUX3:0 = channel number.
    # Internal temp sensor (ch8): REFS1:0 = 11 (internal 1.1V); MUX = 1000.
    # Folded at compile time when channel is a const[str].
    match channel:
        case "PC0":
            return 0x40
        case "PC1":
            return 0x41
        case "PC2":
            return 0x42
        case "PC3":
            return 0x43
        case "PC4":
            return 0x44
        case "PC5":
            return 0x45
        case "TEMP":
            # Internal temperature sensor: REFS1:0=11 (1.1V), MUX=1000 (ch8)
            # ADMUX = 0b11001000 = 0xC8
            return 0xC8
        case "ADC8":
            return 0xC8
        case "VBG":
            # Internal 1.1V bandgap measured against AVcc reference:
            # REFS1:0 = 01 (AVcc), MUX3:0 = 1110 (ch14). ADMUX = 0b01001110.
            # Used to compute Vcc = 1.1 * 1024 / ADCraw.
            return 0x4E
        case _:
            return 0x40


@inline
def adc_init(admux_val: uint8):
    # Enable ADC with prescaler 128 (ADPS=111 -> 125 kHz at 16 MHz).
    # admux_val encodes both the reference (AVcc) and the channel.
    ADMUX.value = admux_val
    ADCSRA.value = 0x87


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


# Start conversion, poll, return result scaled to 16-bit (0-65535).
@inline
def adc_read_u16() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6]:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = (lo + hi * 256) * 64
    return result
