# softspi

Bit-banged SPI in **controller** mode via the SoftSPI HAL.

## What it does

Configures SoftSPI on four arbitrary GPIO pins and sends one byte (`0xA5`). The
`with spi:` context manager auto-asserts/deasserts the active-low CS pin (idle
high). The byte sent is echoed over UART.

## Hardware

- Arduino Uno @ 16 MHz
- **SCK = PC0 (A0)**, **MOSI = PC1 (A1)**, **MISO = PC2 (A2)**, **CS = PC3 (A3)**
- Serial terminal at **9600 baud**

## Expected output

```
SSPI
D:A5
OK
```

## Key concepts

- `pymcu.hal.softspi.SoftSPI` on any GPIO pins (architecture-independent)
- Context-managed chip select

## Build & flash

```bash
cd examples/avr/softspi
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
