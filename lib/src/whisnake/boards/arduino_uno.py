# Arduino Uno board definitions for PyMCU
#
# Target chip: ATmega328P @ 16 MHz (arch = avr)
# Package: DIP-28 / TQFP-32
#
# Usage:
#   from whisnake.boards.arduino_uno import D2, D13, A0, LED_BUILTIN
#   from whisnake.hal.gpio import Pin
#   led = Pin(D13, Pin.OUT)
#
# This file is compiled into your firmware — it is NOT a runtime library.
# Using it with a chip other than atmega328p will fail during compilation
# because the pin strings ("PB5", "PD2", etc.) are only defined for that chip.
from whisnake.chips import __CHIP__

# Guard: emit a compile-time error if the target is not an AVR/ATmega328P.
# The ConditionalCompilator evaluates this match before IR generation, so
# non-AVR builds never see the pin definitions below.
match __CHIP__.arch:
    case "avr":
        pass
    case _:
        raise RuntimeError("arduino_uno board requires an AVR target (arch=avr)")

# ── Pin name constants ────────────────────────────────────────────────────────
# These are compile-time string constants resolved to chip register addresses.
# The compiler substitutes them wherever they are used as Pin() arguments.

# Built-in LED (digital pin 13 → PB5)
LED_BUILTIN = "PB5"

# ── Digital pins ─────────────────────────────────────────────────────────────
# PORTD: D0–D7
D0  = "PD0"   # RX  (UART)
D1  = "PD1"   # TX  (UART)
D2  = "PD2"   # INT0
D3  = "PD3"   # INT1 / PWM OC2B
D4  = "PD4"
D5  = "PD5"   # PWM OC0B
D6  = "PD6"   # PWM OC0A
D7  = "PD7"

# PORTB: D8–D13
D8  = "PB0"
D9  = "PB1"   # PWM OC1A
D10 = "PB2"   # SS  / PWM OC1B
D11 = "PB3"   # MOSI / PWM OC2A
D12 = "PB4"   # MISO
D13 = "PB5"   # SCK / LED

# ── Analog pins (also usable as GPIO) ─────────────────────────────────────────
# PORTC: A0–A5
A0  = "PC0"
A1  = "PC1"
A2  = "PC2"
A3  = "PC3"
A4  = "PC4"   # SDA (I2C / TWI)
A5  = "PC5"   # SCL (I2C / TWI)
