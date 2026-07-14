# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 I2C HAL -- pymcu.hal.rp2040.i2c (I2C0, Synopsys DW_apb_i2c master)
#
# Fixed-address MMIO, so every access folds to a volatile load/store. Default
# pins are SDA=GP4, SCL=GP5 (the I2C0 function group), enabled with pull-ups.

from pymcu.chips.rp2040 import (
    I2C0_IC_CON, I2C0_IC_TAR, I2C0_IC_DATA_CMD, I2C0_IC_ENABLE,
    I2C0_IC_STATUS, I2C0_IC_TXFLR,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_I2C0,
    RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, PADS_BANK0_BASE, GPIO_FUNC_I2C,
    I2C_CMD_STOP, I2C_STATUS_TFNF, I2C_STATUS_TFE,
)
from pymcu.types import ptr, uint8, uint32, const, inline


class I2C:
    """Hardware I2C0 (DW_apb_i2c) master, zero-cost abstraction."""

    def __init__(self, freq: const = 100000, sda: const = 4, scl: const = 5):
        # Bring I2C0, IO_BANK0 and PADS_BANK0 out of reset.
        reset_mask: uint32 = (1 << RESET_I2C0) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        # Disable to configure, set up master mode, then re-enable.
        I2C0_IC_ENABLE.value = 0
        # MASTER_MODE[0]=1, SPEED[2:1]=1 (standard), RESTART_EN[5]=1, SLAVE_DISABLE[6]=1.
        I2C0_IC_CON.value = (1 << 0) | (1 << 1) | (1 << 5) | (1 << 6)
        I2C0_IC_ENABLE.value = 1

        # Route SDA/SCL to the I2C function with internal pull-ups (pad bit3 = PUE).
        sda_pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * sda)
        sda_pad.value = (1 << 6) | (1 << 3)
        scl_pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * scl)
        scl_pad.value = (1 << 6) | (1 << 3)
        sda_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * sda + 4)
        sda_ctrl.value = GPIO_FUNC_I2C
        scl_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * scl + 4)
        scl_ctrl.value = GPIO_FUNC_I2C

    @inline
    def _set_target(self, addr: uint8):
        # IC_TAR can only be changed while the block is disabled.
        I2C0_IC_ENABLE.value = 0
        I2C0_IC_TAR.value = addr
        I2C0_IC_ENABLE.value = 1

    @inline
    def write_to(self, addr: uint8, data: uint8):
        # Single-byte write with a STOP condition.
        self._set_target(addr)
        while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
            pass
        I2C0_IC_DATA_CMD.value = data | I2C_CMD_STOP
        # Wait for the byte to drain so the transfer completes.
        while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFE) & 1) == 0:
            pass

    @inline
    def write_bytes(self, addr: uint8, data: bytearray, n: uint8):
        # Multi-byte write; STOP is asserted with the final byte.
        self._set_target(addr)
        i: uint8 = 0
        while i < n:
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
                pass
            cmd: uint32 = data[i]
            if i == n - 1:
                cmd = cmd | I2C_CMD_STOP
            I2C0_IC_DATA_CMD.value = cmd
            i = i + 1
        while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFE) & 1) == 0:
            pass
