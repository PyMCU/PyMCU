# MicroPython-style Pin.irq with handler(pin) -- Arduino Uno
#
# Demonstrates Pin.irq() with the standard MicroPython API:
#   handler receives the Pin instance as argument.
# The compiler synthesizes a parameterless ISR wrapper at compile time.
#
# Hardware: Button on D2 (PD2 = INT0), active low, external pull-up.
# UART: 9600 baud, TX on D1.
#
# Output:
#   "PIN IRQ MP\n"   -- boot banner
#   "PRESSED\n"      -- sent each time the button is pressed

from machine import Pin, UART
from pymcu.types import uint8

uart = UART(0, 9600)
led  = Pin(13, Pin.OUT)
btn  = Pin(2, Pin.IN)

flag: uint8 = 0

def on_press(pin: Pin):
    global flag
    if pin.value() == 0:
        flag = 1

btn.irq(on_press, Pin.IRQ_FALLING)

uart.println("PIN IRQ MP")

while True:
    if flag == 1:
        flag = 0
        led.toggle()
        uart.println("PRESSED")
