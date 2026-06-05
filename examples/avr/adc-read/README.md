# adc-read

Single-channel, polled ADC read on the ATmega328P.

## What it does

`adc.read()` triggers a conversion, polls the `ADSC` bit until the conversion
finishes, and returns the raw 10-bit result (0–1023). The value is right-shifted
by 2 to scale it to 8 bits (0–255) so it fits in a single UART byte, sent every
100 ms.

## Hardware

- Arduino Uno / ATmega328P @ 16 MHz
- Potentiometer (or any analog source) on **PC0 (A0)**
- Serial terminal at **9600 baud**

## Expected output

`ADC` banner on boot, then a stream of raw 8-bit samples.

## Key concepts

- `AnalogPin.read()` blocking conversion
- 10-bit → 8-bit scaling with `>> 2`

## Build & flash

```bash
cd examples/avr/adc-read
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
