from pymcu.chips.pic18f45k50 import T0CON, TMR0L, TMR0H, INTCON
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, uint32, inline, compile_isr

@inline
def timer0_init(prescaler: uint16):
    # uint16, not uint8: the top prescaler this chip offers is 256, which a
    # uint8 cannot hold. Timer.__init__ declares uint16 and pic12 and the AVR
    # timers already agree; here the 256 arm was unreachable.
    if prescaler == 2:
        T0CON.value = 0x00
    elif prescaler == 4:
        T0CON.value = 0x01
    elif prescaler == 8:
        T0CON.value = 0x02
    elif prescaler == 16:
        T0CON.value = 0x03
    elif prescaler == 32:
        T0CON.value = 0x04
    elif prescaler == 64:
        T0CON.value = 0x05
    elif prescaler == 128:
        T0CON.value = 0x06
    elif prescaler == 256:
        T0CON.value = 0x07

@inline
def timer0_start():
    T0CON[7] = 1

@inline
def timer0_stop():
    T0CON[7] = 0

@inline
def timer0_clear():
    TMR0L.value = 0
    TMR0H.value = 0


@inline
def timer0_counter() -> uint16:
    lo: uint8 = TMR0L.value
    hi: uint8 = TMR0H.value
    result: uint16 = lo + hi * 256
    return result


@inline
def timer0_overflow() -> uint8:
    return INTCON[2]


@inline
def timer0_clear_overflow():
    INTCON[2] = 0


_millis_count: uint32 = 0
_millis_ms: uint32 = 0
_millis_fract: uint8 = 0


def _millis_ovf_isr():
    global _millis_count
    global _millis_ms
    global _millis_fract
    _millis_count = _millis_count + 1
    f: uint8 = _millis_fract + 3
    m: uint32 = _millis_ms + 1
    if f >= 125:
        f = f - 125
        m = m + 1
    _millis_fract = f
    _millis_ms = m
    INTCON[2] = 0


@inline
def _millis_t0con() -> uint8:
    match __FREQ__:
        case 4_000_000:
            return 0xC1
        case 8_000_000:
            return 0xC2
        case 16_000_000:
            return 0xC3
        case _:
            return 0xC3


@inline
def millis_init():
    """Arm Timer0 as the millisecond timebase; it stops being available for anything else.

    The accumulators are zeroed here rather than left to their module-level
    initialisers: an imported module's globals never get theirs emitted, so on a
    core without a RAM clear they start as whatever the last reset left behind.

    8-bit mode, prescaler scaled per __FREQ__ so one overflow is 1024 us and one
    counter tick is 4 us at 4, 8 and 16 MHz -- the same numbers the ATmega HAL
    produces, so the fractional correction below is the AVR one unchanged.
    """
    global _millis_count
    global _millis_ms
    global _millis_fract
    _millis_count = 0
    _millis_ms = 0
    _millis_fract = 0
    T0CON.value = _millis_t0con()
    TMR0L.value = 0
    INTCON[2] = 0
    INTCON[5] = 1
    INTCON[7] = 1
    compile_isr(_millis_ovf_isr, 0x0008)


@inline
def millis() -> uint32:
    """Milliseconds since millis_init(), read atomically under GIE.

    _millis_ms only advances when an overflow completes, so by itself it can
    lag true elapsed time by almost one whole overflow period (~1.024 ms):
    harmless amortized over many overflows, but up to ~0.1% on one isolated
    reading (the same gap the AVR HAL closes in its own millis() -- see its
    comment for the derivation). Fold in the current, not-yet-complete
    overflow's progress (TMR0L, 4 us/tick) plus the ms-fraction the ISR has
    carried but not yet rounded into _millis_ms (_millis_fract, eighths of a
    ms); together they cross 1000 us exactly when a whole extra millisecond
    has genuinely elapsed since the last ISR run.
    """
    INTCON[7] = 0
    ms: uint32 = _millis_ms
    fract: uint8 = _millis_fract
    tc: uint8 = TMR0L.value
    pending: uint8 = INTCON[2]
    INTCON[7] = 1
    if pending == 1:
        if tc < 255:
            ms = ms + 1
            fract = fract + 3
            if fract >= 125:
                fract = fract - 125
                ms = ms + 1
    extra_us: uint16 = uint16(fract) * 8 + uint16(tc) * 4
    if extra_us >= 1000:
        ms = ms + 1
    return ms


@inline
def micros() -> uint32:
    """Microseconds since millis_init(), 4 us resolution.

    The hardware counter keeps running with GIE cleared, so an overflow can land
    between reading the accumulator and reading TMR0L; TMR0IF still pending with
    a small TMR0L means that overflow has not been accounted yet.
    """
    INTCON[7] = 0
    t: uint32 = _millis_count
    tc: uint8 = TMR0L.value
    pending: uint8 = INTCON[2]
    INTCON[7] = 1
    if pending == 1:
        if tc < 255:
            t = t + 1
    ticks: uint32 = tc
    return t * 1024 + ticks * 4
