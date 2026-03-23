from whipsnake.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD, PINB, PINC, PIND, EICRA, EIMSK, PCICR, PCMSK0, PCMSK1, PCMSK2, SREG
from whipsnake.types import uint8, uint16, inline, ptr, compile_isr, const, asm

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PORTB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return PORTC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PORTD
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return DDRB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return DDRC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return DDRD
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PINB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
            return PINC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PIND
        case _:
            raise NotImplementedError('Unsupported Pin')

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
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def pin_irq_setup(name: str, trigger: uint8, handler: const = 0):
    # trigger values: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
    # EICRA ISCn1:ISCn0 encoding: 00=low-level, 01=any-edge, 10=falling, 11=rising
    # handler: compile-time function reference; compile_isr() registers it at the
    # correct vector so the @interrupt decorator is not needed on the handler.
    if name == "PD2":
        if trigger == 1:
            # falling edge: ISC01=1, ISC00=0
            EICRA[0] = 0
            EICRA[1] = 1
        elif trigger == 2:
            # rising edge: ISC01=1, ISC00=1
            EICRA[0] = 1
            EICRA[1] = 1
        elif trigger == 3:
            # any edge (change): ISC01=0, ISC00=1
            EICRA[0] = 1
            EICRA[1] = 0
        elif trigger == 4:
            # low level: ISC01=0, ISC00=0
            EICRA[0] = 0
            EICRA[1] = 0
        EIMSK[0] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0002)
    elif name == "PD3":
        if trigger == 1:
            # falling edge: ISC11=1, ISC10=0
            EICRA[2] = 0
            EICRA[3] = 1
        elif trigger == 2:
            # rising edge: ISC11=1, ISC10=1
            EICRA[2] = 1
            EICRA[3] = 1
        elif trigger == 3:
            # any edge (change): ISC11=0, ISC10=1
            EICRA[2] = 1
            EICRA[3] = 0
        elif trigger == 4:
            # low level: ISC11=0, ISC10=0
            EICRA[2] = 0
            EICRA[3] = 0
        EIMSK[1] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0004)
    elif name == "PB0":
        PCICR[0] = 1
        PCMSK0[0] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PB1":
        PCICR[0] = 1
        PCMSK0[1] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PB2":
        PCICR[0] = 1
        PCMSK0[2] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PB3":
        PCICR[0] = 1
        PCMSK0[3] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PB4":
        PCICR[0] = 1
        PCMSK0[4] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PB5":
        PCICR[0] = 1
        PCMSK0[5] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PC0":
        PCICR[1] = 1
        PCMSK1[0] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PC1":
        PCICR[1] = 1
        PCMSK1[1] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PC2":
        PCICR[1] = 1
        PCMSK1[2] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PC3":
        PCICR[1] = 1
        PCMSK1[3] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PC4":
        PCICR[1] = 1
        PCMSK1[4] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PC5":
        PCICR[1] = 1
        PCMSK1[5] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PD0":
        PCICR[2] = 1
        PCMSK2[0] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PD1":
        PCICR[2] = 1
        PCMSK2[1] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PD4":
        PCICR[2] = 1
        PCMSK2[4] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PD5":
        PCICR[2] = 1
        PCMSK2[5] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PD6":
        PCICR[2] = 1
        PCMSK2[6] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PD7":
        PCICR[2] = 1
        PCMSK2[7] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)


# ---- pulse_in timing helpers -----------------------------------------------
# Non-inline asm() helpers with guaranteed 8-cycle inner loops.
# Loop: SBIS/SBIC(1) + RJMP(2) + ADIW(2) + CP/CPC(2) + BRCS(1) = 8 cyc/iter.
# pin_pulse_in dispatches to one wait+measure pair via compile-time DCE.
# AVR I/O addresses: PINB=0x03, PINC=0x06, PIND=0x09

# ---- PIND (I/O 0x09) bits 0-7 -----------------------------------------------

