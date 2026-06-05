from pymcu.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD, PINB, PINC, PIND, EICRA, EIMSK, PCICR, PCMSK0, PCMSK1, PCMSK2, SREG
from pymcu.types import uint8, uint16, inline, ptr, compile_isr, const, asm

class _PinRegs:
    @inline
    def __init__(self, name: str):
        match name:
            case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
                self._port = PORTB
                self._ddr  = DDRB
                self._pin  = PINB
            case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5':
                self._port = PORTC
                self._ddr  = DDRC
                self._pin  = PINC
            case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
                self._port = PORTD
                self._ddr  = DDRD
                self._pin  = PIND
            case _:
                raise NotImplementedError('Unsupported Pin')
        match name:
            case 'PB0' | 'PC0' | 'PD0': self._bit = 0
            case 'PB1' | 'PC1' | 'PD1': self._bit = 1
            case 'PB2' | 'PC2' | 'PD2': self._bit = 2
            case 'PB3' | 'PC3' | 'PD3': self._bit = 3
            case 'PB4' | 'PC4' | 'PD4': self._bit = 4
            case 'PB5' | 'PC5' | 'PD5': self._bit = 5
            case 'PD6':                  self._bit = 6
            case 'PD7':                  self._bit = 7
            case _:                      raise NotImplementedError('Unsupported Pin')

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
# mode=0 (wait): returns 1 if condition met within timeout, 0 on timeout.
# mode=1 (meas): returns loop-count//2 when condition met, max//2 on timeout.
# pin_pulse_in dispatches to one sbis+sbic pair via compile-time DCE.
# AVR I/O addresses: PINB=0x03, PINC=0x06, PIND=0x09

# ---- PIND (I/O 0x09) bits 0-7 -----------------------------------------------

