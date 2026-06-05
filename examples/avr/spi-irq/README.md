# spi-irq

Interrupt-driven hardware SPI in **peripheral** mode.

## What it does

Configures the hardware SPI as a peripheral (`SPI(SPI.PERIPHERAL)`) and registers
an ISR with `spi.irq(handler)` — no `@interrupt` decorator or manual
`SPCR`/`SREG` writes. Reading `SPDR` in the ISR both clears `SPIF` and captures
the byte from the controller; the main loop prints it as hex.

## Hardware

- Arduino Uno as SPI peripheral @ 16 MHz (connect to any SPI controller)
  - **MISO = PB4**, **MOSI = PB3**, **SCK = PB5**, **SS = PB2**
- Serial terminal at **9600 baud**

## Expected output

`SPII` banner, then two hex digits + newline per byte received.

## Key concepts

- Hardware SPI peripheral mode + STC interrupt
- Reading `SPDR` to clear the interrupt flag

## Build & flash

```bash
cd examples/avr/spi-irq
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
