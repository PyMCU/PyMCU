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
# Supported architectures: AVR (ATmega328P), PIC (16F877A, 16F18877, 18F45K50).
#
# ATmega328P channel mapping (AVcc reference, prescaler 128 = 125 kHz at 16 MHz):
#   "PC0" = A0   "PC1" = A1   "PC2" = A2
#   "PC3" = A3   "PC4" = A4   "PC5" = A5
#
# ATmega328P implementation detail:
#   self._admux -- compile-time ADMUX register value encoding reference + channel.
#                  Resolved once at construction; all read calls use it directly
#                  with no further string dispatch.
#
# PIC implementation:
#   self.channel -- compile-time const[str] channel name forwarded to arch helpers.
from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.gpio import Pin


# noinspection PyProtectedMember
class AnalogPin:

    # Initialise an ADC channel from a port-pin name string, e.g. "PC0".
    # On ATmega328P: resolves the channel to an ADMUX value at compile time,
    # writes ADMUX and enables the ADC peripheral with prescaler 128 (125 kHz
    # at 16 MHz, AVcc reference).  Subsequent reads use self._admux directly.
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

    # Trigger a conversion with ADC Interrupt Enable (ADIE = 1), then return.
    # The ADC-complete ISR fires at vector byte 0x002A / word 0x0015 on ATmega328P.
    # Pair with an @interrupt(0x002A) handler that calls read_result().
    @inline
    def start_conversion(self):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._adc.atmega328p import adc_start_int
                adc_start_int()

    # Read the raw 10-bit result without triggering a new conversion.
    # Returns: uint16 in range 0-1023.
    # Call from the ADC-complete ISR or after polling the ADIF flag.
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
