# t-flag-demo

The low-level T-flag error ABI behind `raise` / `return`.

## What it does

A minimal demo of PyMCU's lightweight error-propagation ABI, which uses the AVR
**T flag** in SREG instead of `setjmp`/`longjmp`:

- `raise`        → `LDI R22, code; SET; RET` (3 instructions, no longjmp)
- successful `return` → `CLT; RET`

`safe_div()` raises `ValueError` on divide-by-zero; `safe_sub()` raises on
underflow. The happy path writes the results over UART.

This is the implementation detail under [`error-handling`](../error-handling) —
look here to understand *why* exceptions are nearly free on AVR (~10 instructions
→ 3 per `raise` vs. the SJLJ approach).

## Hardware

- ATmega328P @ 16 MHz, UART at **9600 baud**

## Key concepts

- T-flag-based `CanFail` calling convention
- Flash savings vs. setjmp/longjmp exceptions

## Build & flash

```bash
cd examples/avr/t-flag-demo
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
