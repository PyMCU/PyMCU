from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A, OCR0B
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, OCR1AL, OCR1BL, OCR1AH, OCR1BH
from pymcu.chips.atmega328p import TCCR2A, TCCR2B, OCR2A, OCR2B
from pymcu.chips.atmega328p import DDRD, DDRB, PORTD, PORTB
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, ptr


# Compile-time (pin, freq) -> TCCRxB CS value.
# Selects the smallest prescaler whose resulting frequency is >= freq.
# Five discrete frequencies at 16 MHz:
#   prescaler  1 ->  62500 Hz
#   prescaler  8 ->   7812 Hz
#   prescaler 64 ->    976 Hz  (default)
#   prescaler 256->    244 Hz
#   prescaler 1024->    61 Hz
# Timer2 uses a different CS encoding from Timer0/Timer1.
@inline
def pwm_prescaler_for_freq(pin: str, freq: uint16) -> uint8:
    match pin:
        case "PD6" | "PD5":
            # Timer0: CS[2:0] = 001/010/011/100/101. Thresholds are the geometric
            # midpoints between achievable frequencies: the chosen prescaler is
            # always the nearest one.
            if freq > 22097:
                return 0x01
            elif freq > 2762:
                return 0x02
            elif freq > 488:
                return 0x03
            elif freq > 122:
                return 0x04
            else:
                return 0x05
        case "PB1" | "PB2":
            # Timer1 Fast PWM 8-bit: WGM12 must stay set (bit3); CS in bits 2:0
            if freq > 22097:
                return 0x09
            elif freq > 2762:
                return 0x0A
            elif freq > 488:
                return 0x0B
            elif freq > 122:
                return 0x0C
            else:
                return 0x0D
        case "PB3" | "PD3":
            # Timer2: CS encoding 001(1) 010(8) 011(32) 100(64) 101(128) 110(256)
            # 111(1024) -- unlike Timer0/1 it also has /32 and /128, so it gets
            # 1953 Hz and 488 Hz buckets the other timers cannot reach.
            if freq > 22097:
                return 0x01
            elif freq > 3906:
                return 0x02
            elif freq > 1381:
                return 0x03
            elif freq > 690:
                return 0x04
            elif freq > 345:
                return 0x05
            elif freq > 122:
                return 0x06
            else:
                return 0x07
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


# OCR1A and OCR1B are 16-bit, and every 16-bit timer register on this chip shares
# one TEMP byte: writing the low byte commits TEMP as the high byte. Writing only
# OCR1AL therefore commits whatever the last 16-bit write on Timer1 left in TEMP,
# so a program that also drives Timer1 through the timer or servo HAL lands a duty
# far above TOP, the compare never matches, and the pin stays driven for the whole
# period. Measured: after a servo pulse leaves 0x0B in TEMP, PWM("PB1", 128) reads
# back OCR1A = 2944 and sits at 256/256 ticks high instead of 128/256.
#
# Clearing the high byte immediately before the low one makes the committed value
# 0x00:duty whatever TEMP held. Timer0 and Timer2 have 8-bit compare registers with
# no TEMP in the path, so this folds away entirely on their four channels.
#
# The two stores have to stay adjacent: a 16-bit timer access between them (from an
# ISR, say) would clobber TEMP again. That hazard is shared with _write16 in the
# servo HAL and is not addressed here.
@inline
def pwm_clear_ocr_high(pin: str):
    match pin:
        case "PB1":
            OCR1AH.value = 0
        case "PB2":
            OCR1BH.value = 0


# Compile-time pin -> OCR register pointer.
# The result is stored as self._ocr so set_duty() is a single register write.
@inline
def pwm_select_ocr(pin: str) -> ptr[uint8]:
    match pin:
        case "PD6":
            return OCR0A
        case "PD5":
            return OCR0B
        case "PB1":
            return OCR1AL
        case "PB2":
            return OCR1BL
        case "PB3":
            return OCR2A
        case "PD3":
            return OCR2B
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


# Compile-time pin -> TCCRxB register pointer (for start/stop).
@inline
def pwm_select_tccr_b(pin: str) -> ptr[uint8]:
    match pin:
        case "PD6" | "PD5":
            return TCCR0B
        case "PB1" | "PB2":
            return TCCR1B
        case "PB3" | "PD3":
            return TCCR2B
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


