# PyMCU HAL Examples

These examples use the PyMCU HAL directly — `pymcu.hal.*` and `pymcu.time`.
This is the lowest-overhead path: no compatibility shim, pin names are AVR
port strings (`"PB5"`, `"PC0"`), and types must be declared explicitly.

---

(hal-blink)=
## Blink

The minimal firmware — one output pin, one loop.

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)

    while True:
        led.high()
        delay_ms(1000)
        led.low()
        delay_ms(1000)
```

`Pin("PB5", Pin.OUT)` is a **zero-cost abstraction**: the constructor compiles to two
register writes (`DDR`, `PORT`). No SRAM is allocated for the pin object.

**Variant using board constants and `toggle()`:**

```python
from pymcu.hal.gpio import Pin
from pymcu.boards.arduino_uno import LED_BUILTIN
from pymcu.time import delay_ms

def main():
    led = Pin(LED_BUILTIN, Pin.OUT)

    while True:
        led.toggle()
        delay_ms(500)
```

---

(hal-button-debounce)=
## Button Debounce

Counts button presses with software debounce. Demonstrates `uint16` arithmetic
and edge detection.

```python
from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def main():
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    led  = Pin("PB5", Pin.OUT)
    uart = UART(9600)

    count: uint16 = 0
    prev:  uint8  = 1    # 1 = released

    while True:
        cur: uint8 = btn.value()

        if cur == 0 and prev == 1:    # falling edge = press
            count += 1
            led.toggle()

            # Transmit count as big-endian uint16
            uart.write((count >> 8) & 0xFF)
            uart.write(count & 0xFF)

            if count == 1000:
                count = 0
                uart.write('R')

        prev = cur
        delay_ms(10)    # 10 ms debounce window
```

**Wiring:**

```
D2 (PD2)   ←→  button to GND   (internal pull-up active)
D13 (PB5)  ←→  built-in LED
```

---

(hal-eeprom)=
## EEPROM Read / Write

Write a 4-byte pattern to EEPROM and read it back.

```python
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.eeprom import EEPROM

def main():
    uart = UART(9600)
    ee   = EEPROM()

    uart.println("EEPROM TEST")

    ee.write(0, 0xA1)
    ee.write(1, 0xB2)
    ee.write(2, 0xC3)
    ee.write(3, 0xD4)

    a: uint8 = ee.read(0)
    b: uint8 = ee.read(1)
    c: uint8 = ee.read(2)
    d: uint8 = ee.read(3)

    if a == 0xA1 and b == 0xB2 and c == 0xC3 and d == 0xD4:
        uart.println("EEPROM OK")
    else:
        uart.println("EEPROM FAIL")

    while True:
        pass
```

**ATmega328P EEPROM:** 1 KB at addresses 0x000–0x3FF. Write endurance is ~100,000
cycles. `EEPROM.write()` polls `EECR[EEPE]` and waits for the previous write to
complete before issuing the next one.

---

(hal-watchdog)=
## Watchdog Timer

Enable the watchdog, feed it 10 times, then disable it.

```python
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.watchdog import Watchdog

def main():
    uart = UART(9600)
    wdt  = Watchdog(timeout_ms=500)

    uart.println("WDT INIT")
    wdt.enable()

    i: uint8 = 0
    while i < 10:
        wdt.feed()
        uart.println("FEED")
        i += 1

    wdt.disable()
    uart.println("DONE")

    while True:
        pass
```

If you remove the `wdt.feed()` call, the MCU will reset after 500 ms and you will
see `"WDT INIT"` repeated in the serial monitor.

---

(hal-i2c-scanner)=
## I2C Bus Scanner

Probe all 7-bit I2C addresses (0x01–0x7F) and report found devices over UART.

```python
from pymcu.types import uint8
from pymcu.hal.i2c import I2C
from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    i2c  = I2C()

    uart.println("I2C SCANNER")

    found: uint8 = 0
    addr:  uint8 = 1

    while addr < 128:
        if i2c.ping(addr):
            uart.write_str("FOUND 0x")
            hi: uint8 = (addr >> 4) & 0x0F
            uart.write(hi + 48 if hi < 10 else hi + 55)
            lo: uint8 = addr & 0x0F
            uart.write(lo + 48 if lo < 10 else lo + 55)
            uart.write('\n')
            found += 1
        addr += 1

    uart.write_str("Done. Found: ")
    uart.write(found + 48)
    uart.write('\n')

    while True:
        pass
```

**Wiring:** SDA → A4 (PC4), SCL → A5 (PC5). Add 4.7 kΩ pull-up resistors to VCC
on both lines.

**Common addresses:**

| Address | Device |
|---------|--------|
| 0x3C / 0x3D | SSD1306 OLED |
| 0x48–0x4F   | PCF8591 / ADS1115 ADC |
| 0x68 / 0x69 | MPU-6050 / DS3231 RTC |
| 0x76 / 0x77 | BMP280 / BME280 |

---

(hal-ssd1306)=
## SSD1306 OLED Display

Initialise a 128×64 OLED over I2C, clear the display, and draw two pixels in
opposite corners.

```python
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.i2c import I2C
from pymcu.drivers.ssd1306 import SSD1306

def main():
    uart = UART(9600)
    i2c  = I2C()
    oled = SSD1306(i2c, 0x3C)

    uart.println("OLED")

    oled.init()
    uart.println("OK")

    oled.clear()
    oled.pixel(0, 0, 1)      # top-left corner
    oled.pixel(127, 63, 1)   # bottom-right corner

    while True:
        pass
```

**Wiring:**

```
SDA  ←→  A4 (PC4)   — 4.7 kΩ pull-up to 3.3V
SCL  ←→  A5 (PC5)   — 4.7 kΩ pull-up to 3.3V
VCC  ←→  3.3V        (most SSD1306 modules are 3.3V)
GND  ←→  GND
```

The `SSD1306` driver sends the 128-byte initialisation sequence over I2C at startup
and maintains a 128×64 pixel framebuffer in SRAM (1 KB). Call `oled.show()` to
flush the buffer after drawing.

---

(hal-neopixel)=
## NeoPixel (WS2812B)

Cycle a single WS2812B LED through Red → Green → Blue using the NeoPixel driver.

```python
from pymcu.types import uint8
from pymcu.hal.irq import disable_interrupts, enable_interrupts
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.drivers.neopixel import NeoPixel

def main():
    uart  = UART(9600)
    strip = NeoPixel("PD6", 1)    # data pin, pixel count

    uart.println("NEO")

    phase: uint8 = 0

    while True:
        disable_interrupts()   # WS2812B timing is sensitive to ISR jitter

        if phase == 0:
            strip.set_pixel(255, 0, 0)    # Red
        elif phase == 1:
            strip.set_pixel(0, 255, 0)    # Green
        elif phase == 2:
            strip.set_pixel(0, 0, 255)    # Blue

        strip.show()
        enable_interrupts()

        phase += 1
        if phase >= 3:
            phase = 0

        delay_ms(500)
```

**Wiring:**

```
D6 (PD6)  ←→  DIN  of WS2812B (through 300–500 Ω series resistor)
5V        ←→  VCC
GND       ←→  GND
```

**Why disable interrupts?** The WS2812B protocol requires bit timings accurate to
±150 ns at 800 kHz. Any ISR that fires mid-transmission will corrupt the pixel
data. Interrupts are re-enabled immediately after `strip.show()` returns.
