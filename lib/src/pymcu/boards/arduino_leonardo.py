# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# boards/arduino_leonardo.py -- Arduino Leonardo / Pro Micro pin name constants
#
# Maps the Arduino digital and analog pin numbers to ATmega32U4 port-pin names.
# Chip: ATmega32U4 at 16 MHz.
#
# Usage:
#   from pymcu.boards.arduino_leonardo import D13, A0, LED_BUILTIN
#   led = Pin(LED_BUILTIN, mode=Pin.OUT)
#

LED_BUILTIN: str = "PC7"   # Arduino Leonardo: pin 13, PC7

# Digital Pins D0-D13
D0:  str = "PD2"   # RX  (USART1 RXD)
D1:  str = "PD3"   # TX  (USART1 TXD)
D2:  str = "PD1"   # SDA
D3:  str = "PD0"   # SCL / PWM (OC0B)
D4:  str = "PD4"
D5:  str = "PC6"   # PWM (OC3A)
D6:  str = "PD7"   # PWM (OC4D)
D7:  str = "PE6"
D8:  str = "PB4"
D9:  str = "PB5"   # PWM (OC1A)
D10: str = "PB6"   # PWM (OC1B / OC4B)
D11: str = "PB7"   # PWM (OC0A / OC1C)
D12: str = "PD6"
D13: str = "PC7"   # LED_BUILTIN, PWM (OC4A)

# Analog Pins A0-A5 (same as D18-D23 on Leonardo)
A0: str = "PF7"
A1: str = "PF6"
A2: str = "PF5"
A3: str = "PF4"
A4: str = "PF1"
A5: str = "PF0"
