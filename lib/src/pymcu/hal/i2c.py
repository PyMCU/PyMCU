# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.i2c import I2C
elif __CHIP__.name == "rp2040":
    from pymcu.hal.rp2040.i2c import I2C
    # The transfer-result codes write_bytes/read_bytes/write_then_read answer
    # with -- busio.py's non-AVR I2C body translates them into the same
    # OSError CircuitPython raises for an unanswered address / any other abort.
    from pymcu.hal.rp2040.i2c import I2C_OK, I2C_ABRT_NODEV, I2C_ABRT_OTHER
elif __CHIP__.name == "rp2350":
    from pymcu.hal.rp2350.i2c import I2C
    from pymcu.hal.rp2350.i2c import I2C_OK, I2C_ABRT_NODEV, I2C_ABRT_OTHER
else:
    raise CompileError("I2C not supported on this architecture")
