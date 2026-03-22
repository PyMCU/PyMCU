# PIC16F18877: Button press toggles LED via interrupt-on-change
# Demonstrates: Pin HAL, @interrupt, pull-up, IRQ, toggle
#
# Hardware: PIC16F18877 (Curiosity HPC or custom board)
#   - LED on RB0 (active high)
#   - Push button on RB4 (active low, uses internal pull-up)
#
# How it works:
#   The LED starts ON. When the button is pressed (falling edge on RB4),
#   the IOC interrupt fires and toggles the LED. The main loop does nothing
#   — all logic is in the ISR. This is a common pattern for low-power designs
#   where the MCU can sleep between button presses.
#
from whisnake.hal.gpio import Pin
from whisnake.types import interrupt

@interrupt(0x04)
def on_button_press():
    led.toggle()

def main():
    global led

    led = Pin("RB0", Pin.OUT, value=1)
    btn = Pin("RB4", Pin.IN, pull=Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)

    while True:
        pass
