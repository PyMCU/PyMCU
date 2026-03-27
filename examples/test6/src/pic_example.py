# PIC16F18877: LED toggle on button press via IOC interrupt
# Demonstrates: Pin HAL, const parameters, Pin.irq(), @interrupt
from pymcu.hal.gpio import Pin
from pymcu.types import interrupt

# LED on RB0 (start ON)
led = Pin("RB0", Pin.OUT, value=1)

# IOC interrupt handler — vector 0x04 on PIC16F18877
@interrupt(0x04)
def on_change():
    led.toggle()  # led is defined in main() — pymcuc flattens scope

def main():
    # Button on RB4 with internal pull-up, falling edge IOC
    btn = Pin("RB4", Pin.IN, pull=Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)

    while True:
        pass