def _pind_wait_hi_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b0:")
    asm("    SBIS 0x09, 0")
    asm("    RJMP _pind_whi_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b0:")
    asm("    SBIC 0x09, 0")
    asm("    RJMP _pind_wlo_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b0:")
    asm("    SBIC 0x09, 0")
    asm("    RJMP _pind_mhi_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b0:")
    asm("    SBIS 0x09, 0")
    asm("    RJMP _pind_mlo_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b1:")
    asm("    SBIS 0x09, 1")
    asm("    RJMP _pind_whi_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b1:")
    asm("    SBIC 0x09, 1")
    asm("    RJMP _pind_wlo_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b1:")
    asm("    SBIC 0x09, 1")
    asm("    RJMP _pind_mhi_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b1:")
    asm("    SBIS 0x09, 1")
    asm("    RJMP _pind_mlo_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b2:")
    asm("    SBIS 0x09, 2")
    asm("    RJMP _pind_whi_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b2:")
    asm("    SBIC 0x09, 2")
    asm("    RJMP _pind_wlo_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b2:")
    asm("    SBIC 0x09, 2")
    asm("    RJMP _pind_mhi_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b2:")
    asm("    SBIS 0x09, 2")
    asm("    RJMP _pind_mlo_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b3:")
    asm("    SBIS 0x09, 3")
    asm("    RJMP _pind_whi_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b3:")
    asm("    SBIC 0x09, 3")
    asm("    RJMP _pind_wlo_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b3:")
    asm("    SBIC 0x09, 3")
    asm("    RJMP _pind_mhi_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b3:")
    asm("    SBIS 0x09, 3")
    asm("    RJMP _pind_mlo_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b4:")
    asm("    SBIS 0x09, 4")
    asm("    RJMP _pind_whi_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b4:")
    asm("    SBIC 0x09, 4")
    asm("    RJMP _pind_wlo_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b4:")
    asm("    SBIC 0x09, 4")
    asm("    RJMP _pind_mhi_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b4:")
    asm("    SBIS 0x09, 4")
    asm("    RJMP _pind_mlo_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b5:")
    asm("    SBIS 0x09, 5")
    asm("    RJMP _pind_whi_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b5:")
    asm("    SBIC 0x09, 5")
    asm("    RJMP _pind_wlo_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b5:")
    asm("    SBIC 0x09, 5")
    asm("    RJMP _pind_mhi_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b5:")
    asm("    SBIS 0x09, 5")
    asm("    RJMP _pind_mlo_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b6(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b6:")
    asm("    SBIS 0x09, 6")
    asm("    RJMP _pind_whi_b6_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b6")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b6(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b6:")
    asm("    SBIC 0x09, 6")
    asm("    RJMP _pind_wlo_b6_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b6")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b6(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b6:")
    asm("    SBIC 0x09, 6")
    asm("    RJMP _pind_mhi_b6_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b6")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b6(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b6:")
    asm("    SBIS 0x09, 6")
    asm("    RJMP _pind_mlo_b6_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b6")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pind_wait_hi_b7(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_whi_b7:")
    asm("    SBIS 0x09, 7")
    asm("    RJMP _pind_whi_b7_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_whi_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_whi_b7")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_wait_lo_b7(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_wlo_b7:")
    asm("    SBIC 0x09, 7")
    asm("    RJMP _pind_wlo_b7_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_wlo_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_wlo_b7")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_meas_hi_b7(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mhi_b7:")
    asm("    SBIC 0x09, 7")
    asm("    RJMP _pind_mhi_b7_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mhi_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mhi_b7")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pind_meas_lo_b7(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_mlo_b7:")
    asm("    SBIS 0x09, 7")
    asm("    RJMP _pind_mlo_b7_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_mlo_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_mlo_b7")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


# ---- PINB (I/O 0x03) bits 0-5 -----------------------------------------------

def _pinb_wait_hi_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b0:")
    asm("    SBIS 0x03, 0")
    asm("    RJMP _pinb_whi_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b0:")
    asm("    SBIC 0x03, 0")
    asm("    RJMP _pinb_wlo_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b0:")
    asm("    SBIC 0x03, 0")
    asm("    RJMP _pinb_mhi_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b0:")
    asm("    SBIS 0x03, 0")
    asm("    RJMP _pinb_mlo_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinb_wait_hi_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b1:")
    asm("    SBIS 0x03, 1")
    asm("    RJMP _pinb_whi_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b1:")
    asm("    SBIC 0x03, 1")
    asm("    RJMP _pinb_wlo_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b1:")
    asm("    SBIC 0x03, 1")
    asm("    RJMP _pinb_mhi_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b1:")
    asm("    SBIS 0x03, 1")
    asm("    RJMP _pinb_mlo_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinb_wait_hi_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b2:")
    asm("    SBIS 0x03, 2")
    asm("    RJMP _pinb_whi_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b2:")
    asm("    SBIC 0x03, 2")
    asm("    RJMP _pinb_wlo_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b2:")
    asm("    SBIC 0x03, 2")
    asm("    RJMP _pinb_mhi_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b2:")
    asm("    SBIS 0x03, 2")
    asm("    RJMP _pinb_mlo_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinb_wait_hi_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b3:")
    asm("    SBIS 0x03, 3")
    asm("    RJMP _pinb_whi_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b3:")
    asm("    SBIC 0x03, 3")
    asm("    RJMP _pinb_wlo_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b3:")
    asm("    SBIC 0x03, 3")
    asm("    RJMP _pinb_mhi_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b3:")
    asm("    SBIS 0x03, 3")
    asm("    RJMP _pinb_mlo_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinb_wait_hi_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b4:")
    asm("    SBIS 0x03, 4")
    asm("    RJMP _pinb_whi_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b4:")
    asm("    SBIC 0x03, 4")
    asm("    RJMP _pinb_wlo_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b4:")
    asm("    SBIC 0x03, 4")
    asm("    RJMP _pinb_mhi_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b4:")
    asm("    SBIS 0x03, 4")
    asm("    RJMP _pinb_mlo_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinb_wait_hi_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_whi_b5:")
    asm("    SBIS 0x03, 5")
    asm("    RJMP _pinb_whi_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_whi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_whi_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_wait_lo_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_wlo_b5:")
    asm("    SBIC 0x03, 5")
    asm("    RJMP _pinb_wlo_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_wlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_wlo_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_meas_hi_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mhi_b5:")
    asm("    SBIC 0x03, 5")
    asm("    RJMP _pinb_mhi_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mhi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mhi_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinb_meas_lo_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_mlo_b5:")
    asm("    SBIS 0x03, 5")
    asm("    RJMP _pinb_mlo_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_mlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_mlo_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


# ---- PINC (I/O 0x06) bits 0-5 -----------------------------------------------

def _pinc_wait_hi_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b0:")
    asm("    SBIS 0x06, 0")
    asm("    RJMP _pinc_whi_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b0(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b0:")
    asm("    SBIC 0x06, 0")
    asm("    RJMP _pinc_wlo_b0_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b0")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b0:")
    asm("    SBIC 0x06, 0")
    asm("    RJMP _pinc_mhi_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b0(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b0:")
    asm("    SBIS 0x06, 0")
    asm("    RJMP _pinc_mlo_b0_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b0")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinc_wait_hi_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b1:")
    asm("    SBIS 0x06, 1")
    asm("    RJMP _pinc_whi_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b1(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b1:")
    asm("    SBIC 0x06, 1")
    asm("    RJMP _pinc_wlo_b1_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b1")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b1:")
    asm("    SBIC 0x06, 1")
    asm("    RJMP _pinc_mhi_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b1(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b1:")
    asm("    SBIS 0x06, 1")
    asm("    RJMP _pinc_mlo_b1_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b1")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinc_wait_hi_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b2:")
    asm("    SBIS 0x06, 2")
    asm("    RJMP _pinc_whi_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b2(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b2:")
    asm("    SBIC 0x06, 2")
    asm("    RJMP _pinc_wlo_b2_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b2")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b2:")
    asm("    SBIC 0x06, 2")
    asm("    RJMP _pinc_mhi_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b2(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b2:")
    asm("    SBIS 0x06, 2")
    asm("    RJMP _pinc_mlo_b2_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b2")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinc_wait_hi_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b3:")
    asm("    SBIS 0x06, 3")
    asm("    RJMP _pinc_whi_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b3(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b3:")
    asm("    SBIC 0x06, 3")
    asm("    RJMP _pinc_wlo_b3_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b3")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b3:")
    asm("    SBIC 0x06, 3")
    asm("    RJMP _pinc_mhi_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b3(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b3:")
    asm("    SBIS 0x06, 3")
    asm("    RJMP _pinc_mlo_b3_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b3")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinc_wait_hi_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b4:")
    asm("    SBIS 0x06, 4")
    asm("    RJMP _pinc_whi_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b4(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b4:")
    asm("    SBIC 0x06, 4")
    asm("    RJMP _pinc_wlo_b4_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b4")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b4:")
    asm("    SBIC 0x06, 4")
    asm("    RJMP _pinc_mhi_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b4(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b4:")
    asm("    SBIS 0x06, 4")
    asm("    RJMP _pinc_mlo_b4_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b4")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


def _pinc_wait_hi_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_whi_b5:")
    asm("    SBIS 0x06, 5")
    asm("    RJMP _pinc_whi_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_whi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_whi_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_wait_lo_b5(max_count: uint16) -> uint8:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_wlo_b5:")
    asm("    SBIC 0x06, 5")
    asm("    RJMP _pinc_wlo_b5_c")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_wlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_wlo_b5")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_meas_hi_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mhi_b5:")
    asm("    SBIC 0x06, 5")
    asm("    RJMP _pinc_mhi_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mhi_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mhi_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")

def _pinc_meas_lo_b5(max_count: uint16) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_mlo_b5:")
    asm("    SBIS 0x06, 5")
    asm("    RJMP _pinc_mlo_b5_c")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_mlo_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_mlo_b5")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")


@inline
def pin_pulse_in(pin_reg: ptr[uint8], bit: uint8, state: uint8, timeout_us: uint16) -> uint16:
    # Non-inline asm helpers guarantee an 8-cycle inner loop:
    # SBIS/SBIC(1) + RJMP(2) + ADIW(2) + CP/CPC(2) + BRCS(1) = 8 cyc/iter.
    # Meas helpers return count//2 (us at 16 MHz, 2 iters/us).
    # timeout_us is passed directly as max_count (500 us headroom at 16 MHz).
    # All bit/state branches are compile-time foldable via DCE.
    result: uint16 = 0
    if pin_reg == PIND:
        if state == 1:
            if bit == 0:
                if _pind_wait_hi_b0(timeout_us) != 0:
                    result = _pind_meas_hi_b0(timeout_us)
            elif bit == 1:
                if _pind_wait_hi_b1(timeout_us) != 0:
                    result = _pind_meas_hi_b1(timeout_us)
            elif bit == 2:
                if _pind_wait_hi_b2(timeout_us) != 0:
                    result = _pind_meas_hi_b2(timeout_us)
            elif bit == 3:
                if _pind_wait_hi_b3(timeout_us) != 0:
                    result = _pind_meas_hi_b3(timeout_us)
            elif bit == 4:
                if _pind_wait_hi_b4(timeout_us) != 0:
                    result = _pind_meas_hi_b4(timeout_us)
            elif bit == 5:
                if _pind_wait_hi_b5(timeout_us) != 0:
                    result = _pind_meas_hi_b5(timeout_us)
            elif bit == 6:
                if _pind_wait_hi_b6(timeout_us) != 0:
                    result = _pind_meas_hi_b6(timeout_us)
            elif bit == 7:
                if _pind_wait_hi_b7(timeout_us) != 0:
                    result = _pind_meas_hi_b7(timeout_us)
        else:
            if bit == 0:
                if _pind_wait_lo_b0(timeout_us) != 0:
                    result = _pind_meas_lo_b0(timeout_us)
            elif bit == 1:
                if _pind_wait_lo_b1(timeout_us) != 0:
                    result = _pind_meas_lo_b1(timeout_us)
            elif bit == 2:
                if _pind_wait_lo_b2(timeout_us) != 0:
                    result = _pind_meas_lo_b2(timeout_us)
            elif bit == 3:
                if _pind_wait_lo_b3(timeout_us) != 0:
                    result = _pind_meas_lo_b3(timeout_us)
            elif bit == 4:
                if _pind_wait_lo_b4(timeout_us) != 0:
                    result = _pind_meas_lo_b4(timeout_us)
            elif bit == 5:
                if _pind_wait_lo_b5(timeout_us) != 0:
                    result = _pind_meas_lo_b5(timeout_us)
            elif bit == 6:
                if _pind_wait_lo_b6(timeout_us) != 0:
                    result = _pind_meas_lo_b6(timeout_us)
            elif bit == 7:
                if _pind_wait_lo_b7(timeout_us) != 0:
                    result = _pind_meas_lo_b7(timeout_us)
    elif pin_reg == PINB:
        if state == 1:
            if bit == 0:
                if _pinb_wait_hi_b0(timeout_us) != 0:
                    result = _pinb_meas_hi_b0(timeout_us)
            elif bit == 1:
                if _pinb_wait_hi_b1(timeout_us) != 0:
                    result = _pinb_meas_hi_b1(timeout_us)
            elif bit == 2:
                if _pinb_wait_hi_b2(timeout_us) != 0:
                    result = _pinb_meas_hi_b2(timeout_us)
            elif bit == 3:
                if _pinb_wait_hi_b3(timeout_us) != 0:
                    result = _pinb_meas_hi_b3(timeout_us)
            elif bit == 4:
                if _pinb_wait_hi_b4(timeout_us) != 0:
                    result = _pinb_meas_hi_b4(timeout_us)
            elif bit == 5:
                if _pinb_wait_hi_b5(timeout_us) != 0:
                    result = _pinb_meas_hi_b5(timeout_us)
        else:
            if bit == 0:
                if _pinb_wait_lo_b0(timeout_us) != 0:
                    result = _pinb_meas_lo_b0(timeout_us)
            elif bit == 1:
                if _pinb_wait_lo_b1(timeout_us) != 0:
                    result = _pinb_meas_lo_b1(timeout_us)
            elif bit == 2:
                if _pinb_wait_lo_b2(timeout_us) != 0:
                    result = _pinb_meas_lo_b2(timeout_us)
            elif bit == 3:
                if _pinb_wait_lo_b3(timeout_us) != 0:
                    result = _pinb_meas_lo_b3(timeout_us)
            elif bit == 4:
                if _pinb_wait_lo_b4(timeout_us) != 0:
                    result = _pinb_meas_lo_b4(timeout_us)
            elif bit == 5:
                if _pinb_wait_lo_b5(timeout_us) != 0:
                    result = _pinb_meas_lo_b5(timeout_us)
    elif pin_reg == PINC:
        if state == 1:
            if bit == 0:
                if _pinc_wait_hi_b0(timeout_us) != 0:
                    result = _pinc_meas_hi_b0(timeout_us)
            elif bit == 1:
                if _pinc_wait_hi_b1(timeout_us) != 0:
                    result = _pinc_meas_hi_b1(timeout_us)
            elif bit == 2:
                if _pinc_wait_hi_b2(timeout_us) != 0:
                    result = _pinc_meas_hi_b2(timeout_us)
            elif bit == 3:
                if _pinc_wait_hi_b3(timeout_us) != 0:
                    result = _pinc_meas_hi_b3(timeout_us)
            elif bit == 4:
                if _pinc_wait_hi_b4(timeout_us) != 0:
                    result = _pinc_meas_hi_b4(timeout_us)
            elif bit == 5:
                if _pinc_wait_hi_b5(timeout_us) != 0:
                    result = _pinc_meas_hi_b5(timeout_us)
        else:
            if bit == 0:
                if _pinc_wait_lo_b0(timeout_us) != 0:
                    result = _pinc_meas_lo_b0(timeout_us)
            elif bit == 1:
                if _pinc_wait_lo_b1(timeout_us) != 0:
                    result = _pinc_meas_lo_b1(timeout_us)
            elif bit == 2:
                if _pinc_wait_lo_b2(timeout_us) != 0:
                    result = _pinc_meas_lo_b2(timeout_us)
            elif bit == 3:
                if _pinc_wait_lo_b3(timeout_us) != 0:
                    result = _pinc_meas_lo_b3(timeout_us)
            elif bit == 4:
                if _pinc_wait_lo_b4(timeout_us) != 0:
                    result = _pinc_meas_lo_b4(timeout_us)
            elif bit == 5:
                if _pinc_wait_lo_b5(timeout_us) != 0:
                    result = _pinc_meas_lo_b5(timeout_us)
    return result
