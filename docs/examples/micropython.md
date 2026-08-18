# MicroPython Compatibility Examples

These examples use the `pymcu-micropython` compatibility layer.
The same source files run **unmodified** on any real MicroPython board — the only
difference is that PyMCU compiles them to bare-metal AVR firmware instead of
interpreting them at runtime.

Install the compat layer with:

```bash
pip install pymcu-micropython
```

---

(mp-blink)=
## Blink

The classic blink in MicroPython style — identical to what you would run on a
Raspberry Pi Pico or ESP32.

```{literalinclude} ../../examples/avr/blink/src/main.py
:language: python
:caption: examples/avr/blink/src/main.py (PyMCU HAL version)
```

MicroPython-style version using `machine.Pin` and `utime`:

```python
from machine import Pin
from utime import sleep_ms

def main():
    led = Pin(13, Pin.OUT)   # D13 = PB5 = built-in LED on Arduino Uno
    while True:
        led.value(1)
        sleep_ms(500)
        led.value(0)
        sleep_ms(500)
```

**What changes at compile time:**
`Pin(13, Pin.OUT)` — the integer `13` is resolved to AVR port `PB5` via a compile-time
`match`/`case` inside the compat layer. No runtime lookup table is generated.

**Build:**

```bash
cd examples/avr/blink
pymcu build
pymcu flash --port /dev/cu.usbmodem*
```

---

(mp-uart-echo)=
## UART Echo

Echo every received byte back on UART0. The LED pulses on each byte received.

```python
from machine import Pin, UART
from pymcu.types import uint8

def main():
    led  = Pin(13, Pin.OUT)
    uart = UART(0, 9600)
    uart.println("READY")

    while True:
        b: uint8 = uart.read()
        led.on()
        uart.write(b)
        led.off()
```

**Wiring:** No external wiring — uses the built-in LED and the UART0 pins (D0/D1).

**Differences from MicroPython:**
- `uart.read()` is always blocking (no timeout, no buffer size argument). On AVR there
  is no heap to allocate a receive buffer.
- `uart.println()` is a PyMCU extension; use `uart.write(b"\r\n")` for portability.

---

(mp-adc-read)=
## ADC Read

Read a potentiometer (or any 0–5 V analog source) on pin A0 every 200 ms.

```python
from machine import UART, ADC, Pin
from utime import sleep_ms
from pymcu.types import uint16, uint8

def main():
    uart = UART(0, 9600)
    adc  = ADC(Pin(14))            # A0 = Pin(14) = PC0; ADC(0) is the same converter

    uart.println("ADC ready")

    while True:
        val: uint16 = adc.read()       # raw 10-bit value, 0–1023
        scaled: uint8 = val >> 2       # 0–255 for single-byte output
        uart.println(f"adc={val} byte=0x{scaled:02X}")
        sleep_ms(200)
```

**Wiring:**

```
Arduino Uno     Potentiometer
-----------     -------------
A0 (PC0)   ←→  wiper (center tap)
5V         ←→  one end
GND        ←→  other end
```

**Note:** the converter is selected at compile time. `ADC(Pin(14))` is the MicroPython pin
form and `ADC(0)` the ESP-style channel form — channels 0–5 are A0–A5 on the Uno, and both
spellings produce identical firmware. A channel above 5 is a compile error naming the range.

---

(mp-dht-sensor)=
## DHT11 Sensor

Read temperature and humidity from a DHT11 sensor. The local `dht.py` driver
uses the same 1-Wire protocol as MicroPython's built-in `dht` module.

```python
from machine import Pin
from utime import sleep_ms
from dht import DHT11

led    = Pin(13, Pin.OUT)
sensor = DHT11(Pin(2, Pin.IN))

print("DHT11 ready")

while True:
    sensor.measure()

    if sensor.failed:
        print("read error")
        led.low()
    else:
        print("H: ", sensor.humidity(), "  T: ", sensor.temperature(), sep="")
        led.high()
        sleep_ms(100)
        led.low()

    sleep_ms(2000)
```

