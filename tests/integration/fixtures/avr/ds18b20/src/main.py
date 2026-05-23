# PyMCU -- ds18b20: DS18B20 1-Wire driver compile and no-sensor test fixture
#
# Output on UART (9600 baud):
#   "DS\n"      -- boot banner
#   "ERR\n"     -- no device present on bus (PD2 floating/high)
#
# The test verifies the driver compiles, boots, attempts a read, and
# outputs ERR when no sensor is present (bus stays HIGH = no presence pulse).
#
from pymcu.types import int16
from pymcu.hal.uart import UART
from pymcu.drivers.ds18b20 import DS18B20


def main():
    uart = UART(9600)
    uart.println("DS")

    sensor = DS18B20("PD2")
    while True:
        raw: int16 = sensor.read()
        if raw == -32768:
            uart.println("ERR")
        else:
            uart.println("OK")
