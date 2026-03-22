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
# AnalogPin(pin) also accepts a Pin ZCA instance; pin.name is extracted at
# compile time via the alias chain with no runtime cost.
#
# Channel-to-register mapping, reference selection, and conversion clock
# are resolved at construction time; subsequent reads require no string dispatch.
from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.gpio import Pin


# noinspection PyProtectedMember
class AnalogPin:
    """Hardware ADC channel, zero-cost abstraction (all methods @inline).

    Accepts a port-pin name string or a Pin ZCA instance. The channel-to-
    register mapping is resolved at construction time; subsequent reads use
    the stored value directly with no string dispatch.

    Usage::

        adc = AnalogPin("PC0")
        val: uint16 = adc.read()    # 0-1023
    """

    # Initialise an ADC channel from a port-pin name string.
    # The channel is resolved to a hardware register value at compile time.
    # Subsequent reads use the stored value directly with no string dispatch.
    @inline
    def __init__(self, channel: str):
        match __CHIP__.name:
            case "atmega328p":
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

    # Initialise an ADC channel from a Pin ZCA instance.
    # Extracts pin.name at compile time via the ZCA alias chain -- no runtime cost.
    # Useful when the same port pin is first set up as a GPIO Pin and then used
    # as an analog input, e.g.:
    #   sensor_pin = Pin("PC0", Pin.IN)
    #   adc = AnalogPin(sensor_pin)
    @inline
    def __init__(self, pin: Pin):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_channel_admux, adc_init
                self._admux = adc_channel_admux(pin.name)
                adc_init(self._admux)
            case "pic16f877a":
                from whipsnake.hal._adc.pic16f877a import adc_init
                self.channel = pin.name
                adc_init(pin.name)
            case "pic16f18877":
                from whipsnake.hal._adc.pic16f18877 import adc_init
                self.channel = pin.name
                adc_init(pin.name)
            case "pic18f45k50":
                from whipsnake.hal._adc.pic18f45k50 import adc_init
                self.channel = pin.name
                adc_init(pin.name)

    # Trigger an ADC conversion (non-blocking).
    # Poll the ADSC bit or use start_conversion() with an interrupt for completion.
    @inline
    def start(self):
        match __CHIP__.name:
            case "atmega328p":
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

    # Trigger a conversion, block until complete, and return the raw 10-bit result.
    # Returns: uint16 in range 0-1023.
    @inline
    def read(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_read
                return adc_read()

    # Trigger a conversion with the ADC interrupt enabled, then return immediately.
    # The ADC-complete ISR fires when conversion finishes.
    # Pair with an @interrupt handler at the ADC-complete vector that calls read_result().
    @inline
    def start_conversion(self):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_start_int
                adc_start_int()

    # Read the raw 10-bit result without triggering a new conversion.
    # Returns: uint16 in range 0-1023.
    # Call from the ADC-complete ISR or after the conversion-complete flag is set.
    @inline
    def read_result(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_read_result
                return adc_read_result()

    # Trigger a conversion, block until complete, and return the result scaled to 16-bit.
    # Returns: uint16 in range 0-65535 (10-bit ADC value multiplied by 64).
    @inline
    def read_u16(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_read_u16
                return adc_read_u16()
