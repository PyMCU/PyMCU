# adc-interrupt

Interrupt-driven ADC sampling on the ATmega328P.

## What it does

Reads analog channel ADC0 (PC0) using the **ADC Conversion Complete interrupt**
instead of polling. `adc.irq(adc_isr)` registers the handler at the ADC vector,
enables `ADIE`, and sets the global interrupt flag (`SEI`) for you — no
`@interrupt` decorator or `asm("SEI")` needed.

The ISR reads `ADCL` first (which latches `ADCH`), stashes the low byte in
`GPIOR1`, and signals the main loop through bit `GPIOR0[1]`. The main loop prints
the result over UART and kicks off the next conversion.

## Hardware

- Arduino Uno / ATmega328P @ 16 MHz
- Analog source on **PC0 (A0)** — e.g. a potentiometer
- Serial terminal on TX at **9600 baud**

## Expected output

`ADC IRQ` banner on boot, then one raw 8-bit sample byte followed by `\n` per
conversion.

## Key concepts

- `AnalogPin.irq()` — zero-boilerplate ISR registration
- `GPIOR0`/`GPIOR1` general-purpose I/O registers used as atomic flags/storage
- Reading `ADCL` before `ADCH` to latch a coherent result

## Build & flash

```bash
cd examples/avr/adc-interrupt
pymcu build                                  # -> dist/firmware.hex
pymcu flash --port /dev/cu.usbmodemXXXX      # upload to the board
```
