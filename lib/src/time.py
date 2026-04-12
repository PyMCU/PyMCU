# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# time.py -- MicroPython-compatible time module alias
#
# Maps the standard MicroPython `import time` interface to pymcu.time.
# This allows code written for MicroPython to compile unchanged on pymcuc.
#
# Supported:
#   time.sleep_ms(ms)  -> delay_ms(ms)
#   time.delay_ms(ms)  -> delay_ms(ms)   (direct alias)

from pymcu.time import delay_ms

def sleep_ms(ms):
    delay_ms(ms)
