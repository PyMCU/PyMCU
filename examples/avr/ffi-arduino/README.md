# ffi-arduino

Arduino-style utility functions in C, called from PyMCU via FFI.

## What it does

Wraps the famous Arduino helpers `map()` and `constrain()` (plus an
`adc_to_pwm()` converter) as portable C in `c_src/arduino_utils.c`, then calls
them through `@extern`. Useful when porting Arduino sketches that rely on these
macros.

## Expected output (UART, 9600 baud)

```
ARDUINO
M:7F   F:FF   Z:00      <- arduino_map(...)
H:C8   L:0A             <- arduino_constrain(...)
P:7F   T:FF             <- adc_to_pwm(...)
OK
```

## Key concepts

- 32-bit intermediate math in C linked into AVR firmware
- `uint16` argument passing across the FFI boundary

## Build & flash

```bash
cd examples/avr/ffi-arduino
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
