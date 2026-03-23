# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/adc.py -- hardware ADC zero-cost abstraction (ZCA)
#
# Supported architectures: AVR, PIC.
#
# AnalogPin(channel) accepts a port-pin name string (e.g. "PC0").
# If you hold a Pin instance, pass pin.name -- it is a compile-time const[str]
# in the ZCA alias chain with no runtime cost: AnalogPin(my_pin.name).
#
# Channel-to-register mapping, reference selection, and conversion clock
# are resolved at construction time; subsequent reads require no string dispatch.
from whipsnake.chips import __CHIP__
from whipsnake.types import uint16, inline


# noinspection PyProtectedMember
class AnalogPin:
    """Hardware ADC channel, zero-cost abstraction (all methods @inline).

    Accepts a port-pin name string. If you hold a Pin instance, pass pin.name --
    it is a compile-time const[str] in the ZCA alias chain, so there is no
    runtime cost:

        adc = AnalogPin("PC0")
        adc = AnalogPin(my_pin.name)   # identical generated code
        val: uint16 = adc.read()       # 0-1023
    """

    def __init__(self, channel: str):
        """Initialize an ADC channel from a port-pin name string.

        The channel is resolved to a hardware register value at compile time.
        Subsequent reads use the stored value directly with no string dispatch.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_channel_admux, adc_init
                # Resolve channel to ADMUX value once and store it.
                # All subsequent reads use self._admux directly -- no string dispatch.
                self._admux = adc_channel_admux(channel)
                adc_init(self._admux)
            case "pic16f877a":
                from whipsnake.hal._adc.pic16f877a import adc_init
                self.channel = channel
                adc_init(channel)
            case "pic16f18877":
                from whipsnake.hal._adc.pic16f18877 import adc_init
                self.channel = channel
                adc_init(channel)
            case "pic18f45k50":
                from whipsnake.hal._adc.pic18f45k50 import adc_init
                self.channel = channel
                adc_init(channel)

    @inline
    def start(self):
        """Trigger an ADC conversion (non-blocking).

        Poll the conversion-complete flag or pair with start_conversion() and
        an interrupt handler to be notified on completion.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_start
                adc_start()
            case "pic16f877a":
                from whipsnake.hal._adc.pic16f877a import adc_start
                adc_start(self.channel)
            case "pic16f18877":
                from whipsnake.hal._adc.pic16f18877 import adc_start
                adc_start(self.channel)
            case "pic18f45k50":
                from whipsnake.hal._adc.pic18f45k50 import adc_start
                adc_start(self.channel)

    @inline
    def read(self) -> uint16:
        """Trigger a conversion, block until complete, and return the raw 10-bit result.

        Returns an uint16 in the range 0-1023.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_read
                return adc_read()

        return 0

    @inline
    def start_conversion(self):
        """Trigger a conversion with the ADC interrupt enabled, then return immediately.

        The ADC-complete ISR fires when conversion finishes. Pair with an
        @interrupt handler at the ADC-complete vector that calls read_result().
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_start_int
                adc_start_int()

    @inline
    def read_result(self) -> uint16:
        """Read the raw 10-bit result without triggering a new conversion.

        Returns an uint16 in the range 0-1023. Call from the ADC-complete ISR
        or after the conversion-complete flag is set.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_read_result
                return adc_read_result()

        return 0

    @inline
    def read_u16(self) -> uint16:
        """Trigger a conversion, block until complete, and return the result scaled to 16-bit.

        Returns an uint16 in the range 0-65535 (10-bit ADC value multiplied by 64).
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from whipsnake.hal._adc.atmega328p import adc_read_u16
                return adc_read_u16()

        return 0
