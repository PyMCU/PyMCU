from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A, OCR0B
from pymcu.chips.atmega328p import TCCR1A, TCCR1B, OCR1AL, OCR1BL, OCR1AH, OCR1BH
from pymcu.chips.atmega328p import TCCR2A, TCCR2B, OCR2A, OCR2B
from pymcu.chips.atmega328p import DDRD, DDRB, PORTD, PORTB
from pymcu.chips import __TIMEBASE__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, ptr, const, claim


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
def pwm_prescaler_for_freq(pin: const, freq: uint16) -> uint8:
    match pin:
        case "PD6" | "PD5":
            # Timer0 is also the millisecond time base (millis(), ticks_ms(),
            # monotonic(), asyncio), which runs it at prescaler 64 and counts its
            # overflows. Any other prescaler here runs that clock at the wrong rate:
            # measured on an Arduino Uno, 5000 Hz on PD6 made monotonic() run 8.44
            # times too fast (PyMCU#295). So with the time base in the program the
            # only frequency on these two pins is the 976 Hz bucket, and the request
            # is refused where it is written. A run-time frequency cannot be checked
            # here and is refused too.
            if __TIMEBASE__ and (freq <= 488 or freq > 2762):
                raise CompileError(
                    "PWM: PD5/PD6 (Arduino D5/D6) share Timer0 with the millisecond time "
                    "base (millis, ticks_ms, monotonic, asyncio), which fixes its prescaler "
                    "at 64: the only PWM frequency on these pins is then 976 Hz (any "
                    "request between 489 and 2762 Hz, or the default). For this frequency "
                    "use PD3/PB3 (D3/D11, Timer2) or PB1/PB2 (D9/D10, Timer1).")
            # Timer0: CS[2:0] = 001/010/011/100/101. Thresholds are the geometric
            # midpoints between achievable frequencies: the chosen prescaler is
            # always the nearest one.
            if freq > 22097:
                pwm_claim_prescaler(pin, 0x01)
                return 0x01
            elif freq > 2762:
                pwm_claim_prescaler(pin, 0x02)
                return 0x02
            elif freq > 488:
                pwm_claim_prescaler(pin, 0x03)
                return 0x03
            elif freq > 122:
                pwm_claim_prescaler(pin, 0x04)
                return 0x04
            else:
                pwm_claim_prescaler(pin, 0x05)
                return 0x05
        case "PB1" | "PB2":
            # Timer1 Fast PWM 8-bit: WGM12 must stay set (bit3); CS in bits 2:0
            if freq > 22097:
                pwm_claim_prescaler(pin, 0x09)
                return 0x09
            elif freq > 2762:
                pwm_claim_prescaler(pin, 0x0A)
                return 0x0A
            elif freq > 488:
                pwm_claim_prescaler(pin, 0x0B)
                return 0x0B
            elif freq > 122:
                pwm_claim_prescaler(pin, 0x0C)
                return 0x0C
            else:
                pwm_claim_prescaler(pin, 0x0D)
                return 0x0D
        case "PB3" | "PD3":
            # Timer2: CS encoding 001(1) 010(8) 011(32) 100(64) 101(128) 110(256)
            # 111(1024) -- unlike Timer0/1 it also has /32 and /128, so it gets
            # 1953 Hz and 488 Hz buckets the other timers cannot reach.
            if freq > 22097:
                pwm_claim_prescaler(pin, 0x01)
                return 0x01
            elif freq > 3906:
                pwm_claim_prescaler(pin, 0x02)
                return 0x02
            elif freq > 1381:
                pwm_claim_prescaler(pin, 0x03)
                return 0x03
            elif freq > 690:
                pwm_claim_prescaler(pin, 0x04)
                return 0x04
            elif freq > 345:
                pwm_claim_prescaler(pin, 0x05)
                return 0x05
            elif freq > 122:
                pwm_claim_prescaler(pin, 0x06)
                return 0x06
            else:
                pwm_claim_prescaler(pin, 0x07)
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
def pwm_clear_ocr_high(pin: const):
    match pin:
        case "PB1":
            OCR1AH.value = 0
        case "PB2":
            OCR1BH.value = 0


# Compile-time pin -> OCR register pointer.
# The result is stored as self._ocr so set_duty() is a single register write.
@inline
def pwm_select_ocr(pin: const) -> ptr[uint8]:
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
def pwm_select_tccr_b(pin: const) -> ptr[uint8]:
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
def pwm_select_start_val(pin: const) -> uint8:
    match pin:
        case "PD6" | "PD5":
            pwm_claim_prescaler(pin, 0x03)
            return 0x03
        case "PB1" | "PB2":
            pwm_claim_prescaler(pin, 0x0A)
            return 0x0A
        case "PB3" | "PD3":
            pwm_claim_prescaler(pin, 0x04)
            return 0x04
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


