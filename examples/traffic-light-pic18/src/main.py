# PIC18F45K50: Traffic light controller
# Demonstrates: Multiple Pin outputs, Pin.value() read, state machine, functions
#
# Hardware: PIC18F45K50 (DM164127 or custom board)
#   - Red LED    on RB0
#   - Yellow LED on RB1
#   - Green LED  on RB2
#   - Pedestrian button on RB4 (active low, external pull-up resistor)
#
# State machine:
#   GREEN → (button press) → YELLOW → (button press) → RED → (button press) → GREEN
#   Each transition requires a button press on RB4.
#   This demonstrates real-time input polling with multiple outputs.
#
from whisnake.hal.gpio import Pin
from whisnake.types import uint8

# Traffic light states
STATE_GREEN  = 0
STATE_YELLOW = 1
STATE_RED    = 2

def wait_press(btn: uint8):
    # Wait for button press (active low) then release (debounce)
    while btn.value() == 1:
        pass
    while btn.value() == 0:
        pass

def main():
    red    = Pin("RB0", Pin.OUT)
    yellow = Pin("RB1", Pin.OUT)
    green  = Pin("RB2", Pin.OUT)
    button = Pin("RB4", Pin.IN)

    state: uint8 = STATE_GREEN

    # Initial state: green on
    green.high()

    while True:
        wait_press(button)

        if state == STATE_GREEN:
            green.low()
            yellow.high()
            state = STATE_YELLOW

        elif state == STATE_YELLOW:
            yellow.low()
            red.high()
            state = STATE_RED

        elif state == STATE_RED:
            red.low()
            green.high()
            state = STATE_GREEN
