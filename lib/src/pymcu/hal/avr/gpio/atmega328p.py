from pymcu.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD, PINB, PINC, PIND, EICRA, EIMSK, PCICR, PCMSK0, PCMSK1, PCMSK2, SREG
from pymcu.types import uint8, uint16, inline, ptr, compile_isr, const, asm
from pymcu.exceptions import CompileError

class _PinRegs:
    # `name` is a port name ('PB5') or an Arduino Uno/Nano board number (13). Both
    # spellings sit in the same match because the number on the silkscreen is the
    # first one anyone writes, and matching them together keeps the whole thing a
    # compile-time fold: Pin(13, Pin.OUT) and Pin("PB5", Pin.OUT) emit the same
    # bytes. D0-D7 -> PORTD, D8-D13 -> PORTB, D14-D19 (A0-A5) -> PORTC.
    @inline
    def __init__(self, name: str):
        match name:
            case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
                self._port = PORTB
                self._ddr  = DDRB
                self._pin  = PINB
            case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
                self._port = PORTC
                self._ddr  = DDRC
                self._pin  = PINC
            case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7:
                self._port = PORTD
                self._ddr  = DDRD
                self._pin  = PIND
            case _:
                raise CompileError(
                    "unknown pin on this chip. Give a PORT NAME (PB0-PB5, PC0-PC5, "
                    "PD0-PD7) or an Arduino Uno/Nano board number (0-19; 13 is the "
                    "built-in LED and 14-19 are A0-A5).")
        match name:
            case 'PB0' | 'PC0' | 'PD0' | 0 | 8 | 14: self._bit = 0
            case 'PB1' | 'PC1' | 'PD1' | 1 | 9 | 15: self._bit = 1
            case 'PB2' | 'PC2' | 'PD2' | 2 | 10 | 16: self._bit = 2
            case 'PB3' | 'PC3' | 'PD3' | 3 | 11 | 17: self._bit = 3
            case 'PB4' | 'PC4' | 'PD4' | 4 | 12 | 18: self._bit = 4
            case 'PB5' | 'PC5' | 'PD5' | 5 | 13 | 19: self._bit = 5
            case 'PD6' | 6:                  self._bit = 6
            case 'PD7' | 7:                  self._bit = 7
            case _:
                raise CompileError(
                    "unknown pin on this chip. Give a PORT NAME (PB0-PB5, PC0-PC5, "
                    "PD0-PD7) or an Arduino Uno/Nano board number (0-19; 13 is the "
                    "built-in LED and 14-19 are A0-A5).")

@inline
def pin_irq_setup(name: str, trigger: uint8, handler: const = 0):
    # trigger values: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
    # EICRA ISCn1:ISCn0 encoding: 00=low-level, 01=any-edge, 10=falling, 11=rising
    # handler: compile-time function reference; compile_isr() registers it at the
    # correct vector so the @interrupt decorator is not needed on the handler.
    #
    # Checked BEFORE any register is touched, and once for every pin: a trigger that
    # matched no arm below used to fall off the end of the if/elif chain with EICRA left
    # at its reset value 0x00 -- which is LOW LEVEL -- and EIMSK enabled anyway. The pin
    # the user asked about never fired, and its complement re-asserted the interrupt for
    # as long as it stayed low, which wedges the part in an ISR that never returns.
    if trigger == 8:
        raise CompileError(
            "Pin.IRQ_HIGH_LEVEL is not supported on this chip. INT0/INT1 encode only "
            "four triggers in ISCn1:ISCn0 -- low level, any edge, falling and rising -- "
            "and high level is not one of them. Use Pin.IRQ_RISING for the moment the "
            "pin goes high, or read the pin in your loop.")
    if trigger != 1 and trigger != 2 and trigger != 3 and trigger != 4:
        raise CompileError(
            "unknown irq trigger. Pin.irq() takes ONE of Pin.IRQ_FALLING, Pin.IRQ_RISING, "
            "Pin.IRQ_CHANGE or Pin.IRQ_LOW_LEVEL. The four are not a bit mask that can be "
            "combined freely: `Pin.IRQ_FALLING | Pin.IRQ_RISING` is 3, which is exactly "
            "Pin.IRQ_CHANGE, and no other combination names a trigger the hardware has.")

    # A port name ('PD2') or an Arduino board number (2) -- the same two
    # spellings Pin() takes, matched together so both fold to one branch.
    match name:
        case 'PD2' | 2:
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
        case 'PD3' | 3:
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
        case 'PB0' | 8:
            PCICR[0] = 1
            PCMSK0[0] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PB1' | 9:
            PCICR[0] = 1
            PCMSK0[1] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PB2' | 10:
            PCICR[0] = 1
            PCMSK0[2] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PB3' | 11:
            PCICR[0] = 1
            PCMSK0[3] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PB4' | 12:
            PCICR[0] = 1
            PCMSK0[4] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PB5' | 13:
            PCICR[0] = 1
            PCMSK0[5] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0006)
        case 'PC0' | 14:
            PCICR[1] = 1
            PCMSK1[0] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PC1' | 15:
            PCICR[1] = 1
            PCMSK1[1] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PC2' | 16:
            PCICR[1] = 1
            PCMSK1[2] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PC3' | 17:
            PCICR[1] = 1
            PCMSK1[3] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PC4' | 18:
            PCICR[1] = 1
            PCMSK1[4] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PC5' | 19:
            PCICR[1] = 1
            PCMSK1[5] = 1
            SREG[7] = 1
            compile_isr(handler, 0x0008)
        case 'PD0' | 0:
            PCICR[2] = 1
            PCMSK2[0] = 1
            SREG[7] = 1
            compile_isr(handler, 0x000A)
        case 'PD1' | 1:
            PCICR[2] = 1
            PCMSK2[1] = 1
            SREG[7] = 1
            compile_isr(handler, 0x000A)
        case 'PD4' | 4:
            PCICR[2] = 1
            PCMSK2[4] = 1
            SREG[7] = 1
            compile_isr(handler, 0x000A)
        case 'PD5' | 5:
            PCICR[2] = 1
            PCMSK2[5] = 1
            SREG[7] = 1
            compile_isr(handler, 0x000A)
        case 'PD6' | 6:
            PCICR[2] = 1
            PCMSK2[6] = 1
            SREG[7] = 1
            compile_isr(handler, 0x000A)
        case 'PD7' | 7:
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
