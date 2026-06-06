from machine import Pin, ADC
from utime import sleep_ms
from lm35 import LM35

sensor = LM35(ADC(Pin("PC0")))   # A0 = PC0
print("LM35 ready")

while True:
    print("T: ", sensor.temperature(), " C", sep="")
    sleep_ms(100)
