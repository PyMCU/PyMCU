# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Shared HAL package for the Raspberry Pi RP family (RP2040 / RP2350).
# Modules here are chip-agnostic across the family: both chips expose the
# same PL011 UART register names in pymcu.chips.<chip>, so a single source
# serves both via a conditional chip import.
