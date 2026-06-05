# softspi-peripheral

Bit-banged SPI in **peripheral** mode via the SoftSPI HAL.

## What it does

Acts as an SPI peripheral: polls the CS pin with `cs_asserted()`, and once a
controller pulls CS low, `exchange(0xAB)` drives MISO with `0xAB` while reading
the controller's byte on MOSI. The received byte is reported over UART.

## Hardware

- Arduino Uno @ 16 MHz (driven by an external SPI controller)
- **SCK = PC0 (A0, in)**, **MOSI = PC1 (A1, in)**, **MISO = PC2 (A2, out)**,
  **CS = PC3 (A3, in)**
- Serial terminal at **9600 baud**

## Expected output

```
SSPIP
R:XX     <- byte received from controller
OK
```

## Key concepts

- SoftSPI `mode=SoftSPI.PERIPHERAL`
- `cs_asserted()` polling and `exchange()`

## Build & flash

```bash
cd examples/avr/softspi-peripheral
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
