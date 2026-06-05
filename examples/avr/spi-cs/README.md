# spi-cs

Hardware SPI with a custom chip-select pin.

## What it does

Configures hardware SPI with a user-chosen CS pin (`SPI(cs="PB0")`). The CS pin
idles high and is asserted low during a transfer; the `with spi:` block
auto-asserts/deasserts it. Sends one byte (`0xA5`) and reports it over UART. The
CS pin is zero-cost — the compile-time string fold removes all overhead.

## Hardware

- Arduino Uno @ 16 MHz
- Custom CS on **PB0** (digital 8); **MOSI = PB3**, **SCK = PB5**
- Serial terminal at **9600 baud**

## Expected output

```
SCS
D:A5
OK
```

## Build & flash

```bash
cd examples/avr/spi-cs
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
