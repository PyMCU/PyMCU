# Device Drivers

PyMCU ships a growing collection of device drivers in `pymcu.drivers`. All drivers are
target-independent where possible, with architecture-specific pulse-timing delegated to
architecture sub-modules.

| Driver | Module | Description |
|---|---|---|
| DHT11 | `pymcu.drivers.dht11` | Temperature + humidity (1-Wire protocol) |
| DS18B20 | `pymcu.drivers.ds18b20` | Precision temperature (1-Wire, 12-bit) |
| BMP280 | `pymcu.drivers.bmp280` | Barometric pressure + temperature (I2C) |
| HD44780 LCD | `pymcu.drivers.lcd` | Character LCD, 4-bit parallel — class `LCD` |
| SSD1306 | `pymcu.drivers.ssd1306` | 128×64 OLED (I2C) |
| MAX7219 | `pymcu.drivers.max7219` | 8×8 LED matrix (SPI) |
| WS2812 | `pymcu.drivers.neopixel` | NeoPixel addressable RGB LEDs |

An LM35 needs no driver — it is a plain analog sensor, read directly with
{doc}`AnalogPin <../adc>`. (The `pymcu-micropython` compat package does ship an `lm35`
module for MicroPython-style code.)

```{toctree}
:maxdepth: 1

dht11
ds18b20
```