def _pind_sbis_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b0_lp:")
    asm("    SBIS 0x09, 0")
    asm("    RJMP _pind_sbis_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b0_lp:")
    asm("    SBIC 0x09, 0")
    asm("    RJMP _pind_sbic_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b1_lp:")
    asm("    SBIS 0x09, 1")
    asm("    RJMP _pind_sbis_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b1_lp:")
    asm("    SBIC 0x09, 1")
    asm("    RJMP _pind_sbic_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b2_lp:")
    asm("    SBIS 0x09, 2")
    asm("    RJMP _pind_sbis_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b2_lp:")
    asm("    SBIC 0x09, 2")
    asm("    RJMP _pind_sbic_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b3_lp:")
    asm("    SBIS 0x09, 3")
    asm("    RJMP _pind_sbis_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b3_lp:")
    asm("    SBIC 0x09, 3")
    asm("    RJMP _pind_sbic_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b4_lp:")
    asm("    SBIS 0x09, 4")
    asm("    RJMP _pind_sbis_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b4_lp:")
    asm("    SBIC 0x09, 4")
    asm("    RJMP _pind_sbic_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b5_lp:")
    asm("    SBIS 0x09, 5")
    asm("    RJMP _pind_sbis_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b5_lp:")
    asm("    SBIC 0x09, 5")
    asm("    RJMP _pind_sbic_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b6(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b6_lp:")
    asm("    SBIS 0x09, 6")
    asm("    RJMP _pind_sbis_b6_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b6_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b6_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b6_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b6_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b6_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b6(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b6_lp:")
    asm("    SBIC 0x09, 6")
    asm("    RJMP _pind_sbic_b6_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b6_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b6_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b6_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b6_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b6_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b6_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pind_sbis_b7(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbis_b7_lp:")
    asm("    SBIS 0x09, 7")
    asm("    RJMP _pind_sbis_b7_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b7_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b7_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbis_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbis_b7_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbis_b7_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbis_b7_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pind_sbic_b7(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pind_sbic_b7_lp:")
    asm("    SBIC 0x09, 7")
    asm("    RJMP _pind_sbic_b7_c")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b7_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b7_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pind_sbic_b7_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pind_sbic_b7_lp")
    asm("    TST  R22")
    asm("    BREQ _pind_sbic_b7_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pind_sbic_b7_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


# ---- PINB (I/O 0x03) bits 0-5 -----------------------------------------------

def _pinb_sbis_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b0_lp:")
    asm("    SBIS 0x03, 0")
    asm("    RJMP _pinb_sbis_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b0_lp:")
    asm("    SBIC 0x03, 0")
    asm("    RJMP _pinb_sbic_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinb_sbis_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b1_lp:")
    asm("    SBIS 0x03, 1")
    asm("    RJMP _pinb_sbis_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b1_lp:")
    asm("    SBIC 0x03, 1")
    asm("    RJMP _pinb_sbic_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinb_sbis_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b2_lp:")
    asm("    SBIS 0x03, 2")
    asm("    RJMP _pinb_sbis_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b2_lp:")
    asm("    SBIC 0x03, 2")
    asm("    RJMP _pinb_sbic_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinb_sbis_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b3_lp:")
    asm("    SBIS 0x03, 3")
    asm("    RJMP _pinb_sbis_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b3_lp:")
    asm("    SBIC 0x03, 3")
    asm("    RJMP _pinb_sbic_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinb_sbis_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b4_lp:")
    asm("    SBIS 0x03, 4")
    asm("    RJMP _pinb_sbis_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b4_lp:")
    asm("    SBIC 0x03, 4")
    asm("    RJMP _pinb_sbic_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinb_sbis_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbis_b5_lp:")
    asm("    SBIS 0x03, 5")
    asm("    RJMP _pinb_sbis_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbis_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbis_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbis_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbis_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinb_sbic_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinb_sbic_b5_lp:")
    asm("    SBIC 0x03, 5")
    asm("    RJMP _pinb_sbic_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinb_sbic_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinb_sbic_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pinb_sbic_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinb_sbic_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


# ---- PINC (I/O 0x06) bits 0-5 -----------------------------------------------

def _pinc_sbis_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b0_lp:")
    asm("    SBIS 0x06, 0")
    asm("    RJMP _pinc_sbis_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b0(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b0_lp:")
    asm("    SBIC 0x06, 0")
    asm("    RJMP _pinc_sbic_b0_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b0_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b0_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b0_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b0_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b0_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b0_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinc_sbis_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b1_lp:")
    asm("    SBIS 0x06, 1")
    asm("    RJMP _pinc_sbis_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b1(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b1_lp:")
    asm("    SBIC 0x06, 1")
    asm("    RJMP _pinc_sbic_b1_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b1_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b1_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b1_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b1_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b1_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b1_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinc_sbis_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b2_lp:")
    asm("    SBIS 0x06, 2")
    asm("    RJMP _pinc_sbis_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b2(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b2_lp:")
    asm("    SBIC 0x06, 2")
    asm("    RJMP _pinc_sbic_b2_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b2_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b2_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b2_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b2_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b2_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b2_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinc_sbis_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b3_lp:")
    asm("    SBIS 0x06, 3")
    asm("    RJMP _pinc_sbis_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b3(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b3_lp:")
    asm("    SBIC 0x06, 3")
    asm("    RJMP _pinc_sbic_b3_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b3_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b3_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b3_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b3_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b3_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b3_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinc_sbis_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b4_lp:")
    asm("    SBIS 0x06, 4")
    asm("    RJMP _pinc_sbis_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b4(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b4_lp:")
    asm("    SBIC 0x06, 4")
    asm("    RJMP _pinc_sbic_b4_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b4_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b4_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b4_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b4_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b4_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b4_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


def _pinc_sbis_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbis_b5_lp:")
    asm("    SBIS 0x06, 5")
    asm("    RJMP _pinc_sbis_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbis_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbis_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbis_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbis_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")

def _pinc_sbic_b5(max_count: uint16, mode: uint8) -> uint16:
    asm("    MOVW R26, R24")
    asm("    CLR  R24")
    asm("    CLR  R25")
    asm("_pinc_sbic_b5_lp:")
    asm("    SBIC 0x06, 5")
    asm("    RJMP _pinc_sbic_b5_c")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b5_wd")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b5_wd:")
    asm("    LDI  R24, 1")
    asm("    CLR  R25")
    asm("    RET")
    asm("_pinc_sbic_b5_c:")
    asm("    ADIW R24, 1")
    asm("    CP   R24, R26")
    asm("    CPC  R25, R27")
    asm("    BRCS _pinc_sbic_b5_lp")
    asm("    TST  R22")
    asm("    BREQ _pinc_sbic_b5_wt")
    asm("    MOVW R24, R26")
    asm("    LSR  R25")
    asm("    ROR  R24")
    asm("    RET")
    asm("_pinc_sbic_b5_wt:")
    asm("    CLR  R24")
    asm("    CLR  R25")


@inline
def pin_pulse_in(pin_reg: ptr[uint8], bit: uint8, state: uint8, timeout_us: uint16) -> uint16:
    # Non-inline asm helpers guarantee an 8-cycle inner loop:
    # SBIS/SBIC(1) + RJMP(2) + ADIW(2) + CP/CPC(2) + BRCS(1) = 8 cyc/iter.
    # state=1 (HIGH pulse): sbis waits for HIGH (mode=0), sbic measures HIGH (mode=1).
    # state=0 (LOW pulse):  sbic waits for LOW (mode=0), sbis measures LOW (mode=1).
    # timeout_us passed directly as max_count (8-cycle loop, 16 MHz: 2 iters/us).
    # All bit/state branches are compile-time foldable via DCE.
    result: uint16 = 0
    if pin_reg == PIND:
        if state == 1:
            if bit == 0:
                if _pind_sbis_b0(timeout_us, 0) != 0:
                    result = _pind_sbic_b0(timeout_us, 1)
            elif bit == 1:
                if _pind_sbis_b1(timeout_us, 0) != 0:
                    result = _pind_sbic_b1(timeout_us, 1)
            elif bit == 2:
                if _pind_sbis_b2(timeout_us, 0) != 0:
                    result = _pind_sbic_b2(timeout_us, 1)
            elif bit == 3:
                if _pind_sbis_b3(timeout_us, 0) != 0:
                    result = _pind_sbic_b3(timeout_us, 1)
            elif bit == 4:
                if _pind_sbis_b4(timeout_us, 0) != 0:
                    result = _pind_sbic_b4(timeout_us, 1)
            elif bit == 5:
                if _pind_sbis_b5(timeout_us, 0) != 0:
                    result = _pind_sbic_b5(timeout_us, 1)
            elif bit == 6:
                if _pind_sbis_b6(timeout_us, 0) != 0:
                    result = _pind_sbic_b6(timeout_us, 1)
            elif bit == 7:
                if _pind_sbis_b7(timeout_us, 0) != 0:
                    result = _pind_sbic_b7(timeout_us, 1)
        else:
            if bit == 0:
                if _pind_sbic_b0(timeout_us, 0) != 0:
                    result = _pind_sbis_b0(timeout_us, 1)
            elif bit == 1:
                if _pind_sbic_b1(timeout_us, 0) != 0:
                    result = _pind_sbis_b1(timeout_us, 1)
            elif bit == 2:
                if _pind_sbic_b2(timeout_us, 0) != 0:
                    result = _pind_sbis_b2(timeout_us, 1)
            elif bit == 3:
                if _pind_sbic_b3(timeout_us, 0) != 0:
                    result = _pind_sbis_b3(timeout_us, 1)
            elif bit == 4:
                if _pind_sbic_b4(timeout_us, 0) != 0:
                    result = _pind_sbis_b4(timeout_us, 1)
            elif bit == 5:
                if _pind_sbic_b5(timeout_us, 0) != 0:
                    result = _pind_sbis_b5(timeout_us, 1)
            elif bit == 6:
                if _pind_sbic_b6(timeout_us, 0) != 0:
                    result = _pind_sbis_b6(timeout_us, 1)
            elif bit == 7:
                if _pind_sbic_b7(timeout_us, 0) != 0:
                    result = _pind_sbis_b7(timeout_us, 1)
    elif pin_reg == PINB:
        if state == 1:
            if bit == 0:
                if _pinb_sbis_b0(timeout_us, 0) != 0:
                    result = _pinb_sbic_b0(timeout_us, 1)
            elif bit == 1:
                if _pinb_sbis_b1(timeout_us, 0) != 0:
                    result = _pinb_sbic_b1(timeout_us, 1)
            elif bit == 2:
                if _pinb_sbis_b2(timeout_us, 0) != 0:
                    result = _pinb_sbic_b2(timeout_us, 1)
            elif bit == 3:
                if _pinb_sbis_b3(timeout_us, 0) != 0:
                    result = _pinb_sbic_b3(timeout_us, 1)
            elif bit == 4:
                if _pinb_sbis_b4(timeout_us, 0) != 0:
                    result = _pinb_sbic_b4(timeout_us, 1)
            elif bit == 5:
                if _pinb_sbis_b5(timeout_us, 0) != 0:
                    result = _pinb_sbic_b5(timeout_us, 1)
        else:
            if bit == 0:
                if _pinb_sbic_b0(timeout_us, 0) != 0:
                    result = _pinb_sbis_b0(timeout_us, 1)
            elif bit == 1:
                if _pinb_sbic_b1(timeout_us, 0) != 0:
                    result = _pinb_sbis_b1(timeout_us, 1)
            elif bit == 2:
                if _pinb_sbic_b2(timeout_us, 0) != 0:
                    result = _pinb_sbis_b2(timeout_us, 1)
            elif bit == 3:
                if _pinb_sbic_b3(timeout_us, 0) != 0:
                    result = _pinb_sbis_b3(timeout_us, 1)
            elif bit == 4:
                if _pinb_sbic_b4(timeout_us, 0) != 0:
                    result = _pinb_sbis_b4(timeout_us, 1)
            elif bit == 5:
                if _pinb_sbic_b5(timeout_us, 0) != 0:
                    result = _pinb_sbis_b5(timeout_us, 1)
    elif pin_reg == PINC:
        if state == 1:
            if bit == 0:
                if _pinc_sbis_b0(timeout_us, 0) != 0:
                    result = _pinc_sbic_b0(timeout_us, 1)
            elif bit == 1:
                if _pinc_sbis_b1(timeout_us, 0) != 0:
                    result = _pinc_sbic_b1(timeout_us, 1)
            elif bit == 2:
                if _pinc_sbis_b2(timeout_us, 0) != 0:
                    result = _pinc_sbic_b2(timeout_us, 1)
            elif bit == 3:
                if _pinc_sbis_b3(timeout_us, 0) != 0:
                    result = _pinc_sbic_b3(timeout_us, 1)
            elif bit == 4:
                if _pinc_sbis_b4(timeout_us, 0) != 0:
                    result = _pinc_sbic_b4(timeout_us, 1)
            elif bit == 5:
                if _pinc_sbis_b5(timeout_us, 0) != 0:
                    result = _pinc_sbic_b5(timeout_us, 1)
        else:
            if bit == 0:
                if _pinc_sbic_b0(timeout_us, 0) != 0:
                    result = _pinc_sbis_b0(timeout_us, 1)
            elif bit == 1:
                if _pinc_sbic_b1(timeout_us, 0) != 0:
                    result = _pinc_sbis_b1(timeout_us, 1)
            elif bit == 2:
                if _pinc_sbic_b2(timeout_us, 0) != 0:
                    result = _pinc_sbis_b2(timeout_us, 1)
            elif bit == 3:
                if _pinc_sbic_b3(timeout_us, 0) != 0:
                    result = _pinc_sbis_b3(timeout_us, 1)
            elif bit == 4:
                if _pinc_sbic_b4(timeout_us, 0) != 0:
                    result = _pinc_sbis_b4(timeout_us, 1)
            elif bit == 5:
                if _pinc_sbic_b5(timeout_us, 0) != 0:
                    result = _pinc_sbis_b5(timeout_us, 1)
    return result
