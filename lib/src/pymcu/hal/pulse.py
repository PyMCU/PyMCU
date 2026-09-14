# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Pulses -- pymcu.hal.pulse
#
# Two things that are the same question from opposite ends: how long were the pulses
# that arrived, and send these pulses.
#
#   capture = PulseCapture("PD2", maxlen=81)
#   while capture.count() < 81:
#       pass
#   first = capture.popleft()          # microseconds
#
#   train = PulseTrain("PD3", freq=38000)
#   train.send(frame, len(frame))      # microseconds, carrier on for the first
#
# Durations are microseconds on every architecture, because that is the unit a protocol
# is written in. What times the edges -- a free-running timer and a pin-change interrupt,
# a capture unit, a PIO program -- is the chip's business and never reaches a caller.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.pulse import PulseCapture, PulseTrain
else:
    raise CompileError(
        "pulse capture and pulse trains are not implemented on this architecture yet. They "
        "need a timer running free to timestamp edges and a compare channel for the carrier, "
        "and neither has been mapped for this part. Measure a single pulse with "
        "pymcu.hal.gpio's Pin.pulse_in(), which counts cycles and needs no timer.")
