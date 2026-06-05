# timer-poll

Polling a timer overflow flag instead of using an interrupt.

## What it does

Polls Timer0's overflow flag `TIFR0[0]` (TOV0) in the main loop, clearing it by
writing a 1 (the AVR convention for timer flag bits). After 244 overflows
(~1 second at prescaler 256) it toggles the LED and sends `T` over UART.

Contrast with [`timer-interrupt`](../timer-interrupt), which does the same thing
with an ISR.

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (D13)
- Serial terminal at **9600 baud**

## Key concepts

- Flag polling vs. interrupts
- Write-1-to-clear timer flags

## Build & flash

```bash
cd examples/avr/timer-poll
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
