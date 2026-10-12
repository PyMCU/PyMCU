# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Pulse capture on the RP2040 and RP2350 -- pymcu.hal.rp.pulse
#
# The lengths of the pulses arriving on one pin, in microseconds, with nothing but a PIO state
# machine and a DMA channel between the pin and a buffer, so the CPU can be asleep, busy or
# running a delay while a frame arrives:
#
#   PIO1 state machine 0   measures every interval between two edges, counting in 1 us steps,
#                          and pushes one word per interval into its RX FIFO (8 deep, joined);
#   DMA channel 11         moves each word, paced by the FIFO, into a 256-word ring of RAM
#                          that never stops (hal.pulse.PULSE_RING_BASE).
#
# Reading is bookkeeping on the ring: the DMA's write address says how many words have come,
# `_head` says how many have been taken. Nothing runs on the CPU between the two.
#
# The PIO program, assembled by the compiler's own assembler (tests/unit
# PulsePioProgramTests holds the source and checks these words against it). Each loop
# iteration is two PIO cycles, and the state machine runs at 2 MHz, so a count is a microsecond:
#
#   start:    jmp pin hi_first      ; the line is high: wait for it to fall
#             wait 1 pin 0          ; it is low: wait for it to rise
#             jmp count_h
#   hi_first: wait 0 pin 0
#             jmp count_l
#   count_h:  mov x, ~null          ; x counts down from 2**32-1
#   h_loop:   jmp pin h_dec
#             jmp done_h            ; the pin fell: the high pulse is over
#   h_dec:    jmp x-- h_loop
#   done_h:   mov isr, ~x           ; ~x is the number of iterations
#             push noblock          ; a full FIFO drops the word, it never stalls the count
#   count_l:  mov x, ~null
#   l_loop:   jmp pin done_l        ; the pin rose: the low pulse is over
#             jmp x-- l_loop
#   done_l:   mov isr, ~x
#             push noblock
#             jmp count_h
#
# The first edge after a clear only starts the clock, so the first interval stored is the one
# that follows the line leaving rest, whichever level rest is; `idle_state` is accepted and
# honoured without a register, as on the AVR. A full ring pushes the OLDEST pulse out. The
# accuracy is one microsecond plus the three cycles (1.5 us) between one interval's end and the
# next one's start, which the count does not see: fine for IR bursts and for DHT bits, whose
# threshold is 50 us between a 26 us and a 70 us high.
#
# One capture per program (PIO1 state machine 0, DMA channel 11, one ring), like the AVR's. The
# ring is the top 1 KiB below the stack reserve, which the pymcu-arm linker scripts keep out of
# the heap; maxlen is at most 255.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__, __FREQ__
from pymcu.exceptions import CompileError
from pymcu.types import ptr, uint8, uint16, uint32, const, inline
from pymcu.hal.gpio import Pin as _Pin
from pymcu.time import delay_us

if __CHIP__.name == "rp2350":
    from pymcu.chips.rp2350 import (
        PIO1_BASE, DMA_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE,
        RESET_PIO1, RESET_DMA, PULSE_RING_BASE,
        PWM_BASE, PWM_CH_STRIDE, PWM_CH_CSR, PWM_CH_DIV, PWM_CH_CC, PWM_CH_TOP,
        RESET_PWM, RESET_IO_BANK0, RESET_PADS_BANK0, IO_BANK0_BASE, PADS_BANK0_BASE,
        SIO_GPIO_OE_SET, SIO_GPIO_OE_CLR, SIO_GPIO_OUT_CLR, GPIO_FUNC_PWM, GPIO_FUNC_SIO,
    )
else:
    from pymcu.chips.rp2040 import (
        PIO1_BASE, DMA_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE,
        RESET_PIO1, RESET_DMA, PULSE_RING_BASE,
        PWM_BASE, PWM_CH_STRIDE, PWM_CH_CSR, PWM_CH_DIV, PWM_CH_CC, PWM_CH_TOP,
        RESET_PWM, RESET_IO_BANK0, RESET_PADS_BANK0, IO_BANK0_BASE, PADS_BANK0_BASE,
        SIO_GPIO_OE_SET, SIO_GPIO_OE_CLR, SIO_GPIO_OUT_CLR, GPIO_FUNC_PWM, GPIO_FUNC_SIO,
    )

