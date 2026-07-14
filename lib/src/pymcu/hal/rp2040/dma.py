# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 DMA HAL -- pymcu.hal.rp2040.dma
#
# Channel-0 unpaced memory-to-memory copy. Uses the fixed channel-0 register
# pointers so every access folds to a constant-address volatile store.

from pymcu.chips.rp2040 import (
    DMA_CH0_READ_ADDR, DMA_CH0_WRITE_ADDR, DMA_CH0_TRANS_COUNT,
    DMA_CH0_CTRL_TRIG, DMA_CTRL_BUSY, DMA_CTRL_TREQ_SHIFT,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_DMA,
)
from pymcu.types import uint8, uint32, const, inline


class DMA:
    """DMA channel 0 (memory-to-memory), zero-cost abstraction."""

    def __init__(self, channel: const[uint8] = 0):
        RESETS_RESET_CLR.value = 1 << RESET_DMA
        while (RESETS_RESET_DONE.value & (1 << RESET_DMA)) == 0:
            pass

    @inline
    def transfer(self, src: uint32, dst: uint32, count: uint32):
        # Program the channel and trigger an unpaced 32-bit-word copy.
        DMA_CH0_READ_ADDR.value = src
        DMA_CH0_WRITE_ADDR.value = dst
        DMA_CH0_TRANS_COUNT.value = count
        # CTRL_TRIG: EN | DATA_SIZE=word(2<<2) | INCR_READ | INCR_WRITE |
        # CHAIN_TO=0 (self) | TREQ_SEL=0x3F (permanent). Writing it starts the copy.
        DMA_CH0_CTRL_TRIG.value = 1 | (2 << 2) | (1 << 4) | (1 << 5) | (0x3F << DMA_CTRL_TREQ_SHIFT)
        # Wait for completion (BUSY clears).
        while (DMA_CH0_CTRL_TRIG.value >> DMA_CTRL_BUSY) & 1:
            pass
