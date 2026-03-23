# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.chips import __CHIP__


# noinspection PyProtectedMember
class I2C:
    """Hardware I2C (TWI) controller or peripheral, zero-cost abstraction.

    The operating role is determined by the ``addr`` argument at construction
    (mirroring the Arduino Wire.begin() API):

        i2c = I2C()        # controller mode (no address)
        i2c = I2C(0x42)    # peripheral mode, respond to address 0x42

    All methods are @inline; the mode check in each one folds at compile time
    so only the relevant code is emitted.

    **Controller** status code constants::

        I2C.START, I2C.SLA_ACK, I2C.SLA_NACK, I2C.DATA_ACK, I2C.SLA_R_ACK

    **Peripheral** status code constants::

        I2C.ADDR_WRITE, I2C.DATA_RECEIVED, I2C.LAST_RECEIVED,
        I2C.STOP_RECEIVED, I2C.ADDR_READ, I2C.DATA_SENT, I2C.LAST_SENT

    Controller context manager: ``with i2c:`` auto-sends START/STOP.

    Peripheral polling example::

        i2c = I2C(0x42)
        while True:
            if i2c.ready():
                st = i2c.status()
                if st == I2C.ADDR_WRITE:
                    i2c.acknowledge()
                elif st == I2C.DATA_RECEIVED:
                    byte = i2c.read()
                    i2c.acknowledge()
                elif st == I2C.STOP_RECEIVED:
                    i2c.acknowledge()
                elif st == I2C.ADDR_READ:
                    i2c.write(response_byte)
                    i2c.acknowledge()
                elif st == I2C.DATA_SENT:
                    i2c.write(next_byte)
                    i2c.acknowledge()
                elif st == I2C.LAST_SENT:
                    i2c.acknowledge()
    """

    # Controller TWI status codes.
    START     = 0x08   # START condition transmitted OK
    SLA_ACK   = 0x18   # SLA+W sent, ACK received  (device present)
    SLA_NACK  = 0x20   # SLA+W sent, NACK received (no device)
    DATA_ACK  = 0x28   # Data byte sent, ACK received
    SLA_R_ACK = 0x40   # SLA+R sent, ACK received

    # Peripheral TWI status codes.
    ADDR_WRITE    = 0x60   # Own SLA+W received, ACK returned
    DATA_RECEIVED = 0x80   # Data byte received, ACK returned
    LAST_RECEIVED = 0x88   # Data byte received, NACK returned
    STOP_RECEIVED = 0xA0   # STOP or repeated START received
    ADDR_READ     = 0xA8   # Own SLA+R received, ACK returned
    DATA_SENT     = 0xB8   # Data byte sent, ACK received (more bytes wanted)
    LAST_SENT     = 0xC0   # Data byte sent, NACK received (controller done)

    def __init__(self, addr: uint8 = 0, general_call: uint8 = 0):
        """Initialize I2C.

        addr=0 (default): controller mode at 100 kHz.
        addr>0:           peripheral mode, respond to that 7-bit address.
        general_call:     peripheral only; set to 1 to also respond to the
                          general call address (0x00).
        """
        match __CHIP__.arch:
            case "avr":
                if addr == 0:
                    from whipsnake.hal._i2c.avr import i2c_init
                    i2c_init()
                    self._mode = "c"
                else:
                    from whipsnake.hal._i2c.avr import i2c_peripheral_init
                    i2c_peripheral_init(addr, general_call)
                    self._mode = "p"

    # ---- Controller methods --------------------------------------------------

    @inline
    def ping(self, addr: uint8) -> uint8:
        """Controller: return 1 if a device responds at the given 7-bit address."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_ping
                    return i2c_ping(addr)
            case _:
                return 0
        return 0

    @inline
    def start(self) -> uint8:
        """Controller: send a START condition. Returns the TWI status byte."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_start
                    return i2c_start()
            case _:
                return 0
        return 0

    @inline
    def stop(self):
        """Controller: send a STOP condition and release the bus."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_stop
                    i2c_stop()

    @inline
    def end(self):
        """Controller: send a STOP condition. Alias for stop()."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_stop
                    i2c_stop()

    @inline
    def write(self, data: uint8) -> uint8:
        """Controller: send one byte; returns TWI status (ACK/NACK).
        Peripheral: load TWDR with data to send to controller on next clock.
        """
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_write
                    return i2c_write(data)
                else:
                    from whipsnake.hal._i2c.avr import i2c_peripheral_write
                    i2c_peripheral_write(data)
            case _:
                return 0
        return 0

    @inline
    def read_ack(self) -> uint8:
        """Controller: receive one byte and send ACK (more bytes to follow)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_read_ack
                    return i2c_read_ack()
            case _:
                return 0
        return 0

    @inline
    def read_nack(self) -> uint8:
        """Controller: receive one byte and send NACK (last byte in transfer)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_read_nack
                    return i2c_read_nack()
            case _:
                return 0
        return 0

    @inline
    def write_to(self, addr: uint8, data: uint8) -> uint8:
        """Controller: send START, SLA+W, one data byte, STOP.

        Returns 1 on ACK, 0 on NACK.
        """
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_write_to
                    return i2c_write_to(addr, data)
            case _:
                return 0
        return 0

    @inline
    def read_from(self, addr: uint8) -> uint8:
        """Controller: send START, SLA+R, read one byte with NACK, STOP.

        Returns byte read, or 0 on NACK.
        """
        match __CHIP__.arch:
            case "avr":
                if self._mode == "c":
                    from whipsnake.hal._i2c.avr import i2c_read_from
                    return i2c_read_from(addr)
            case _:
                return 0
        return 0

    # ---- Peripheral methods --------------------------------------------------

    @inline
    def ready(self) -> uint8:
        """Peripheral: return 1 if TWI has completed an operation (TWINT=1)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "p":
                    from whipsnake.hal._i2c.avr import i2c_peripheral_ready
                    return i2c_peripheral_ready()
            case _:
                return 0
        return 0

    @inline
    def status(self) -> uint8:
        """Peripheral: return current TWI status code (TWSR & 0xF8)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "p":
                    from whipsnake.hal._i2c.avr import i2c_peripheral_status
                    return i2c_peripheral_status()
            case _:
                return 0
        return 0

    @inline
    def acknowledge(self):
        """Peripheral: release TWINT and send ACK (tell controller to continue)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "p":
                    from whipsnake.hal._i2c.avr import i2c_peripheral_acknowledge
                    i2c_peripheral_acknowledge()

    @inline
    def nack(self):
        """Peripheral: release TWINT and send NACK (last byte)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "p":
                    from whipsnake.hal._i2c.avr import i2c_peripheral_nack
                    i2c_peripheral_nack()

    @inline
    def read(self) -> uint8:
        """Peripheral: read the byte the controller just sent (from TWDR)."""
        match __CHIP__.arch:
            case "avr":
                if self._mode == "p":
                    from whipsnake.hal._i2c.avr import i2c_peripheral_read
                    return i2c_peripheral_read()
            case _:
                return 0
        return 0

    # ---- Context manager (controller) ---------------------------------------

    def __enter__(self):
        """Controller: send a START condition."""
        self.start()

    def __exit__(self):
        """Controller: send a STOP condition."""
        self.stop()
