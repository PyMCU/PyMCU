# interrupt-counter

External interrupt (INT0) press counter.

## What it does

`btn.irq(Pin.IRQ_FALLING, int0_isr)` configures the INT0 hardware interrupt on
**PD2** (falling edge), enables the interrupt mask, and sets `SEI` — no manual
`EICRA`/`EIMSK` writes. The ISR sets an atomic flag in `GPIOR0`; the main loop
clears it, increments a counter, toggles the LED, and sends the raw count over
UART.

## Hardware

- Arduino Uno @ 16 MHz
- Button on **PD2** (INT0), active-low, internal pull-up
- LED on **PB5** (built-in)
- Serial terminal at **9600 baud**

## Key concepts

- `Pin.irq()` for hardware external interrupts
- `GPIOR0` SBI/CBI atomic flag pattern (ISR sets, main clears)

## Build & flash

```bash
cd examples/avr/interrupt-counter
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
