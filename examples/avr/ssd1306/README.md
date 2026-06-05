# ssd1306

Bring up a 128x64 **SSD1306** OLED over I2C.

## What it does

Initializes an SSD1306 display via the stdlib driver, clears it, and lights a
single pixel at each corner (0,0) and (127,63). UART reports boot and init.

## Hardware

- Arduino Uno @ 16 MHz
- SSD1306 OLED: **SDA → PC4 (A4)**, **SCL → PC5 (A5)**, I2C address **0x3C**
- Serial terminal at **9600 baud**

## Expected output

```
OLED
OK
```

## Key concepts

- `pymcu.drivers.ssd1306.SSD1306` — `init()`, `clear()`, `pixel(x, y, on)`

## Build & flash

```bash
cd examples/avr/ssd1306
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
