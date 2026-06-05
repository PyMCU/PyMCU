# i2c-scanner

Scan the I2C bus and report every device that ACKs.

## What it does

Probes addresses `0x01`–`0x7F` with `i2c.ping(addr)` and prints `FOUND 0xNN` for
each device that responds, followed by a total count. A handy first step when
bringing up a new I2C peripheral.

## Hardware

- Arduino Uno @ 16 MHz
- **SDA → PC4 (A4)**, **SCL → PC5 (A5)**, each pulled up to VCC via 4.7 kΩ
- One or more I2C devices on the bus
- Serial terminal at **9600 baud**

## Common addresses

| Address | Device |
|---------|--------|
| 0x3C / 0x3D | SSD1306 OLED |
| 0x48–0x4F | PCF8591 / ADS1115 ADC |
| 0x68 / 0x69 | MPU-6050 / DS3231 |
| 0x76 / 0x77 | BMP280 / BME280 |

## Build & flash

```bash
cd examples/avr/i2c-scanner
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
