# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR pulse facade -- pymcu.hal.avr.pulse
#
# Module-level conditional imports select the chip implementation at compile time.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, uint32, inline, const

if (__CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328"
        or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168"
        or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88"
        or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48"):
    from pymcu.hal.avr.pulse.atmega328p import (
        pulse_capture_attach, pulse_capture_timebase_init, pulse_capture_set_maxlen,
        pulse_capture_clear, pulse_capture_count, pulse_capture_maxlen,
        pulse_capture_capacity, pulse_capture_popleft, pulse_capture_get,
        pulse_capture_pause, pulse_capture_resume, pulse_capture_paused,
        pulse_train_init, pulse_train_carrier_on, pulse_train_carrier_off,
        pulse_train_deinit, pulse_delay_us,
    )
else:
    # Every other AVR needs its own edge timestamping: the timer that runs free, the
    # pin-change vectors and the carrier channel are all different, and guessing at them is
    # how a HAL comes to write registers a die does not have.
    raise CompileError(
        "pulse capture and pulse trains are implemented on the ATmega 48/88/168/328 family "
        "and not yet on this chip. They need a free-running 16-bit timer for the timestamps "
        "and a compare channel for the carrier, and neither has been mapped for this part. "
        "Measure a single pulse with pymcu.hal.gpio's Pin.pulse_in(), which is a cycle-counted "
        "loop and needs no timer.")

from pymcu.hal.gpio import Pin as _Pin


class PulseCapture:
    """The lengths of the pulses arriving on one pin, in microseconds.

    The same shape on every architecture: construct it on a pin, read the count, take the
    pulses out oldest first. What the chip uses to timestamp the edges is the HAL's business.

    One per program. The buffer and the edge timestamp are module state, so a second
    PulseCapture would share them; there is no per-instance storage to give it.
    """

    def __init__(self, pin: const, maxlen: const[uint16] = 2, idle_state: const[uint8] = 0):
        # idle_state is the level the line sits at between pulses. It is what decides which
        # edge starts the first pulse, and this capture decides that from the line itself:
        # the first edge after a clear only sets the reference, so the first STORED interval
        # is the one that follows the line leaving rest, whichever level rest is. Accepted,
        # and honoured without a register.
        self._idle = idle_state
        pulse_capture_set_maxlen(maxlen)
        pulse_capture_timebase_init()
        pulse_capture_clear()
        _r = _Pin(pin, _Pin.IN)
        pulse_capture_attach(pin)

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
        pulse_capture_pause()


class PulseTrain:
    """A gated carrier on one pin: what an infrared emitter sends.

    send() walks a list of durations in microseconds, carrier on for the first, off for the
    second, and so on -- the same convention CircuitPython's PulseOut uses.
    """

    def __init__(self, pin: const, freq: const[uint32] = 38000,
                 duty_u16: const[uint16] = 32768):
        self._pin = pin
        pulse_train_init(pin, freq, duty_u16)

    @inline
    def send(self, pulses, n: uint16):
        i: uint16 = 0
        while i < n:
            if (i & 1) == 0:
                pulse_train_carrier_on()
            else:
                pulse_train_carrier_off()
            pulse_delay_us(pulses[i])
            i = i + 1
        pulse_train_carrier_off()

    @inline
    def carrier_on(self):
        pulse_train_carrier_on()

    @inline
    def carrier_off(self):
        pulse_train_carrier_off()

    @inline
    def deinit(self):
        pulse_train_deinit()
