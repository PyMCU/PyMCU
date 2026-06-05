# sensor-dashboard

A multi-interrupt sensor monitor with min/max tracking and live display modes.

## What it does

Samples ADC0 (**PC0/A0**) roughly every 262 ms (driven by a Timer0 overflow ISR
counting 64 ticks). It tracks lifetime min/max and a running EMA
(`avg = (avg + raw) >> 1`), blinks the LED on each sample, and lets an INT0
button (**PD2**) toggle between a verbose and a compact UART display.

## Hardware

- Arduino Uno @ 16 MHz
- ADC input on **PC0 (A0)**
- LED on **PB5** (D13)
- Button on **PD2** (INT0, falling edge)
- Serial terminal at **9600 baud**

## Expected output

- Verbose: `R:HH A:HH L:HH H:HH`
- Compact: `HH`

## Key concepts

- Two simultaneous interrupt sources (Timer0 OVF + INT0)
- `GPIOR0` bit flags coordinating ISRs and the main loop
- Min/max + EMA filtering

## Build & flash

```bash
cd examples/avr/sensor-dashboard
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
