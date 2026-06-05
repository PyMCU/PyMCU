# timer-interrupt

Timer1 overflow interrupt blinking an LED at ~1 Hz.

## What it does

`Timer(1, 256).irq(on_overflow)` registers an ISR at the Timer1 overflow vector
and enables `TOIE1` + `SEI` automatically. At prescaler 256 the 16-bit timer
overflows about every 1.05 s; the ISR sets a `GPIOR0` flag and the main loop
toggles the LED and sends `T` over UART.

A simpler counterpart to [`timer-ctc`](../timer-ctc) (overflow vs. compare-match).

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (D13)
- Serial terminal at **9600 baud**

## Key concepts

- `Timer.irq()` overflow interrupt registration
- `GPIOR0` atomic flag pattern

## Build & flash

```bash
cd examples/avr/timer-interrupt
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