# Compile-time pin -> TCCRxB value that starts (enables) the PWM.
@inline
def pwm_select_start_val(pin: str) -> uint8:
    match pin:
        case "PD6" | "PD5":
            return 0x03
        case "PB1" | "PB2":
            return 0x0A
        case "PB3" | "PD3":
            return 0x04
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


@inline
def pwm_init(pin: str, duty: uint8, prescaler: uint8):
    # TCCRxA is shared by both channels of a timer: the COM bits are OR-ed in so
    # initializing OC1B does not silently disconnect an already-running OC1A
    # (Arduino's analogWrite on D9+D10 together froze D9 before this). The two
    # channels of one timer necessarily share WGM and prescaler.
    match pin:
        case "PD6":
            # Timer0 OC0A: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x83
            DDRD[6] = 1
            OCR0A.value = duty
            TCCR0A.value = TCCR0A.value | 0x83
            TCCR0B.value = prescaler
        case "PD5":
            # Timer0 OC0B: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x23
            DDRD[5] = 1
            OCR0B.value = duty
            TCCR0A.value = TCCR0A.value | 0x23
            TCCR0B.value = prescaler
        case "PB1":
            # Timer1 OC1A: Fast PWM 8-bit (WGM=0101), COM1A1=1
            DDRB[1] = 1
            OCR1AH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1AL.value = duty
            TCCR1A.value = TCCR1A.value | 0x81
            TCCR1B.value = prescaler
        case "PB2":
            # Timer1 OC1B: Fast PWM 8-bit, COM1B1=1
            DDRB[2] = 1
            OCR1BH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1BL.value = duty
            TCCR1A.value = TCCR1A.value | 0x21
            TCCR1B.value = prescaler
        case "PB3":
            # Timer2 OC2A: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x83
            DDRB[3] = 1
            OCR2A.value = duty
            TCCR2A.value = TCCR2A.value | 0x83
            TCCR2B.value = prescaler
        case "PD3":
            # Timer2 OC2B: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x23
            DDRD[3] = 1
            OCR2B.value = duty
            TCCR2A.value = TCCR2A.value | 0x23
            TCCR2B.value = prescaler
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")
    if duty == 0:
        pwm_disconnect(pin)


# duty 0 means off, and OCRx = BOTTOM is not off. In fast PWM the output is set
# at BOTTOM and cleared on compare match, so OCRx = 0 leaves a one-prescaled-clock
# pulse in every 256 -- about 0.4%, a visibly dim LED (ATmega328P 15.7.3). Off is
# the compare output disconnected and the pin driven low, which is also what
# Arduino's analogWrite(pin, 0) does. The other extreme needs nothing: OCRx = MAX
# holds the output constantly high, so duty 255 is already 100%.
#
# The COM bits live in TCCRxA, which the two channels of a timer share, so only
# this channel's pair is touched: OCxA is bits 7:6, OCxB is bits 5:4.
@inline
def pwm_disconnect(pin: str):
    match pin:
        case "PD6":
            TCCR0A.value = TCCR0A.value & 0x3F
            PORTD[6] = 0
        case "PD5":
            TCCR0A.value = TCCR0A.value & 0xCF
            PORTD[5] = 0
        case "PB1":
            TCCR1A.value = TCCR1A.value & 0x3F
            PORTB[1] = 0
        case "PB2":
            TCCR1A.value = TCCR1A.value & 0xCF
            PORTB[2] = 0
        case "PB3":
            TCCR2A.value = TCCR2A.value & 0x3F
            PORTB[3] = 0
        case "PD3":
            TCCR2A.value = TCCR2A.value & 0xCF
            PORTD[3] = 0
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


# Reconnect the compare output after a duty of 0 disconnected it. Non-inverting
# fast PWM is COMxA1 (bit 7) or COMxB1 (bit 5) with the low COM bit clear.
@inline
def pwm_connect(pin: str):
    match pin:
        case "PD6":
            TCCR0A.value = TCCR0A.value | 0x80
        case "PD5":
            TCCR0A.value = TCCR0A.value | 0x20
        case "PB1":
            TCCR1A.value = TCCR1A.value | 0x80
        case "PB2":
            TCCR1A.value = TCCR1A.value | 0x20
        case "PB3":
            TCCR2A.value = TCCR2A.value | 0x80
        case "PD3":
            TCCR2A.value = TCCR2A.value | 0x20
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")
