# LED bar drivers for the RTOS demo.
#
#   Bar 1  (VU meter)    -- PORTD bits 2-7  (Arduino D2-D7, 6 segments)
#   Bar 2  (Knight Rider)-- PORTC bits 0-5  (Arduino A0-A5, 6 segments)
#
# init_bars()           -- set DDR pins as outputs (call once before tasks start)
# set_level(val)        -- fill bar 1 proportionally to val (0-255)
# set_scanner(pos)      -- light a single segment on bar 2 at position 0-5
# get_scanner_pattern(pos) -> uint8  -- return the bitmask for a given position
from pymcu.types import uint8
from pymcu.chips.atmega328p import DDRC, DDRD, PORTC, PORTD


def init_bars():
    # Configure bar pins as outputs (bits 2-7 on PORTD, bits 0-5 on PORTC).
    DDRD.value = 0xFC
    DDRC.value = 0x3F


# Level patterns for bar 1: 0 to 6 segments lit on PD2-PD7.
# PD2 = bit 2 (0x04), PD3 = bit 3 (0x08), ..., PD7 = bit 7 (0x80).
def set_level(val: uint8):
    # Map val (0-255) to 0-6 lit segments using the top 3 bits (val >> 5).
    level: uint8 = val >> 5   # 0 .. 7; clamp to 6 for 6-segment bar
    if level > 6:
        level = 6
    if level == 0:
        PORTD.value = 0x00
    elif level == 1:
        PORTD.value = 0x04
    elif level == 2:
        PORTD.value = 0x0C
    elif level == 3:
        PORTD.value = 0x1C
    elif level == 4:
        PORTD.value = 0x3C
    elif level == 5:
        PORTD.value = 0x7C
    else:
        PORTD.value = 0xFC


# Scanner pattern lookup: one lit segment per position on PC0-PC5.
def get_scanner_pattern(pos: uint8) -> uint8:
    if pos == 0:
        return 0x01
    elif pos == 1:
        return 0x02
    elif pos == 2:
        return 0x04
    elif pos == 3:
        return 0x08
    elif pos == 4:
        return 0x10
    else:
        return 0x20


def set_scanner(pattern: uint8):
    PORTC.value = pattern
