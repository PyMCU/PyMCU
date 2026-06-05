# bmp280

Read temperature and pressure from a Bosch **BMP280** sensor over I2C.

## What it does

Brings up the I2C bus, configures the BMP280 in normal mode via the stdlib
driver, then reads the raw temperature and pressure ADC values and prints both
as hex bytes (high byte then low byte) over UART.

## Hardware

- Arduino Uno / ATmega328P @ 16 MHz
- BMP280 breakout:
  - **SDA → PC4 (A4)**, **SCL → PC5 (A5)**
  - I2C address **0x76** (SDO tied to GND)
- Serial terminal at **9600 baud**

## Expected output

```
BMP280
OK
T:XXXX     <- raw temperature (hex hi+lo)
P:XXXX     <- raw pressure (hex hi+lo)
```

## Key concepts

- `pymcu.hal.i2c.I2C` master mode
- `pymcu.drivers.bmp280.BMP280` stdlib driver
- `uart.write_hex()` for byte-as-hex output

## Build & flash

```bash
cd examples/avr/bmp280
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