_PIO_CTRL: ptr[uint32] = ptr(PIO1_BASE + 0x000)
_PIO_FSTAT: ptr[uint32] = ptr(PIO1_BASE + 0x004)
_SM_CLKDIV: ptr[uint32] = ptr(PIO1_BASE + 0x0C8)
_SM_EXECCTRL: ptr[uint32] = ptr(PIO1_BASE + 0x0CC)
_SM_SHIFTCTRL: ptr[uint32] = ptr(PIO1_BASE + 0x0D0)
_SM_INSTR: ptr[uint32] = ptr(PIO1_BASE + 0x0D8)
_SM_PINCTRL: ptr[uint32] = ptr(PIO1_BASE + 0x0DC)

# DMA channel 11: READ_ADDR, WRITE_ADDR, TRANS_COUNT, CTRL_TRIG.
_DMA_READ: ptr[uint32] = ptr(DMA_BASE + 0x40 * 11 + 0x00)
_DMA_WRITE: ptr[uint32] = ptr(DMA_BASE + 0x40 * 11 + 0x04)
_DMA_COUNT: ptr[uint32] = ptr(DMA_BASE + 0x40 * 11 + 0x08)
_DMA_CTRL: ptr[uint32] = ptr(DMA_BASE + 0x40 * 11 + 0x0C)

_head: uint32 = 0          # words taken from the ring
_maxlen: uint32 = 0
_paused: uint8 = 0


@inline
def _load(i: const[uint8], word: uint32):
    mem: ptr[uint32] = ptr(PIO1_BASE + 0x048 + 4 * i)
    mem.value = word


