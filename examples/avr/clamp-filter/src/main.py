# ATmega328P: Clamp filter — 3-argument function + multi-level call chain
#
# Demonstrates:
#   - 3-argument non-inline functions (GCC AVR calling convention: R24, R22, R20)
#   - Multiple return paths in a function (early return from if/else)
#   - Two-level call chain: main -> clamp(val, lo, hi) -> (returns uint8)
#   - Linear predictor: predict(prev, curr) computes simple 1st-order prediction
#     and returns it clamped with a 3-arg call to clamp()
#   - UART read/write round-trip with transformed echo
#
# Protocol:
#   Boot: "CLAMP FILTER\n"
#   For each received byte B:
#     1. Clamp B to [32, 126]   (printable ASCII range)
#     2. Predict next value as: (prev + clamped) / 2  (rounded down)
#     3. Echo: clamped_byte, predicted_byte, '\n'
#
# Tests:
#   - clamp(val, lo, hi): 3 args -> R24, R22, R20; return -> R24
#   - predict(prev, curr): 2 args, calls clamp internally with 3 args
#   - uint8 shift right for division by 2 (>> 1)
#   - Uint8 additions without overflow (values stay in 32-126 range)
#   - Multiple non-inline functions in same compilation unit
#
# Hardware: Arduino Uno
#   UART TX/RX on PD1/PD0 at 9600 baud
#
from whisnake.types import uint8
from whisnake.hal.uart import UART


# Clamp val to [lo, hi]. Tests 3-argument calling convention.
def clamp(val: uint8, lo: uint8, hi: uint8) -> uint8:
    if val < lo:
        return lo
    if val > hi:
        return hi
    return val


# Simple 1st-order linear predictor: returns average of prev and curr,
# clamped to [32, 126]. Tests a 2-arg function that calls a 3-arg function.
def predict(prev: uint8, curr: uint8) -> uint8:
    avg: uint8 = (prev + curr) >> 1
    return clamp(avg, 32, 126)


def main():
    uart = UART(9600)
    uart.println("CLAMP FILTER")

    prev: uint8 = 64   # initial "previous" value for predictor

    while True:
        raw: uint8 = uart.read()

        clamped:   uint8 = clamp(raw, 32, 126)
        predicted: uint8 = predict(prev, clamped)

        uart.write(clamped)
        uart.write(predicted)
        uart.write('\n')

        prev = clamped
