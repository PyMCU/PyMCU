# inheritance-zca

Zero-cost class inheritance and function overloading.

## What it does

- **Inheritance:** `LED(GPIODevice)` inherits `on()`/`off()`/`read()` and adds
  its own `blink_code()` method — all resolved and inlined at compile time (ZCA),
  so there is no vtable or runtime cost.
- **Overloading:** two `encode()` functions with the same name dispatch by
  argument type (`uint8` vs `uint16`).

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (built-in)
- Serial terminal at **9600 baud**

## Expected output

```
IZ
A:01      <- LED.read() after on() (output latch high)
B:AB      <- encode(uint8 = 0xAB)
C:1234    <- encode(uint16 = 0x1234)
```

## Key concepts

- Single-level inheritance with `super()` resolution, inlined
- Type-based function overloading

## Build & flash

```bash
cd examples/avr/inheritance-zca
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