@inline
def pwm_init_raw(pin: const, ocr: uint8, off: uint8, prescaler: uint8, invert: const[uint8] = 0):
    # TCCRxA is shared by both channels of a timer: the COM bits are OR-ed in so
    # initializing OC1B does not silently disconnect an already-running OC1A
    # (Arduino's analogWrite on D9+D10 together froze D9 before this). The two
    # channels of one timer necessarily share WGM and prescaler.
    match pin:
        case "PD6":
            # Timer0 OC0A: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x83
            DDRD[6] = 1
            OCR0A.value = ocr
            TCCR0A.value = TCCR0A.value | (0xC3 if invert else 0x83)
            TCCR0B.value = prescaler
        case "PD5":
            # Timer0 OC0B: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x23
            DDRD[5] = 1
            OCR0B.value = ocr
            TCCR0A.value = TCCR0A.value | (0x33 if invert else 0x23)
            TCCR0B.value = prescaler
        case "PB1":
            # Timer1 OC1A: Fast PWM 8-bit (WGM=0101), COM1A1=1
            DDRB[1] = 1
            OCR1AH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1AL.value = ocr
            TCCR1A.value = TCCR1A.value | (0xC1 if invert else 0x81)
            TCCR1B.value = prescaler
        case "PB2":
            # Timer1 OC1B: Fast PWM 8-bit, COM1B1=1
            DDRB[2] = 1
            OCR1BH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1BL.value = ocr
            TCCR1A.value = TCCR1A.value | (0x31 if invert else 0x21)
            TCCR1B.value = prescaler
        case "PB3":
            # Timer2 OC2A: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x83
            DDRB[3] = 1
            OCR2A.value = ocr
            TCCR2A.value = TCCR2A.value | (0xC3 if invert else 0x83)
            TCCR2B.value = prescaler
        case "PD3":
            # Timer2 OC2B: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x23
            DDRD[3] = 1
            OCR2B.value = ocr
            TCCR2A.value = TCCR2A.value | (0x33 if invert else 0x23)
            TCCR2B.value = prescaler
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")
    if off:
        pwm_disconnect(pin)


# The 8-bit entry every HAL user has: duty 0 is off, 255 is fully on, and a value in
# between is high for duty + 1 of 256 counts (fast PWM sets at BOTTOM and clears on the
# match, inclusive). Written out rather than routed through pwm_init_raw with a flag:
# a run-time duty then computed the flag and branched on it twice (+30 bytes on the
# duty-zero fixture). The exact 16-bit path is pwm_u16_steps + pwm_init_raw.
@inline
def pwm_init(pin: const, duty: uint8, prescaler: uint8, invert: const[uint8] = 0):
    # TCCRxA is shared by both channels of a timer: the COM bits are OR-ed in so
    # initializing OC1B does not silently disconnect an already-running OC1A
    # (Arduino's analogWrite on D9+D10 together froze D9 before this). The two
    # channels of one timer necessarily share WGM and prescaler.
    match pin:
        case "PD6":
            # Timer0 OC0A: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x83
            DDRD[6] = 1
            OCR0A.value = duty
            TCCR0A.value = TCCR0A.value | (0xC3 if invert else 0x83)
            TCCR0B.value = prescaler
        case "PD5":
            # Timer0 OC0B: Fast PWM non-inverting, WGM01:00=11 -> TCCR0A=0x23
            DDRD[5] = 1
            OCR0B.value = duty
            TCCR0A.value = TCCR0A.value | (0x33 if invert else 0x23)
            TCCR0B.value = prescaler
        case "PB1":
            # Timer1 OC1A: Fast PWM 8-bit (WGM=0101), COM1A1=1
            DDRB[1] = 1
            OCR1AH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1AL.value = duty
            TCCR1A.value = TCCR1A.value | (0xC1 if invert else 0x81)
            TCCR1B.value = prescaler
        case "PB2":
            # Timer1 OC1B: Fast PWM 8-bit, COM1B1=1
            DDRB[2] = 1
            OCR1BH.value = 0          # see pwm_clear_ocr_high: TEMP commits with the low byte
            OCR1BL.value = duty
            TCCR1A.value = TCCR1A.value | (0x31 if invert else 0x21)
            TCCR1B.value = prescaler
        case "PB3":
            # Timer2 OC2A: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x83
            DDRB[3] = 1
            OCR2A.value = duty
            TCCR2A.value = TCCR2A.value | (0xC3 if invert else 0x83)
            TCCR2B.value = prescaler
        case "PD3":
            # Timer2 OC2B: Fast PWM non-inverting, WGM21:20=11 -> TCCR2A=0x23
            DDRD[3] = 1
            OCR2B.value = duty
            TCCR2A.value = TCCR2A.value | (0x33 if invert else 0x23)
            TCCR2B.value = prescaler
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")
    if duty == 0:
        pwm_disconnect(pin)


