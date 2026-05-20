# Arduino Mega 2560 board definitions for PyMCU
#
# Target chip: ATmega2560 @ 16 MHz (arch = avr)
# Package: TQFP-100
#
# Usage:
#   from pymcu.boards.arduino_mega import D2, D13, A0, LED_BUILTIN
#   from pymcu.hal.gpio import Pin
#   led = Pin(D13, Pin.OUT)
#
# This file is compiled into your firmware -- it is NOT a runtime library.
from pymcu.chips import __CHIP__

match __CHIP__.arch:
    case "avr":
        pass
    case _:
        raise RuntimeError("arduino_mega board requires an AVR target (arch=avr)")

# ==========================================
#  Pin name constants
# ==========================================

# Built-in LED (digital pin 13 -> PB7)
LED_BUILTIN = "PB7"

# ==========================================
#  Digital pins D0-D13
# ==========================================

# PORTE
D0  = "PE0"   # RX0 (USART0)
D1  = "PE1"   # TX0 (USART0)
D2  = "PE4"   # INT4
D3  = "PE5"   # INT5 / PWM OC3C
D4  = "PG5"   # PWM OC0B
D5  = "PE3"   # PWM OC3A
D6  = "PH3"   # PWM OC4A
D7  = "PH4"   # PWM OC4B
D8  = "PH5"   # PWM OC4C
D9  = "PH6"   # PWM OC2B

# PORTB
D10 = "PB4"   # PWM OC2A
D11 = "PB5"   # MOSI / PWM OC1A
D12 = "PB6"   # MISO / PWM OC1B
D13 = "PB7"   # SCK / LED / PWM OC0A

# PORTJ
D14 = "PJ1"   # TX3 (USART3)
D15 = "PJ0"   # RX3 (USART3)

# PORTH
D16 = "PH1"   # TX2 (USART2)
D17 = "PH0"   # RX2 (USART2)

# PORTD
D18 = "PD3"   # TX1 (USART1) / INT3
D19 = "PD2"   # RX1 (USART1) / INT2
D20 = "PD1"   # SDA (TWI) / INT1
D21 = "PD0"   # SCL (TWI) / INT0

# PORTA
D22 = "PA0"
D23 = "PA1"
D24 = "PA2"
D25 = "PA3"
D26 = "PA4"
D27 = "PA5"
D28 = "PA6"
D29 = "PA7"

# PORTC
D30 = "PC7"
D31 = "PC6"
D32 = "PC5"
D33 = "PC4"
D34 = "PC3"
D35 = "PC2"
D36 = "PC1"
D37 = "PC0"

# PORTD
D38 = "PD7"

# PORTG
D39 = "PG2"
D40 = "PG1"
D41 = "PG0"

# PORTL
D42 = "PL7"
D43 = "PL6"
D44 = "PL5"   # PWM OC5C
D45 = "PL4"   # PWM OC5B
D46 = "PL3"   # PWM OC5A
D47 = "PL2"
D48 = "PL1"
D49 = "PL0"

# PORTB
D50 = "PB3"   # MISO
D51 = "PB2"   # MOSI
D52 = "PB1"   # SCK
D53 = "PB0"   # SS

# ==========================================
#  Analog pins A0-A15
# ==========================================

# PORTF: A0-A7 (ADC0-ADC7)
A0  = "PF0"
A1  = "PF1"
A2  = "PF2"
A3  = "PF3"
A4  = "PF4"
A5  = "PF5"
A6  = "PF6"
A7  = "PF7"

# PORTK: A8-A15 (ADC8-ADC15)
A8  = "PK0"
A9  = "PK1"
A10 = "PK2"
A11 = "PK3"
A12 = "PK4"
A13 = "PK5"
A14 = "PK6"
A15 = "PK7"
