# error-handling

Exception-based error handling with `try` / `except` / `raise`.

## What it does

Demonstrates structured error handling using PyMCU's exception support. Four
scenarios each raise and catch an exception:

- **A** — `validate_sensor()` raises `ValueError` for a value out of range
- **B** — `safe_divide()` raises `ValueError` on divide-by-zero
- **C** — `bounds_check()` raises `IndexError` for an out-of-bounds index
- **D** — `raise_by_type()` shows multiple `except` clauses (`ValueError` vs `TypeError`)

## Hardware

- Arduino Uno @ 16 MHz
- Serial terminal at **9600 baud**

## Expected output

```
ERR-HANDLING
A:out-of-range
B:div-by-zero
C:out-of-bounds
D:type-err
DONE
```

## Key concepts

- `try` / `except <Type>` / `raise` on bare metal
- `pymcu.exceptions` (`ValueError`, `TypeError`, `IndexError`)

> See also [`t-flag-demo`](../t-flag-demo) for the low-level T-flag error ABI
> that backs `raise`/`return`.

## Build & flash

```bash
cd examples/avr/error-handling
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
