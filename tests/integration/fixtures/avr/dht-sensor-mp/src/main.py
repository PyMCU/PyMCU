# DHT11 Sensor -- MicroPython style on Arduino Uno
#
# Demonstrates:
#   machine.Pin  -- integer pin numbers (D2 = PD2 sensor, D13 = PB5 LED)
#   machine.UART -- serial output at 9600 baud
#   utime        -- sleep_ms() between measurements
#   local driver -- dht.DHT11 reads temperature and humidity
#
# MicroPython equivalent (runs unmodified on any MicroPython board with DHT):
#   from machine import Pin, UART
#   from utime import sleep_ms
#   from dht import DHT11
#   sensor = DHT11(Pin(2, Pin.IN))
#
# Wiring:
#   DHT11 DATA -> D2  (4.7 kohm pull-up to +5 V recommended)
#   DHT11 VCC  -> +5 V
#   DHT11 GND  -> GND
#   LED:    built-in on D13 (no wiring needed)
#
# UART output (9600 baud):
#   Boot:  "DHT11 ready"
#   OK:    "H: XX  T: XX"
#   Error: "read error"

from machine import Pin, UART
from utime import sleep_ms
from dht import DHT11


uart     = UART(0, 9600)
led      = Pin(13, Pin.OUT)
sensor   = DHT11(Pin(2, Pin.IN))
    
uart.println("DHT11 ready")

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