**Wiring:**

```
Arduino Uno    DHT11
-----------    -----
D2 (PD2)  ←→  DATA  (4.7 kΩ pull-up to 5V recommended)
5V        ←→  VCC
GND       ←→  GND
```

**Portability:** This code runs unmodified on any MicroPython board. On PyMCU the
driver is compiled to a ~30-instruction timing loop with no heap allocation.

---

(mp-ticks-ms)=
## Elapsed time with `ticks_ms()`

Measure elapsed time using `ticks_ms()` and `ticks_diff()`. The build driver
automatically initialises the Timer0 millisecond counter when `ticks_ms()` is
detected — no manual setup required.

```python
from machine import Pin, UART
from utime import ticks_ms, ticks_diff
from pymcu.types import uint32

def main():
    uart = UART(0, 9600)
    led  = Pin(13, Pin.OUT)

    uart.println("TICKS DEMO")

    while True:
        t0: uint32 = ticks_ms()
        led.high()
        # Simulate some work
        busy: uint32 = 0
        while busy < 10000:
            busy = busy + 1
        led.low()
        elapsed: uint32 = ticks_diff(ticks_ms(), t0)
        uart.println(f"ms={elapsed}")
```

:::{note}
`millis_init()` is auto-injected by `pymcu build` when `ticks_ms()` usage is
detected — identical to how UART is pre-initialised for `print()`. Timer0 is
used as the free-running counter. Do not use Timer0 for PWM or CTC in the
same project when `ticks_ms()` is active.
:::

---

(mp-signal-led)=
## Signal (active-low LED)

`machine.Signal` wraps a `Pin` and adds logical `on()`/`off()` semantics with
optional polarity inversion.

```python
from machine import Pin, Signal
from utime import sleep_ms

pin = Pin(13, Pin.OUT)
led = Signal(pin)          # invert=False: on() = HIGH (active-high)

while True:
    led.on()
    sleep_ms(500)
    led.off()
    sleep_ms(500)
```

**Active-low variant** (e.g. LED connected between D13 and VCC):

```python
led = Signal(pin, invert=True)   # on() drives LOW, off() drives HIGH
```

---

(mp-pwm-fade)=
## PWM Fade

Hardware PWM on D6 (OC0A / Timer0) fades a LED in while a soft-PWM LED on D13
fades out, so the contrast in smoothness is visible.

```python
from machine import Pin, PWM
from pymcu.types import uint8
from utime import sleep_us

hw     = PWM("PD6")
hw.init()

sw_pin = Pin(13, Pin.OUT)

print("PWM+SOFTPWM ready")

hw_duty: uint8 = 0
sw_duty: uint8 = 100
hw_up:   uint8 = 1
sw_up:   uint8 = 0

while True:
    hw.duty(hw_duty)

    # Soft PWM: 100 steps × 200 µs = 20 ms period (50 Hz)
    count: uint8 = 0
    while count < 100:
        if count < sw_duty:
            sw_pin.high()
        else:
            sw_pin.low()
        sleep_us(200)
        count = count + 1
    sw_pin.low()

    if hw_up:
        if hw_duty >= 250:
            hw_up = 0
        else:
            hw_duty = hw_duty + 5
    else:
        if hw_duty <= 5:
            hw_up = 1
        else:
            hw_duty = hw_duty - 5

    if sw_up:
        if sw_duty >= 100:
            sw_up = 0
        else:
            sw_duty = sw_duty + 5
    else:
        if sw_duty == 0:
            sw_up = 1
        else:
            sw_duty = sw_duty - 5
```

**Wiring:**

```
D6  (PD6)  ←→  LED + 220 Ω to GND   (hardware PWM — smooth fade)
D13 (PB5)  ←→  built-in LED          (soft PWM — visible stepping)
```

**Hardware PWM pins on ATmega328P:** D3 (OC2B), D5 (OC0B), D6 (OC0A), D9 (OC1A),
D10 (OC1B), D11 (OC2A). Only these pins support `machine.PWM`.
