# ffi-dsp

Multi-file C interop: two C sources linked into one firmware.

## What it does

A more involved FFI setup with two C files (`math_utils.c`, `filter.c`) and six
`@extern` declarations covering clamp, lerp, scale, IIR smoothing, and deadband
DSP primitives. Results are verified over UART.

## Expected output (UART, 9600 baud)

```
FFIDSP
C:64   L:64   K:64   E:57   D:00   B:1E
OK
```

## Key concepts

- Multiple C source files in one `[tool.pymcu.ffi]` build
- Functions with 2 and 3 arguments across the FFI boundary

## Build & flash

```bash
cd examples/avr/ffi-dsp
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
