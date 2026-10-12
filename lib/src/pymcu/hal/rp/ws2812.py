# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# WS2812 one-wire pixels on the RP2040 and RP2350 -- pymcu.hal.rp.ws2812
#
# A PIO state machine makes the waveform; the CPU only hands it bytes. The program is the
# four-instruction one from pico-examples (ws2812.pio): one bit is 10 PIO cycles, the pin
# high for 3 of them (a zero) or for 7 (a one), after 3 cycles low, so the state machine runs
# at 8 MHz for 800 kHz bits:
#
#     .side_set 1
#     .wrap_target
#   bitloop:
#       out x, 1       side 0 [2]     ; T3 = 3 cycles low, takes the next bit
#       jmp !x do_zero side 1 [1]     ; T1 = 2 cycles high
#   do_one:
#       jmp  bitloop   side 1 [4]     ; T2 = 5 more cycles high: a one is high for 7
#   do_zero:
#       nop            side 0 [4]     ; T2 = 5 cycles low: a zero is high for 2
#     .wrap
#
# It sits at the TOP of instruction memory (words 28-31) of PIO0 and runs on state machine 3,
# so a program that also uses `rp2.StateMachine(0, ...)`, which loads from word 0 and runs
# state machine 0, does not collide with it. Autopull is on at 8 bits and the shift is
# left (MSB first), so each byte the caller writes is one word in the TX FIFO, in its top
# byte, and the FIFO is joined to 8 words deep.
#
# The same registers, offsets and function-select values serve both chips (the PIO blocks
# are identical); the chip modules only differ in where RESETS, IO_BANK0 and PADS_BANK0 live.
#
# Interrupts do not need to be off across a frame, unlike the bit-banged AVR driver: the
# state machine keeps its own time. What can still break a frame is the CPU not refilling
# the FIFO in time, which shows as a gap longer than 50 us and latches a short frame.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__, __FREQ__
from pymcu.types import ptr, uint8, uint32, const, inline
from pymcu.time import delay_us

if __CHIP__.name == "rp2350":
    from pymcu.chips.rp2350 import (
        PIO0_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_PIO0,
        RESET_IO_BANK0, RESET_PADS_BANK0, IO_BANK0_BASE, PADS_BANK0_BASE, GPIO_FUNC_PIO0,
    )
else:
    from pymcu.chips.rp2040 import (
        PIO0_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_PIO0,
        RESET_IO_BANK0, RESET_PADS_BANK0, IO_BANK0_BASE, PADS_BANK0_BASE, GPIO_FUNC_PIO0,
    )

_SM = 3                       # state machine 3 of PIO0
_LOAD = 28                    # the program occupies words 28-31

_PIO_CTRL: ptr[uint32] = ptr(PIO0_BASE + 0x000)
_PIO_FSTAT: ptr[uint32] = ptr(PIO0_BASE + 0x004)
_PIO_TXF: ptr[uint32] = ptr(PIO0_BASE + 0x010 + 4 * _SM)
_PIO_MEM28: ptr[uint32] = ptr(PIO0_BASE + 0x048 + 4 * _LOAD)
_PIO_MEM29: ptr[uint32] = ptr(PIO0_BASE + 0x048 + 4 * (_LOAD + 1))
_PIO_MEM30: ptr[uint32] = ptr(PIO0_BASE + 0x048 + 4 * (_LOAD + 2))
_PIO_MEM31: ptr[uint32] = ptr(PIO0_BASE + 0x048 + 4 * (_LOAD + 3))
_SM_CLKDIV: ptr[uint32] = ptr(PIO0_BASE + 0x0C8 + 0x18 * _SM)
_SM_EXECCTRL: ptr[uint32] = ptr(PIO0_BASE + 0x0CC + 0x18 * _SM)
_SM_SHIFTCTRL: ptr[uint32] = ptr(PIO0_BASE + 0x0D0 + 0x18 * _SM)
_SM_INSTR: ptr[uint32] = ptr(PIO0_BASE + 0x0D8 + 0x18 * _SM)
_SM_PINCTRL: ptr[uint32] = ptr(PIO0_BASE + 0x0DC + 0x18 * _SM)


@inline
def ws2812_init(pin: const[uint8]):
    """Route `pin` to PIO0, load the program and start state machine 3, pin driven low."""
    # PIO0, IO_BANK0 and PADS_BANK0 out of reset.
    unreset: uint32 = (1 << RESET_PIO0) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
    RESETS_RESET_CLR.value = unreset
    while (RESETS_RESET_DONE.value & unreset) != unreset:
        pass

    # The pad: input enable on and (RP2350) isolation off, then the pin to PIO0.
    pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * pin)
    pad.value = 1 << 6
    ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
    ctrl.value = GPIO_FUNC_PIO0

    # State machine 3 off while it is configured.
    _PIO_CTRL.value = _PIO_CTRL.value & ~(1 << _SM)

    # out x,1 side 0 [2] / jmp !x,31 side 1 [1] / jmp 28 side 1 [4] / nop side 0 [4].
    _PIO_MEM28.value = 0x6221
    _PIO_MEM29.value = 0x113F
    _PIO_MEM30.value = 0x141C
    _PIO_MEM31.value = 0xA442

    # 8 MHz: sys_clk / 8e6 as a 16.8 fixed-point divider.
    # (kHz * 32) / 1000: the product stays inside 32 bits at 150 MHz, where MHz * 256 * 1e6 would not.
    div: uint32 = (__FREQ__ // 1000) * 32 // 1000
    _SM_CLKDIV.value = (div >> 8) << 16 | (div & 0xFF) << 8

    _SM_EXECCTRL.value = (31 << 12) | (28 << 7)                  # wrap 31 -> 28
    _SM_SHIFTCTRL.value = (1 << 17) | (8 << 25) | (1 << 30)      # autopull at 8 bits, TX FIFO joined
    _SM_PINCTRL.value = (1 << 29) | (pin << 10) | (1 << 26) | (pin << 5)   # one side-set pin, one set pin

    # Restart, then make the pin an output driven low, then enter the loop.
    _PIO_CTRL.value = _PIO_CTRL.value | (1 << (4 + _SM)) | (1 << (8 + _SM))
    _SM_INSTR.value = 0xE081                                      # set pindirs, 1
    _SM_INSTR.value = 0xE000                                      # set pins, 0
    _SM_INSTR.value = _LOAD                                       # jmp 28
    _PIO_CTRL.value = _PIO_CTRL.value | (1 << _SM)


@inline
def ws2812_write_byte(val: uint8):
    """One byte onto the wire, most significant bit first. Waits while the FIFO is full."""
    while (_PIO_FSTAT.value >> (16 + _SM)) & 1:
        pass
    word: uint32 = val
    _PIO_TXF.value = word << 24


@inline
def ws2812_reset():
    """Wait for the FIFO to drain, then hold the line low long enough for the strip to latch."""
    while ((_PIO_FSTAT.value >> (24 + _SM)) & 1) == 0:
        pass
    # The last byte is still being shifted out (10 us), then more than 50 us low.
    delay_us(80)
