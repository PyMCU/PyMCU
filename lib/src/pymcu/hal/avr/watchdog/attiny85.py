from pymcu.chips.attiny85 import WDTCR
from pymcu.types import uint8, uint16, inline, const, asm

# ATtiny85 Watchdog Timer HAL
#
# WDTCR at I/O 0x21 (data 0x41) -- accessible via OUT/IN (I/O < 0x40).
#
# WDTCR bit layout (identical to ATmega328P WDTCSR):
#   bit 7: WDIF  -- Watchdog Interrupt Flag
#   bit 6: WDIE  -- Watchdog Interrupt Enable
#   bit 5: WDP3  -- Prescaler bit 3
#   bit 4: WDCE  -- Watchdog Change Enable (timed window)
#   bit 3: WDE   -- Watchdog System Reset Enable
#   bit 2: WDP2  |
#   bit 1: WDP1  | Prescaler bits [2:0]
#   bit 0: WDP0  |
#
# Prescaler table (WDP[3:0]):
#   0000 = ~16ms    0001 = ~32ms    0010 = ~64ms    0011 = ~125ms
#   0100 = ~250ms   0101 = ~500ms   0110 = ~1s      0111 = ~2s
#   1000 = ~4s      1001 = ~8s
#
# Timed write sequence (OUT is 1 cycle; window allows 4 cycles between steps):
#   1. CLI
#   2. WDR
#   3. OUT 0x21, (WDCE=1 | WDE=1)
#   4. OUT 0x21, (WDE=1 | WDP[2:0] | WDP3<<5)  -- within 4 cycles of step 3

@inline
def wdt_enable(wdp: uint8):
    asm("cli")
    asm("wdr")
    if wdp == 0:
        asm("ldi r17, 0x08")   # WDE | WDP=0000 -> ~16ms
    elif wdp == 1:
        asm("ldi r17, 0x09")   # WDE | WDP0 -> ~32ms
    elif wdp == 2:
        asm("ldi r17, 0x0a")   # WDE | WDP1 -> ~64ms
    elif wdp == 3:
        asm("ldi r17, 0x0b")   # WDE | WDP1 | WDP0 -> ~125ms
    elif wdp == 4:
        asm("ldi r17, 0x0c")   # WDE | WDP2 -> ~250ms
    elif wdp == 5:
        asm("ldi r17, 0x0d")   # WDE | WDP2 | WDP0 -> ~500ms
    elif wdp == 6:
        asm("ldi r17, 0x0e")   # WDE | WDP2 | WDP1 -> ~1s
    elif wdp == 7:
        asm("ldi r17, 0x0f")   # WDE | WDP2 | WDP1 | WDP0 -> ~2s
    elif wdp == 8:
        asm("ldi r17, 0x28")   # WDE | WDP3 -> ~4s
    elif wdp == 9:
        asm("ldi r17, 0x29")   # WDE | WDP3 | WDP0 -> ~8s
    asm("ldi r16, 0x18")       # WDCE=1, WDE=1 (change enable)
    asm("out 0x21, r16")       # WDTCR = WDCE|WDE  (step 3)
    asm("out 0x21, r17")       # WDTCR = WDE|WDP   (step 4, within 4 cycles)
    asm("sei")

@inline
def wdt_disable():
    asm("cli")
    asm("wdr")
    asm("ldi r16, 0x18")   # WDCE=1, WDE=1
    asm("out 0x21, r16")   # WDTCR = WDCE|WDE
    asm("ldi r16, 0x00")   # disabled
    asm("out 0x21, r16")   # WDTCR = 0
    asm("sei")

@inline
def wdt_feed():
    asm("wdr")

@inline
def wdt_timeout_wdp(timeout_ms: uint16) -> uint8:
    if timeout_ms <= 16:
        return 0
    elif timeout_ms <= 32:
        return 1
    elif timeout_ms <= 64:
        return 2
    elif timeout_ms <= 125:
        return 3
    elif timeout_ms <= 250:
        return 4
    elif timeout_ms <= 500:
        return 5
    elif timeout_ms <= 1000:
        return 6
    elif timeout_ms <= 2000:
        return 7
    elif timeout_ms <= 4000:
        return 8
    return 9


@inline
def wdt_arm_rt(timeout_ms: uint16):
    # Arm the watchdog in reset mode from a RUNTIME timeout (ms). A plain
    # runtime if/elif picks the prescaler bucket and each branch calls
    # wdt_enable() with a LITERAL, so the timed asm sequence stays const-folded
    # and straight-line (the verified path) -- only the bucket choice is runtime.
    if timeout_ms <= 16:
        wdt_enable(0)
    elif timeout_ms <= 32:
        wdt_enable(1)
    elif timeout_ms <= 64:
        wdt_enable(2)
    elif timeout_ms <= 125:
        wdt_enable(3)
    elif timeout_ms <= 250:
        wdt_enable(4)
    elif timeout_ms <= 500:
        wdt_enable(5)
    elif timeout_ms <= 1000:
        wdt_enable(6)
    elif timeout_ms <= 2000:
        wdt_enable(7)
    elif timeout_ms <= 4000:
        wdt_enable(8)
    else:
        wdt_enable(9)