# A 16-bit duty (0..65535 = 0..100 %, what CircuitPython and MicroPython speak) as the
# number of counts the pin is high per period on this channel, 0..256 on the 8-bit
# channels: round(duty * 256 / 65535). The compare register then holds steps - 1, since
# fast PWM is high for OCR + 1 counts, and 0 steps means off. Measured before this
# existed: every duty came out 1/256 above what was asked, 50.4 % for 32768
# (pymcu-circuitpython#30). Rounding without a 16-bit overflow: the high byte plus the
# top bit of the low byte.
@inline
def pwm_u16_steps(pin: const, duty_u16: uint16) -> uint16:
    match pin:
        case _:
            return (duty_u16 >> 8) + ((duty_u16 >> 7) & 1)



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
def pwm_disconnect(pin: const):
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
def pwm_connect(pin: const, invert: const[uint8] = 0):
    match pin:
        case "PD6":
            TCCR0A.value = TCCR0A.value | (0xC0 if invert else 0x80)
        case "PD5":
            TCCR0A.value = TCCR0A.value | (0x30 if invert else 0x20)
        case "PB1":
            TCCR1A.value = TCCR1A.value | (0xC0 if invert else 0x80)
        case "PB2":
            TCCR1A.value = TCCR1A.value | (0x30 if invert else 0x20)
        case "PB3":
            TCCR2A.value = TCCR2A.value | (0xC0 if invert else 0x80)
        case "PD3":
            TCCR2A.value = TCCR2A.value | (0x30 if invert else 0x20)
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")


# deinit(): the pin back to an input, no pull-up. pwm_disconnect has already taken the
# compare output off the pin and cleared the port bit, so clearing the direction bit is
# all that is left; leaving it an output is how a released pin kept driving a load.
@inline
def pwm_release(pin: const):
    match pin:
        case "PD6":
            DDRD[6] = 0
        case "PD5":
            DDRD[5] = 0
        case "PB1":
            DDRB[1] = 0
        case "PB2":
            DDRB[2] = 0
        case "PB3":
            DDRB[3] = 0
        case "PD3":
            DDRD[3] = 0
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")




# The two channels of one timer share its prescaler: whoever writes TCCRxB last sets the
# frequency of both. Measured on an Arduino Uno: PWM("PD5", 128, 5000) then
# PWM("PD6", 128, 100) left both pins at 61 Hz with nothing said (PyMCU#300). The PWM class
# claims the prescaler it is about to program under the timer's key, and the compiler
# refuses a second channel asking for another one, where it is written. A run-time
# prescaler has nothing to claim and goes through. Emits nothing.
@inline
def pwm_claim_prescaler(pin: const, code: uint8):
    match pin:
        case "PD6" | "PD5":
            claim("Timer0 prescaler (PD5 and PD6 share it)", code, pin,
                  "The two channels of one timer run at one frequency. Timer0 codes: 1 = 62500 Hz, "
                  "2 = 7812 Hz, 3 = 976 Hz, 4 = 244 Hz, 5 = 61 Hz. Ask both for the same "
                  "frequency, or move one to PB1/PB2 (Timer1) or PB3/PD3 (Timer2)")
        case "PB1" | "PB2":
            claim("Timer1 prescaler (PB1 and PB2 share it)", code, pin,
                  "The two channels of one timer run at one frequency. Timer1 codes: 9 = 62500 Hz, "
                  "10 = 7812 Hz, 11 = 976 Hz, 12 = 244 Hz, 13 = 61 Hz. Ask both for the same "
                  "frequency, or move one to PD5/PD6 (Timer0) or PB3/PD3 (Timer2)")
        case "PB3" | "PD3":
            claim("Timer2 prescaler (PB3 and PD3 share it)", code, pin,
                  "The two channels of one timer run at one frequency. Timer2 codes: 1 = 62500 Hz, "
                  "2 = 7812 Hz, 3 = 1953 Hz, 4 = 976 Hz, 5 = 488 Hz, 6 = 244 Hz, 7 = 61 Hz. Ask "
                  "both for the same frequency, or move one to PD5/PD6 (Timer0) or PB1/PB2 (Timer1)")
        case _:
            raise CompileError("PWM: unsupported pin -- use PD6, PD5 (Timer0), PB1, PB2 (Timer1) or PB3, PD3 (Timer2)")
