# DHT22 Sensor -- MicroPython style on Arduino Uno
#
# Demonstrates DHT22 float decoding: humidity() and temperature() return float
# values (e.g. 55.1, 23.5, -5.0) via the AVR soft-float runtime.
#
# UART output (9600 baud):
#   Boot:  "DHT22 ready"
#   OK:    "H: XX.X  T: XX.X"
#   Error: "read error"

from machine import Pin, UART
from utime import sleep_ms
from dht import DHT22

uart     = UART(0, 9600)
led      = Pin(13, Pin.OUT)
sensor   = DHT22(Pin(2, Pin.IN))

uart.println("DHT22 ready")

while True:
    sensor.measure()
    if sensor.failed:
        print("read error")
        led.low()
    else:
        print("H: ", sensor.humidity(), "  T: ", sensor.temperature(), sep="")
        led.high()
        sleep_ms(100)
        led.low()

    sleep_ms(2000)
