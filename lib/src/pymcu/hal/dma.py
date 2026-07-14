# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.name == "rp2040":
    from pymcu.hal.rp2040.dma import DMA
elif __CHIP__.name == "rp2350":
    from pymcu.hal.rp2350.dma import DMA
else:
    raise CompileError("DMA not supported on this architecture")
