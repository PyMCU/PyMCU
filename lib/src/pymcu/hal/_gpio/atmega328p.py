from pymcu.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD, PINB, PINC, PIND, EICRA, EIMSK, PCICR, PCMSK0, PCMSK1, PCMSK2, SREG
from pymcu.types import uint8, uint16, inline, ptr

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PORTB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return PORTC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PORTD

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return DDRB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return DDRC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return DDRD

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PINB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return PINC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PIND

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PB0' | 'PC0' | 'PD0':
            return 0
        case 'PB1' | 'PC1' | 'PD1':
            return 1
        case 'PB2' | 'PC2' | 'PD2':
            return 2
        case 'PB3' | 'PC3' | 'PD3':
            return 3
        case 'PB4' | 'PC4' | 'PD4':
            return 4
        case 'PB5' | 'PC5' | 'PD5':
            return 5
        case 'PD6':
            return 6
        case 'PD7':
            return 7

@inline
def pin_irq_setup(name: str, trigger: uint8):
    if name == "PD2":
        if trigger == 1:
            EICRA[0] = 0
            EICRA[1] = 1
        elif trigger == 2:
            EICRA[0] = 1
            EICRA[1] = 1
        elif trigger == 4:
            EICRA[0] = 0
            EICRA[1] = 0
        EIMSK[0] = 1
        SREG[7] = 1
    elif name == "PD3":
        if trigger == 1:
            EICRA[2] = 0
            EICRA[3] = 1
        elif trigger == 2:
            EICRA[2] = 1
            EICRA[3] = 1
        elif trigger == 4:
            EICRA[2] = 0
            EICRA[3] = 0
        EIMSK[1] = 1
        SREG[7] = 1
    elif name == "PB0":
        PCICR[0] = 1
        PCMSK0[0] = 1
        SREG[7] = 1
    elif name == "PB1":
        PCICR[0] = 1
        PCMSK0[1] = 1
        SREG[7] = 1
    elif name == "PB2":
        PCICR[0] = 1
        PCMSK0[2] = 1
        SREG[7] = 1
    elif name == "PB3":
        PCICR[0] = 1
        PCMSK0[3] = 1
        SREG[7] = 1
    elif name == "PB4":
        PCICR[0] = 1
        PCMSK0[4] = 1
        SREG[7] = 1
    elif name == "PB5":
        PCICR[0] = 1
        PCMSK0[5] = 1
        SREG[7] = 1
    elif name == "PC0":
        PCICR[1] = 1
        PCMSK1[0] = 1
        SREG[7] = 1
    elif name == "PC1":
        PCICR[1] = 1
        PCMSK1[1] = 1
        SREG[7] = 1
    elif name == "PC2":
        PCICR[1] = 1
        PCMSK1[2] = 1
        SREG[7] = 1
    elif name == "PC3":
        PCICR[1] = 1
        PCMSK1[3] = 1
        SREG[7] = 1
    elif name == "PC4":
        PCICR[1] = 1
        PCMSK1[4] = 1
        SREG[7] = 1
    elif name == "PC5":
        PCICR[1] = 1
        PCMSK1[5] = 1
        SREG[7] = 1
    elif name == "PD0":
        PCICR[2] = 1
        PCMSK2[0] = 1
        SREG[7] = 1
    elif name == "PD1":
        PCICR[2] = 1
        PCMSK2[1] = 1
        SREG[7] = 1
    elif name == "PD4":
        PCICR[2] = 1
        PCMSK2[4] = 1
        SREG[7] = 1
    elif name == "PD5":
        PCICR[2] = 1
        PCMSK2[5] = 1
        SREG[7] = 1
    elif name == "PD6":
        PCICR[2] = 1
        PCMSK2[6] = 1
        SREG[7] = 1
    elif name == "PD7":
        PCICR[2] = 1
        PCMSK2[7] = 1
        SREG[7] = 1


@inline
def pin_pulse_in(pin_reg: ptr[uint8], bit: uint8, state: uint8, timeout_us: uint16) -> uint16:
    # Wait for pin to reach desired state (with timeout)
    wait: uint16 = 0
    while wait < timeout_us:
        if pin_reg[bit] == state:
            break
        wait = wait + 1
    if wait >= timeout_us:
        return 0
    # Measure how long pin stays in that state
    count: uint16 = 0
    while count < timeout_us:
        if pin_reg[bit] != state:
            break
        count = count + 1
    return count