@inline
def pulse_capture_start(pin: const[uint8], maxlen: const[uint16]):
    """Build the capture on `pin`: program, clock, DMA ring, state machine running."""
    global _head, _maxlen, _paused
    if maxlen == 0 or maxlen > 255:
        raise CompileError(
            "a PulseIn on the RP2040 or RP2350 holds between 1 and 255 pulses: the DMA ring it "
            "records into has 256 words and one of them tells a full ring from an empty one.")

    # PIO1 and DMA out of reset.
    unreset: uint32 = (1 << RESET_PIO1) | (1 << RESET_DMA)
    RESETS_RESET_CLR.value = unreset
    while (RESETS_RESET_DONE.value & unreset) != unreset:
        pass

    # State machine 0 off while it is set up, then the program.
    _PIO_CTRL.value = _PIO_CTRL.value & ~1
    _load(0, 0x00C3)
    _load(1, 0x20A0)
    _load(2, 0x0005)
    _load(3, 0x2020)
    _load(4, 0x000B)
    _load(5, 0xA02B)
    _load(6, 0x00C8)
    _load(7, 0x0009)
    _load(8, 0x0046)
    _load(9, 0xA0C9)
    _load(10, 0x8000)
    _load(11, 0xA02B)
    _load(12, 0x00CE)
    _load(13, 0x004C)
    _load(14, 0xA0C9)
    _load(15, 0x8000)
    _load(16, 0x0005)

    # 2 MHz, so that two cycles per count make a microsecond: sys_clk / 2e6 as 16.8 fixed point.
    div: uint32 = (__FREQ__ // 1000) * 128 // 1000
    _SM_CLKDIV.value = (div >> 8) << 16 | (div & 0xFF) << 8
    _SM_EXECCTRL.value = (pin << 24) | (16 << 12) | (5 << 7)     # jmp pin = the pin, wrap 16 -> 5
    _SM_SHIFTCTRL.value = 1 << 31                                # RX FIFO joined: 8 words deep
    _SM_PINCTRL.value = pin << 15                                # in base = the pin (for wait ... pin)

    # The pin is an input with a pull-up the caller may keep or change; PIO reads it whatever
    # function the pad has.
    inp = _Pin(pin, _Pin.IN)

    # DMA channel 11: PIO1 RX FIFO 0 -> the ring, paced by the FIFO, 4-byte words, write
    # address wrapping inside 1 KiB, never chaining.
    _DMA_READ.value = PIO1_BASE + 0x20
    _DMA_WRITE.value = PULSE_RING_BASE
    _DMA_COUNT.value = 0x0FFFFFFF
    if __CHIP__.name == "rp2350":
        # EN | DATA_SIZE word | INCR_WRITE | RING_SIZE 10 | RING_SEL write | CHAIN_TO self | TREQ PIO1_RX0
        _DMA_CTRL.value = 1 | (2 << 2) | (1 << 6) | (10 << 8) | (1 << 12) | (11 << 13) | (12 << 17)
    else:
        _DMA_CTRL.value = 1 | (2 << 2) | (1 << 5) | (10 << 6) | (1 << 10) | (11 << 11) | (12 << 15)

    _head = 0
    _maxlen = maxlen
    _paused = 0

    # Restart the state machine at word 0 and let it run.
    _PIO_CTRL.value = _PIO_CTRL.value | (1 << 4)
    _SM_INSTR.value = 0x0000
    _PIO_CTRL.value = _PIO_CTRL.value | 1


def _count() -> uint32:
    """Pulses waiting, never more than maxlen: a full ring has its oldest ones pushed out."""
    global _head
    written: uint32 = ((_DMA_WRITE.value - PULSE_RING_BASE) >> 2) & 255
    n: uint32 = (written - _head) & 255
    if n > _maxlen:
        _head = (written - _maxlen) & 255
        n = _maxlen
    return n


@inline
def pulse_capture_count() -> uint16:
    return _count()


@inline
def pulse_capture_maxlen() -> uint16:
    return _maxlen


@inline
def pulse_capture_capacity() -> uint16:
    return _maxlen


@inline
def _microseconds(word: uint32) -> uint16:
    # A pulse longer than 65535 us reads as 65535, which is the largest a uint16 holds and,
    # at that length, as good as "idle".
    if word > 65535:
        return 65535
    return word


def pulse_capture_popleft() -> uint16:
    global _head
    if _count() == 0:
        return 0
    slot: ptr[uint32] = ptr(PULSE_RING_BASE + 4 * _head)
    word: uint32 = slot.value
    _head = (_head + 1) & 255
    return _microseconds(word)


def pulse_capture_get(i: uint16) -> uint16:
    if i >= _count():
        return 0
    slot: ptr[uint32] = ptr(PULSE_RING_BASE + 4 * ((_head + i) & 255))
    word: uint32 = slot.value
    return _microseconds(word)


def pulse_capture_pause():
    global _paused
    _PIO_CTRL.value = _PIO_CTRL.value & ~1
    _paused = 1


def pulse_capture_resume():
    """Listen again: the state machine starts over, so the next edge is a fresh reference."""
    global _paused
    _PIO_CTRL.value = _PIO_CTRL.value | (1 << 4)
    _SM_INSTR.value = 0x0000
    _PIO_CTRL.value = _PIO_CTRL.value | 1
    _paused = 0


def pulse_capture_clear():
    """Forget every pulse held and start the next one from the next edge."""
    global _head
    _PIO_CTRL.value = _PIO_CTRL.value & ~1
    # What the state machine already pushed is on its way to the ring: let the DMA finish.
    spin: uint16 = 0
    while ((_PIO_FSTAT.value >> 8) & 1) == 0 and spin < 60000:
        spin = spin + 1
    _head = ((_DMA_WRITE.value - PULSE_RING_BASE) >> 2) & 255
    if _paused == 0:
        _PIO_CTRL.value = _PIO_CTRL.value | (1 << 4)
        _SM_INSTR.value = 0x0000
        _PIO_CTRL.value = _PIO_CTRL.value | 1


@inline
def pulse_capture_paused() -> uint8:
    return _paused


def pulse_capture_deinit():
    """Stop recording and the DMA channel."""
    global _paused
    _PIO_CTRL.value = _PIO_CTRL.value & ~1
    _DMA_CTRL.value = 0
    _paused = 1


def pulse_delay_us(us: uint16):
    delay_us(us)


class PulseCapture:
    """The lengths of the pulses arriving on one pin, in microseconds.

    The same shape on every architecture: construct it on a pin, read the count, take the
    pulses out oldest first. Here a PIO state machine times the edges and DMA stores them, so
    reading is only arithmetic on a ring.

    One per program: the state machine, the DMA channel and the ring are fixed.
    """

    def __init__(self, pin: const, maxlen: const[uint16] = 2, idle_state: const[uint8] = 0):
        self._idle = idle_state
        pulse_capture_start(pin, maxlen)

    @inline
    def count(self) -> uint16:
        return pulse_capture_count()

    @inline
    def maxlen(self) -> uint16:
        return pulse_capture_maxlen()

    @inline
    def capacity(self) -> uint16:
        return pulse_capture_capacity()

    @inline
    def popleft(self) -> uint16:
        return pulse_capture_popleft()

    @inline
    def get(self, i: uint16) -> uint16:
        return pulse_capture_get(i)

    @inline
    def clear(self):
        pulse_capture_clear()

    @inline
    def pause(self):
        pulse_capture_pause()

    @inline
    def resume(self):
        pulse_capture_resume()

    @inline
    def paused(self) -> uint8:
        return pulse_capture_paused()

    @inline
    def deinit(self):
        pulse_capture_deinit()


class PulseTrain:
    """A gated carrier on one pin: what an infrared emitter sends.

    The carrier is a PWM slice that runs free; the pin is handed to it (carrier on) or taken
    back and held low (carrier off) by switching its function, so the gap between two edges
    is the CPU's delay, as on the AVR. Any GPIO below 32 has a slice.
    """

    def __init__(self, pin: const[uint8], freq: const[uint32] = 38000,
                 duty_u16: const[uint16] = 32768):
        self._pin = pin
        unreset: uint32 = (1 << RESET_PWM) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = unreset
        while (RESETS_RESET_DONE.value & unreset) != unreset:
            pass

        # The slice: counts the system clock, wraps once per carrier period.
        div: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_DIV)
        div.value = 1 << 4
        top: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_TOP)
        top.value = ((__FREQ__ // freq) - 1) & 0xFFFF
        cc: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CC)
        if (pin & 1) == 0:
            cc.value = ((((__FREQ__ // freq) >> 4) * duty_u16) >> 12) & 0xFFFF
        else:
            cc.value = (((((__FREQ__ // freq) >> 4) * duty_u16) >> 12) & 0xFFFF) << 16
        csr: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CSR)
        csr.value = 1

        # The pin: an output held low, until the carrier is switched on.
        pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * pin)
        pad.value = 1 << 6
        SIO_GPIO_OUT_CLR.value = 1 << pin
        SIO_GPIO_OE_SET.value = 1 << pin
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
        ctrl.value = GPIO_FUNC_SIO

    @inline
    def carrier_on(self):
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * self._pin + 4)
        ctrl.value = GPIO_FUNC_PWM

    @inline
    def carrier_off(self):
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * self._pin + 4)
        ctrl.value = GPIO_FUNC_SIO

    @inline
    def send(self, pulses, n: uint16):
        i: uint16 = 0
        while i < n:
            if (i & 1) == 0:
                self.carrier_on()
            else:
                self.carrier_off()
            pulse_delay_us(pulses[i])
            i = i + 1
        self.carrier_off()

    @inline
    def deinit(self):
        self.carrier_off()
        csr: ptr[uint32] = ptr(PWM_BASE + ((self._pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CSR)
        csr.value = 0
        SIO_GPIO_OE_CLR.value = 1 << self._pin
