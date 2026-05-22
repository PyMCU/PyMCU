# DHT11/DHT22 temperature and humidity driver (MicroPython style)
#
# API matches micropython-lib dht.py:
#   sensor = DHT11(Pin(2, Pin.IN))   # or DHT22(...)
#   sensor.measure()         -- read sensor (updates internal state)
#   sensor.humidity()        -- humidity value
#   sensor.temperature()     -- temperature value
#   sensor.failed            -- True if read failed or checksum mismatch
#                               (PyMCU extension: real MicroPython raises Exception)
#
# DHT11: humidity() -> integer % RH,  temperature() -> integer C
# DHT22: humidity() -> float % RH,    temperature() -> float C (signed)
#
# Wiring:
#   DATA -> pin passed in (e.g. D2), with 4.7 kohm pull-up to +5 V
#   VCC  -> +5 V  (DHT11) or +3.3/5 V (DHT22)
#   GND  -> GND
#
# Protocol (single-wire, 40-bit):
#   1. MCU pulls low >= 18 ms  (start signal)
#   2. MCU releases, sensor pulls low ~80 us then high ~80 us (ACK)
#   3. 40 bits: each bit starts with ~50 us LOW; HIGH duration decides value:
#      count > 35 loop iterations = 1, else = 0
#   4. Checksum = lower 8 bits of sum of the first 4 bytes

from pymcu.types import uint8, inline
from machine import Pin as _Pin, time_pulse_us
from pymcu.time import delay_ms, delay_us


class DHTBase:
    @inline
    def __init__(self, pin: _Pin):
        self._pin      = pin
        self.failed    = False
        self._hum_int  = 0
        self._hum_dec  = 0
        self._temp_int = 0
        self._temp_dec = 0

    @inline
    def measure(self):
        # Start signal: hold low >= 18 ms, then pull high 20-40 us before
        # releasing to input.  The high() call leaves PORT bit = 1 so that
        # mode(IN) enables the AVR internal pull-up (PORT=1, DDR=0).
        self._pin.mode(_Pin.OUT)
        self._pin.low()
        delay_ms(18)
        self._pin.high()
        delay_us(30)
        self._pin.mode(_Pin.IN)

        # ACK: sensor pulls low ~80 us, then high ~80 us
        if time_pulse_us(self._pin, 0, 1000) < 0:
            self.failed = True
            return
        if time_pulse_us(self._pin, 1, 1000) < 0:
            self.failed = True
            return

        # Read five bytes: hum_int, hum_dec, temp_int, temp_dec, checksum
        hum_int:  uint8 = self._read_byte()
        hum_dec:  uint8 = self._read_byte()
        temp_int: uint8 = self._read_byte()
        temp_dec: uint8 = self._read_byte()
        checksum: uint8 = self._read_byte()

        expected: uint8 = (hum_int + hum_dec + temp_int + temp_dec) & 0xFF
        if checksum != expected:
            self.failed = True
            return

        self.failed    = False
        self._hum_int  = hum_int
        self._hum_dec  = hum_dec
        self._temp_int = temp_int
        self._temp_dec = temp_dec

    @inline
    def _read_byte(self) -> uint8:
        result: uint8 = 0
        bit: uint8 = 0
        while bit < 8:
            # pulse_in(1) waits through the ~50 us bit-start LOW then
            # measures the HIGH duration: ~26 us = 0, ~70 us = 1.
            # Timeout returns -1 (int16) which is < 40, so the bit reads as 0;
            # the checksum will catch any corrupted frame.
            high_dur = time_pulse_us(self._pin, 1, 1000)
            result = result << 1
            if high_dur > 40:
                result = result | 1
            bit = bit + 1
        return result


class DHT11(DHTBase):
    @inline
    def humidity(self) -> uint8:
        return self._hum_int

    @inline
    def temperature(self) -> uint8:
        return self._temp_int


class DHT22(DHTBase):
    @inline
    def humidity(self) -> float:
        hi_b: uint8 = self._hum_int
        lo_b: uint8 = self._hum_dec
        raw: uint16 = 0
        raw = raw | hi_b
        raw = raw << 8
        raw = raw | lo_b
        return raw * 0.1

    @inline
    def temperature(self) -> float:
        ti_b: uint8 = self._temp_int
        td_b: uint8 = self._temp_dec
        sign: uint8 = ti_b & 0x80
        masked: uint8 = ti_b & 0x7F
        raw: uint16 = 0
        raw = raw | masked
        raw = raw << 8
        raw = raw | td_b
        t: float = raw * 0.1
        if sign:
            t = 0.0 - t
        return t
