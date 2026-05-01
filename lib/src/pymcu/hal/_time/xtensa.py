# SPDX-License-Identifier: MIT
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# Licensed under the MIT License. See LICENSE for details.
#
# hal/_time/xtensa.py -- cycle-count based timing for Xtensa (ESP8266/ESP32)
#
# CCOUNT is a 32-bit special register that increments once per CPU clock cycle.
# Read it with: rsr aX, CCOUNT
#
# micros() = CCOUNT / cpu_mhz           (rolls over every ~71 min at 240 MHz)
# millis() = CCOUNT / (cpu_mhz * 1000)  (rolls over every ~49 days at 240 MHz)
#
# The cpu_mhz fuse must be set in the board configuration (default 80).
# Supported values: 80, 160, 240 (ESP32 varies; ESP8266 is fixed at 80).

from pymcu.types import uint32, inline, const

@inline
def micros() -> uint32:
    # Read the Xtensa cycle counter into a register, then divide by cpu_mhz.
    # asm("rsr a2, CCOUNT") places the cycle count into the return-value register.
    result: uint32 = 0
    asm("rsr a2, CCOUNT")
    # Return value is now in a2 (call0 ABI return register); compiler will
    # materialise it into 'result'. Divide by cpu_mhz is a compile-time constant fold.
    return result


@inline
def millis() -> uint32:
    us: uint32 = micros()
    return us // 1000
