# pin-irq

Minimal external interrupt: INT0 falling-edge on PD2.

## What it does

The simplest possible `Pin.irq()` demo. `btn.irq(Pin.IRQ_FALLING, on_press)`
configures INT0 for falling-edge triggering and enables global interrupts. The
ISR sets an atomic `GPIOR0` flag; the main loop counts presses and sends the raw
count byte over UART.

A good companion to [`interrupt-counter`](../interrupt-counter) — start here for
the bare mechanics of pin interrupts.

## Hardware

- Arduino Uno @ 16 MHz
- Button on **PD2** (INT0), active-low, pull-up
- Serial terminal at **9600 baud**

## Key concepts

- `compile_isr()` auto-registers the handler at the INT0 vector — no
  `@interrupt` decorator needed

## Build & flash

```bash
cd examples/avr/pin-irq
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
