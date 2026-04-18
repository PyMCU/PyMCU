# PyMCU -- millis-micros: Timer0-based elapsed-time counter.
#
# Tests: millis_init() configures Timer0 (prescaler 64, normal mode),
#        millis() returns the overflow count (each overflow ~= 1 ms),
#        micros() approximates elapsed microseconds.
#
# Timer0 at 16 MHz, prescaler 64:
#   overflow period = 256 * 64 / 16_000_000 = 1024 us ~= 1 ms
#   Timer0 OVF vector: byte 0x0020 (word 0x0010)
#
# Test sequence:
#   1. Call millis_init() to start counting.
#   2. Busy-wait until millis() >= 10 (10 overflows ~= 10 ms).
#   3. BREAK: GPIOR0 = millis() low byte (should be >= 10).
#   4. Continue until millis() >= 50, then BREAK again.
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#
from pymcu.types import uint8, uint32, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1
from pymcu.time import millis_init, millis


def main():
    millis_init()

    # --- Checkpoint 1: wait until millis() >= 10 ---
    while millis() < 10:
        pass
    t1: uint32 = millis()
    GPIOR0.value = uint8(t1 & 0xFF)
    GPIOR1.value = uint8((t1 >> 8) & 0xFF)
    asm("BREAK")

    # --- Checkpoint 2: wait until millis() >= 50 ---
    while millis() < 50:
        pass
    t2: uint32 = millis()
    GPIOR0.value = uint8(t2 & 0xFF)
    GPIOR1.value = uint8((t2 >> 8) & 0xFF)
    asm("BREAK")

    while True:
        pass
