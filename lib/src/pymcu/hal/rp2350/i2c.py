# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2350 I2C HAL -- pymcu.hal.rp2350.i2c (I2C0, Synopsys DW_apb_i2c master)
#
# Fixed-address MMIO, so every access folds to a volatile load/store. Default
# pins are SDA=GP4, SCL=GP5 (the I2C0 function group), enabled with pull-ups.

from pymcu.chips.rp2350 import (
    I2C0_IC_CON, I2C0_IC_TAR, I2C0_IC_DATA_CMD, I2C0_IC_ENABLE,
    I2C0_IC_STATUS, I2C0_IC_TXFLR,
    I2C0_IC_CLR_TX_ABRT, I2C0_IC_TX_ABRT_SOURCE, I2C0_IC_RAW_INTR_STAT,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_I2C0,
    RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, PADS_BANK0_BASE, GPIO_FUNC_I2C,
    I2C_CMD_READ, I2C_CMD_STOP, I2C_CMD_RESTART,
    I2C_STATUS_TFNF, I2C_STATUS_TFE, I2C_STATUS_RFNE,
    I2C_RAW_INTR_TX_ABRT, I2C_ABRT_7B_ADDR_NOACK,
)
from pymcu.types import ptr, uint8, uint16, uint32, const, inline

# write_bytes/read_bytes/write_then_read return one of these, which busio.py
# maps straight onto the OSError CircuitPython's own common-hal raises for
# the same two faults (adafruit_bus_device.I2CDevice is what actually catches
# it: "No I2C device at address").
I2C_OK          = 0
I2C_ABRT_NODEV  = 1   # the target address went unanswered -- no device there
I2C_ABRT_OTHER  = 2   # any other abort (a data byte NACKed, arbitration lost, ...)


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
    def _aborted(self) -> uint8:
        # 1 once the controller has flagged an abort on the transfer in progress
        # (IC_RAW_INTR_STAT.TX_ABRT) -- checked INSIDE every wait loop below, not
        # only after it, because software must stop pushing to IC_DATA_CMD the
        # moment one lands: the FIFO is flushed by hardware on abort, and ANY
        # further write starts a brand-new transfer attempt the caller never
        # asked for, instead of leaving the bus idle for `_abort_code` to read.
        return uint8((I2C0_IC_RAW_INTR_STAT.value >> I2C_RAW_INTR_TX_ABRT) & 1)

    @inline
    def _abort_code(self) -> uint8:
        # Reads and clears IC_TX_ABRT_SOURCE (a read of IC_CLR_TX_ABRT is what
        # clears the latched abort, per the DW_apb_i2c databook), and classifies
        # it into the two shapes busio.py's OSError paths distinguish: the
        # target address itself went unanswered (I2C_ABRT_NODEV, CircuitPython's
        # "no device"), or anything else aborted the transfer (I2C_ABRT_OTHER,
        # CircuitPython's generic I/O error).
        if self._aborted() == 0:
            return I2C_OK
        src: uint32 = I2C0_IC_TX_ABRT_SOURCE.value
        clear: uint32 = I2C0_IC_CLR_TX_ABRT.value
        if (src & (1 << I2C_ABRT_7B_ADDR_NOACK)) != 0:
            return I2C_ABRT_NODEV
        return I2C_ABRT_OTHER

    @inline
    def ping(self, addr: uint8) -> uint8:
        # Returns 1 if a device acknowledges `addr`, else 0 -- busio.I2C.probe's
        # own contract. A true address-only ping like AVR's i2c_ping (START,
        # SLA+W, check ACK, STOP, no data phase at all) is not expressible
        # through IC_DATA_CMD: the controller sends nothing until a FIFO entry
        # exists to send. Reading one byte instead of writing one keeps this
        # non-destructive -- the byte itself is discarded, but nothing is sent
        # TO the device either, which a dummy write would have done.
        dummy: bytearray = bytearray(1)
        return uint8(self.read_bytes(addr, dummy, 0, 1) == I2C_OK)

    @inline
    def write_to(self, addr: uint8, data: uint8) -> uint8:
        # Single-byte write with a STOP condition. Returns an I2C_* code.
        self._set_target(addr)
        while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
            if self._aborted():
                return self._abort_code()
        I2C0_IC_DATA_CMD.value = data | I2C_CMD_STOP
        # Wait for the byte to drain so the transfer completes.
        while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFE) & 1) == 0:
            if self._aborted():
                break
        return self._abort_code()

    @inline
    def writebyte(self, addr: uint8, data: uint8) -> uint8:
        # The AVR HAL's single-byte write, with its answer: 1 when the address and the
        # byte were acknowledged, 0 otherwise (write_to answers with an I2C_* code).
        if self.write_to(addr, data) == I2C_OK:
            return 1
        return 0

    @inline
    def write_bytes(self, addr: uint8, data: bytearray, start: uint16, n: uint16) -> uint8:
        # Multi-byte write starting at data[start]; STOP is asserted with the
        # final byte. The index is as wide as the count, and a uint8 here is
        # worse than a truncated transfer. The comparison is not evaluated at
        # run time at all when start/n fold to constants: range folding settles
        # `i < end` while compiling, because a uint8 cannot reach 300, and what
        # is emitted is a bare backward jump with no compare. Do not go looking
        # for a CP/CPC in the asm, there is none. Everything after the loop is
        # then dead code and is deleted, so the STOP, and any cleanup a caller
        # put after the transfer, are not in the program either.
        #
        # Returns an I2C_* code: I2C_OK once every byte has drained, or the abort
        # classification from the point the controller stopped -- an address NACK
        # on the FIRST byte and a data NACK on a later one both land here, which
        # is what makes a single return value answer for either.
        self._set_target(addr)
        i: uint16 = start
        end: uint16 = start + n
        aborted: uint8 = 0
        while i < end:
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
                if self._aborted():
                    aborted = 1
                    break
            if aborted:
                break
            cmd: uint32 = data[i]
            if i == end - 1:
                cmd = cmd | I2C_CMD_STOP
            I2C0_IC_DATA_CMD.value = cmd
            i = i + 1
        if aborted == 0:
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFE) & 1) == 0:
                if self._aborted():
                    break
        return self._abort_code()

    @inline
    def read_bytes(self, addr: uint8, data: bytearray, start: uint16, n: uint16) -> uint8:
        # One request-then-read pair per byte (no FIFO pipelining -- the simpler,
        # still-correct shape): queue a READ command, wait for the RX FIFO to
        # hold the answer, and store it at data[start + k]. STOP rides the final
        # command, exactly as write_bytes rides it on the final byte.
        #
        # Returns an I2C_* code; a NACK on the address is I2C_ABRT_NODEV before
        # a single byte of `data` is touched.
        self._set_target(addr)
        k: uint16 = 0
        aborted: uint8 = 0
        while k < n:
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
                if self._aborted():
                    aborted = 1
                    break
            if aborted:
                break
            cmd: uint32 = I2C_CMD_READ
            if k == n - 1:
                cmd = cmd | I2C_CMD_STOP
            I2C0_IC_DATA_CMD.value = cmd
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_RFNE) & 1) == 0:
                if self._aborted():
                    aborted = 1
                    break
            if aborted:
                break
            data[start + k] = uint8(I2C0_IC_DATA_CMD.value & 0xFF)
            k = k + 1
        return self._abort_code()

    @inline
    def write_then_read(self, addr: uint8, out_data: bytearray, out_start: uint16,
                        out_n: uint16, in_data: bytearray, in_start: uint16,
                        in_n: uint16) -> uint8:
        # writeto_then_readfrom's one bus transaction: the write phase (from
        # out_data[out_start]) asserts no STOP, and the read phase's FIRST
        # command carries RESTART instead -- a repeated START on the SAME
        # target address, direction switched to read, exactly what
        # CircuitPython's own common-hal does for a combined transfer (and
        # what lets a register-address-then-read driver like the BMP280's see
        # one transaction, not two separate ones a device could answer
        # differently to). The read lands at in_data[in_start + k].
        self._set_target(addr)
        i: uint16 = out_start
        out_end: uint16 = out_start + out_n
        aborted: uint8 = 0
        while i < out_end:
            while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
                if self._aborted():
                    aborted = 1
                    break
            if aborted:
                break
            cmd0: uint32 = out_data[i]
            # A zero-length read phase means the write phase is the WHOLE
            # transaction (writeto_then_readfrom(..., in_end=in_start), a
            # legal if odd slice): its own last byte has to carry the STOP,
            # since the read loop below -- the only other place one could
            # come from -- never runs, and an open transaction would hold
            # the bus forever.
            if i == out_end - 1 and in_n == 0:
                cmd0 = cmd0 | I2C_CMD_STOP
            I2C0_IC_DATA_CMD.value = cmd0
            i = i + 1
        if aborted == 0:
            k: uint16 = 0
            while k < in_n:
                while ((I2C0_IC_STATUS.value >> I2C_STATUS_TFNF) & 1) == 0:
                    if self._aborted():
                        aborted = 1
                        break
                if aborted:
                    break
                cmd: uint32 = I2C_CMD_READ
                # RESTART only when a write phase actually ran: with none
                # (out_n == 0, a plain read through this entry point), the
                # bus is idle and the controller issues an ordinary START
                # on its own -- forcing a RESTART here with nothing to
                # restart FROM is the shape the databook leaves undefined.
                if k == 0 and out_n > 0:
                    cmd = cmd | I2C_CMD_RESTART
                if k == in_n - 1:
                    cmd = cmd | I2C_CMD_STOP
                I2C0_IC_DATA_CMD.value = cmd
                while ((I2C0_IC_STATUS.value >> I2C_STATUS_RFNE) & 1) == 0:
                    if self._aborted():
                        aborted = 1
                        break
                if aborted:
                    break
                in_data[in_start + k] = uint8(I2C0_IC_DATA_CMD.value & 0xFF)
                k = k + 1
        return self._abort_code()
