# PyMCU -- compat-cp-gpio: CircuitPython digitalio list-comp fixture
#
# Verifies:
#   1. [DigitalInOut(p) for p in (...)] unrolls CT ZCA instances from list comp
#   2. for pin in outs: pin.direction = ... (plain for-in over ZCA instance array)
#   3. for bit, pin in enumerate(outs): pin.value = ... (enumerate over ZCA array)
#
# After main() runs:
#   - DDRD bits 5-7 are configured as outputs via Direction.OUTPUT
#   - pattern=1 applied: PD5=HIGH (bit0=1), PD6=LOW (bit1=0), PD7=LOW (bit2=0)
#
import board
import digitalio


def main():
    outs = [digitalio.DigitalInOut(p) for p in (board.D5, board.D6, board.D7)]
    for pin in outs:
        pin.direction = digitalio.Direction.OUTPUT

    pattern: uint8 = 1
    for bit, pin in enumerate(outs):
        pin.value = (pattern >> bit) & 1

    while True:
        pass
