# ffi-abi

Validate the PyMCU → C calling convention (ABI) through FFI probes.

## What it does

Each `@extern` C function is an ABI probe that echoes one of its arguments back.
By calling `f(10, 20, 30)` and checking which value returns, the example proves
PyMCU places each positional argument in the correct AVR register:

```
arg0 -> R24    arg1 -> R22    arg2 -> R20    arg3 -> R18    return -> R24
```

The non-commutative `abi_sub8(a, b) = a - b` verifies argument *order*, and a
final test confirms a local in a callee-saved register survives a C call.

## Expected output (UART, 9600 baud)

```
ABI
0:0A   1:14   2:1E   3:04   S:46   K:AA   OK
```

## Key concepts

- AVR avr-gcc register calling convention
- Argument order and callee-saved register preservation across FFI calls

## Build & flash

```bash
cd examples/avr/ffi-abi
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
