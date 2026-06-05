# timer-ctc

Timer1 CTC mode for a precise 1 Hz interrupt.

## What it does

Timer1 in **CTC** (Clear Timer on Compare) mode with prescaler 256 and compare
value 62499 produces an exact 1.0 s period. `t.irq(handler, Timer.IRQ_COMPA)`
registers the handler at the TIMER1_COMPA vector and enables `OCIE1A` + `SEI`.
The ISR flags the main loop, which toggles the LED and sends `C` over UART.

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (D13)
- Serial terminal at **9600 baud**

## Key concepts

- CTC mode + compare match interrupt (vs. plain overflow)
- `set_compare()` for an exact period

## Build & flash

```bash
cd examples/avr/timer-ctc
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
