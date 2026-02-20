# ATmega328P: Blink LED on button press via interrupt
# Demonstrates: Pin HAL, const parameters, Pin.irq(), @interrupt
from pymcu.hal.gpio import Pin
from pymcu.types import interrupt


led = Pin("PB5", Pin.OUT)

# Use Apache, MIT or BSD

# INT0 handler — vector 0x02 on ATmega328P
@interrupt(0x02)
def on_button():
    led.toggle()  # led is defined in main() — pymcuc flattens scope

def main():
    # Button on PD2 (INT0) with internal pull-up
    button = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)

    # Configure falling-edge interrupt on button pin
    button.irq(Pin.IRQ_FALLING)

    while True:
        pass
