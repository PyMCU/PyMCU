# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR I2C / TWI Master HAL - ATmega328P hardware TWI (100 kHz default)
#
# ATmega328P TWI pins (Arduino Uno mapping):
#   SDA = PC4  (Arduino pin A4) - automatically controlled by TWI hardware
#   SCL = PC5  (Arduino pin A5) - automatically controlled by TWI hardware
#
# Register map (all in LDS/STS range > 0x5F):
#   TWBR = 0xB8  - Bit Rate register:  SCL = F_CPU / (16 + 2*TWBR*prescaler)
#   TWSR = 0xB9  - Status register:    upper 5 bits = status code, lower 2 = prescaler
#   TWDR = 0xBB  - Data register:      byte to send / last received byte
#   TWCR = 0xBC  - Control register:   TWINT(7)|TWEA(6)|TWSTA(5)|TWSTO(4)|TWEN(2)
#
# At 16 MHz, 100 kHz: TWBR = (16000000/100000 - 16) / 2 = 72  (prescaler = 1)
#
# TWI status codes (TWSR & 0xF8):
#   0x08 - START condition transmitted OK
#   0x18 - SLA+W sent, ACK received
#   0x20 - SLA+W sent, NACK received (no device)
#   0x28 - data byte sent, ACK received
#   0x40 - SLA+R sent, ACK received
#   0x50 - data byte received, ACK returned
#   0x58 - data byte received, NACK returned (last byte)
# -----------------------------------------------------------------------------

from whipsnake.chips.atmega328p import TWBR, TWSR, TWDR, TWCR
from whipsnake.types import uint8, inline


@inline
def i2c_ping(addr: uint8) -> uint8:
    # Probe one address: START + SLA+W, check ACK, then STOP.
    # Returns 1 if device acknowledges, 0 otherwise.
    TWCR.value = 0xA4           # START
    while TWCR[7] == 0:
        pass
    status: uint8 = TWSR.value & 0xF8
    if status == 0x08:          # START OK
        TWDR.value = addr << 1  # SLA+W
        TWCR.value = 0x84
        while TWCR[7] == 0:
            pass
        ack: uint8 = TWSR.value & 0xF8
        TWCR.value = 0x94       # STOP
        if ack == 0x18:
            return 1     # ACK received - device present
    else:
        TWCR.value = 0x94       # STOP (START failed)
    return 0


@inline
def i2c_init():
    # 100 kHz at F_CPU = 16 MHz, prescaler = 1
    # TWBR = (F_CPU / SCL_freq - 16) / (2 * prescaler) = (160 - 16) / 2 = 72
    TWBR.value = 72
    TWSR.value = 0x00   # prescaler bits[1:0] = 00 -- prescaler = 1
    TWCR.value = 0x04   # TWEN(2) = 1: enable TWI (takes over PC4/PC5 pins)


@inline
def i2c_start() -> uint8:
    # Transmit START condition: TWINT(7)|TWSTA(5)|TWEN(2) = 0xA4
    TWCR.value = 0xA4
    while TWCR[7] == 0:   # Wait for TWINT (hardware clears it when operation done)
        pass
    status: uint8 = TWSR.value & 0xF8   # Mask prescaler bits - 0x08 = START OK
    return status


@inline
def i2c_stop():
    # Transmit STOP condition: TWINT(7)|TWSTO(4)|TWEN(2) = 0x94
    # No need to wait; hardware releases the bus automatically
    TWCR.value = 0x94


@inline
def i2c_write(data: uint8) -> uint8:
    # Load byte into data register then kick off transmission
    TWDR.value = data
    TWCR.value = 0x84   # TWINT(7)|TWEN(2) = clear TWINT, keep TWEN
    while TWCR[7] == 0:
        pass
    status: uint8 = TWSR.value & 0xF8   # 0x18 = ACK, 0x20 = NACK, 0x28 = data ACK
    return status


@inline
def i2c_read_ack() -> uint8:
    # Read one byte and send ACK (more bytes to follow)
    TWCR.value = 0xC4   # TWINT(7)|TWEA(6)|TWEN(2) - TWEA=1 -- generate ACK
    while TWCR[7] == 0:
        pass
    result: uint8 = TWDR.value
    return result


@inline
def i2c_read_nack() -> uint8:
    # Read last byte and send NACK (signals end of transfer to slave)
    TWCR.value = 0x84   # TWINT(7)|TWEN(2) - TWEA=0 -- generate NACK
    while TWCR[7] == 0:
        pass
    last_byte: uint8 = TWDR.value
    return last_byte


@inline
def i2c_write_to(addr: uint8, data: uint8) -> uint8:
    # Send START, SLA+W, one data byte, STOP.
    # Returns 1 if ACK received for address, 0 if NACK (no device).
    # Only sends the data byte if the address was acknowledged.
    TWCR.value = 0xA4           # START: TWINT|TWSTA|TWEN
    while TWCR[7] == 0:
        pass
    start_status: uint8 = TWSR.value & 0xF8
    if start_status == 0x08:    # START OK
        TWDR.value = addr << 1  # SLA+W
        TWCR.value = 0x84
        while TWCR[7] == 0:
            pass
        ack_status: uint8 = TWSR.value & 0xF8
        if ack_status == 0x18:  # ACK received
            TWDR.value = data
            TWCR.value = 0x84
            while TWCR[7] == 0:
                pass
            TWCR.value = 0x94   # STOP
            return 1
    TWCR.value = 0x94           # STOP on any failure
    return 0


@inline
def i2c_read_from(addr: uint8) -> uint8:
    # Send START, SLA+R, read one byte with NACK, STOP.
    # Returns the byte read, or 0 if address NACK (no device).
    TWCR.value = 0xA4               # START
    while TWCR[7] == 0:
        pass
    start_status: uint8 = TWSR.value & 0xF8
    if start_status == 0x08:        # START OK
        sla_r: uint8 = (addr << 1) | 1  # SLA+R
        TWDR.value = sla_r
        TWCR.value = 0x84
        while TWCR[7] == 0:
            pass
        ack_status: uint8 = TWSR.value & 0xF8
        if ack_status == 0x40:      # SLA+R ACK received
            TWCR.value = 0x84       # TWINT|TWEN (TWEA=0 -- NACK for last byte)
            while TWCR[7] == 0:
                pass
            rx_byte: uint8 = TWDR.value
            TWCR.value = 0x94       # STOP
            return rx_byte
    TWCR.value = 0x94               # STOP on any failure
    return 0
