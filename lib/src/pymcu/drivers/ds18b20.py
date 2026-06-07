# DS18B20 temperature sensor driver
# Zero-cost abstraction -- mirrors the DHT11 driver pattern.
#
# Usage:
#   from pymcu.drivers.ds18b20 import DS18B20
#
#   sensor = DS18B20("PD2")         # compile-time pin binding (PORTD only)
#   raw    = sensor.read()           # int16: raw 12-bit value (1/16 C), -32768 on error
#
# To convert to degrees Celsius (integer):
#   if raw != -32768:
#       temp_c = raw >> 4            # integer part
#
# To add a new architecture, add a case to DS18B20.read() and create
# lib/src/pymcu/drivers/_ds18b20/<arch>.py following the avr.py template.
from pymcu.chips import __CHIP__
from pymcu.types import int16, inline


class DS18B20:

    @inline
    def __init__(self, pin: str):
        self.name = pin

    @inline
    def read(self) -> int16:
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._ds18b20.avr import _avr_read
                return _avr_read(self.name)
            case _:
                return -32768

    @inline
    def read_celsius_x16(self) -> int16:
        return self.read()
