# PyMCU -- cond-import-gpio
#
# Verifies that module-level conditional imports (if __CHIP__.name == ...)
# are resolved at compile time by ConditionalImportExtractor, so only the
# winning chip-specific module is loaded.
#
# The test uses pymcu.hal.avr.gpio which now uses module-level conditional
# imports. It blinks PB5 once and writes a sentinel byte to GPIOR0 (0x3E).
#
# Checkpoint: PORTB bit 5 goes high within 10 cycles (SBI PORTB,5).
#
from pymcu.hal.avr.gpio import Pin
from pymcu.types import asm

def main():
    led = Pin("PB5", Pin.OUT)
    led.high()
    asm("BREAK")
    while True:
        pass
