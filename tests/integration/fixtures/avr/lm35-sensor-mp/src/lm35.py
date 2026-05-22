# LM35 precision temperature sensor driver (MicroPython style)
#
# API:
#   sensor = LM35(ADC("A0"))   # or LM35("A0") -- channel string also accepted
#   sensor.read()              -- raw ADC count (0-1023)
#   sensor.temperature()       -- degrees Celsius as float (e.g. 24.8)
#
# Conversion formula (5 V reference, 10-bit ADC):
#   V_out  = ADC_raw * 5000.0 / 1024  (mV)
#   Temp_C = V_out / 10.0             (LM35 outputs 10 mV/C)
#   => Temp_C = ADC_raw * 0.4882813

from pymcu.types import uint16, inline
from machine import ADC as _ADC


class LM35:
    @inline
    def __init__(self, pin):
        self._adc = _ADC(pin)

    @inline
    def read(self) -> uint16:
        return self._adc.read()

    @inline
    def temperature(self) -> float:
        raw: uint16 = self._adc.read()
        return raw * 0.4882813
