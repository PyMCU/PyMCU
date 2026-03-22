# PyMCU DHT11 sensor example — Arduino Uno
#
# Demonstrates:
#   - Board abstractions:  pymcu.boards.arduino_uno
#   - Stdlib driver:       pymcu.drivers.dht11 (arch-neutral ZCA, same pattern as Pin/UART)
#   - Multi-file project:  main.py is the only local file; drivers live in the stdlib
#
# sensor = DHT11("PD2")   → D2 = "PD2" bound at compile time
# sensor.read()        → match __CHIP__.arch → match pin_name (const[str]) → direct protocol
#
# Wiring:
#   DHT11 DATA → Arduino D2 (4.7 kΩ pull-up to +5 V)
#   DHT11 VCC  → +5 V    DHT11 GND → GND
#
# UART output (9600 baud):
#   Boot:  "DHT11\n"
#   OK:    "H:XX T:XX\n"
#   Error: "ERR\n"
from whisnake.types import uint8, uint16
from whisnake.boards.arduino_uno import D2, LED_BUILTIN
from whisnake.hal.gpio import Pin
from whisnake.hal.uart import UART
import time
from dht11 import DHT11


def nibble_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart     = UART(9600)
    led      = Pin(LED_BUILTIN, Pin.OUT)
    data_pin = Pin(D2, Pin.IN)   # DHT11 data pin — driver switches direction at runtime
    sensor   = DHT11(data_pin)   # ZCA: sensor.pin tracks data_pin.name compile-time

    print("DHT11")

    while True:
        sensor.measure()

        if sensor.failed:
            print("ERR")
            led.low()
        else:
            print("H:", sensor.humidity, " T:", sensor.temperature, sep="")
            led.high()

        time.sleep_ms(2000)
