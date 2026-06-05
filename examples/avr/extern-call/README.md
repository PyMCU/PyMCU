# extern-call

Call C functions from PyMCU firmware via the `@extern` FFI.

## What it does

Declares two C functions with `@extern("symbol")` and calls them from `main()`.
The C bodies live in `c_src/math_helper.c`, compiled with `avr-gcc` and linked
into the firmware by `avr-ld`. The `[tool.pymcu.ffi]` section in
`pyproject.toml` lists the C sources, include dirs, and cflags.

## Layout

```
src/main.py              # @extern declarations + calls
c_src/math_helper.c/.h   # C implementation
pyproject.toml           # [tool.pymcu.ffi] build config
```

## Expected output (UART, 9600 baud)

```
EXTERN
M:1E     <- c_mul8(3, 10)       = 30  = 0x1E
S:FF     <- c_add_saturate(200,100) = 255 = 0xFF (saturated)
A:0A     <- c_add_saturate(4, 6) = 10  = 0x0A
OK
```

## Key concepts

- `@extern` declaration (body is a stub the compiler ignores)
- `[tool.pymcu.ffi]` C build integration

## Build & flash

```bash
cd examples/avr/extern-call
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
