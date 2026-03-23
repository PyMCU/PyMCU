from whipsnake.chips.atmega328p import WDTCSR
from whipsnake.types import uint8, uint16, inline, const

# ATmega328P Watchdog Timer (WDT)
# WDTCSR at DATA 0x60 (above 0x5F -- uses LDS/STS, not IN/OUT)
#
# WDTCSR bits:
#   bit 7: WDIF  -- Watchdog Interrupt Flag (clear by writing 1)
#   bit 6: WDIE  -- Watchdog Interrupt Enable
#   bit 5: WDP3  -- Prescaler bit 3
#   bit 4: WDCE  -- Watchdog Change Enable (must be 1 to change WDE/WDP)
#   bit 3: WDE   -- Watchdog System Reset Enable
#   bit 2: WDP2  \
#   bit 1: WDP1   > Prescaler bits [2:0]
#   bit 0: WDP0  /
#
# Prescaler (WDP[3:0]):
#   0000 = ~16ms    0001 = ~32ms    0010 = ~64ms    0011 = ~125ms
#   0100 = ~250ms   0101 = ~500ms   0110 = ~1s      0111 = ~2s
#   1000 = ~4s      1001 = ~8s
#
# Timed write sequence:
#   1. CLI (disable interrupts)
#   2. WDR (reset WDT)
#   3. Write WDCE=1, WDE=1 to WDTCSR
#   4. Within 4 cycles: write new WDTCSR value with WDE and prescaler
#   5. SEI (re-enable interrupts)
#
# Since WDTCSR > 0x5F, use STS (2 cycles) instead of OUT.
# Step 3->4 must fit in 4 cycles: STS(2) + LDI(1) + STS(2) = 5 cycles -- too many!
# Solution: preload the final value into a register BEFORE step 3, then:
#   STS WDTCSR, r_enable  (step 3: 2 cycles)
#   STS WDTCSR, r_final   (step 4: 2 cycles)  -- 2 cycles after step 3 = OK
from whipsnake.types import asm


@inline
def wdt_enable(wdp: const[uint8]):
    # wdp = WDP[3:0] value (0-9). WDE=1 (reset mode).
    # Final WDTCSR = WDE | WDP[2:0] | (WDP3 << 5)
    # Example: wdp=5 (500ms) -> WDP[3:0]=0101 -> WDP3=0,WDP2=1,WDP0=1 -> 0x0D
    # We use asm() for the timed write sequence.
    asm("cli")
    asm("wdr")
    if wdp == 0:
        asm("ldi r17, 0x08")   # WDE only, WDP=0000 -> ~16ms
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
        asm("ldi r17, 0x28")   # WDE | WDP3 -> ~4s  (WDP3=bit5)
    elif wdp == 9:
        asm("ldi r17, 0x29")   # WDE | WDP3 | WDP0 -> ~8s
    # Timed sequence: preload r17 already done above
    # WDTCSR address = 0x60; use STS
    asm("ldi r16, 0x18")       # WDCE=1, WDE=1 (change enable)
    asm("sts 0x60, r16")       # WDTCSR = WDCE|WDE  (step 3)
    asm("sts 0x60, r17")       # WDTCSR = WDE|WDP   (step 4, within 4 cycles)
    asm("sei")

@inline
def wdt_disable():
    asm("cli")
    asm("wdr")
    asm("ldi r16, 0x18")   # WDCE=1, WDE=1
    asm("sts 0x60, r16")   # WDTCSR = WDCE|WDE
    asm("ldi r16, 0x00")   # WDE=0, WDP=0 (disabled)
    asm("sts 0x60, r16")   # WDTCSR = 0 (within 4 cycles)
    asm("sei")

@inline
def wdt_feed():
    # Reset the watchdog timer counter (WDR instruction)
    asm("wdr")

@inline
def wdt_timeout_wdp(timeout_ms: const[uint16]) -> uint8:
    # Map a timeout in ms to the WDP[3:0] prescaler code.
    # Returns the closest WDP value >= the requested timeout.
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
    return 9  # 8000ms (max)
