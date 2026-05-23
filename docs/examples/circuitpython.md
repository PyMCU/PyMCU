# CircuitPython Compatibility Examples

These examples use the `pymcu-circuitpython` compatibility layer.
The same source files run **unmodified** on any real CircuitPython board — PyMCU
compiles them to bare-metal AVR firmware with zero runtime overhead.

Install the compat layer with:

```bash
pip install pymcu-circuitpython
```

---

(cp-blink)=
## Blink

The classic blink in Adafruit CircuitPython style. Uses `board.LED` — the
compiler resolves it to `PB5` (Arduino Uno pin 13) at compile time.

```python
import board
import digitalio
import time

def main():
    led = digitalio.DigitalInOut(board.LED)
    led.direction = digitalio.Direction.OUTPUT

    while True:
        led.value = True
        time.sleep(0.15)
        led.value = False
        time.sleep(0.75)
```

**Note:** `board.LED` is a compile-time constant. Assigning `led.direction` and
`led.value` is zero-cost — `DigitalInOut` is a zero-cost abstraction (ZCA).

---

(cp-uart-echo)=
## UART Echo

Echo every received byte over `busio.UART`. Adapted from the Adafruit CircuitPython
Essentials UART example.

```python
import board
import busio
from digitalio import DigitalInOut, Direction
from pymcu.types import uint8

def main():
    led = DigitalInOut(board.LED)
    led.set_direction(Direction.OUTPUT)

    uart = busio.UART(board.TX, board.RX, baudrate=9600)

    while True:
        byte: uint8 = uart.read()
        led.set_value(1)
        uart.write(byte)
        led.set_value(0)
```

**Differences from CircuitPython:**
- `uart.read()` takes no size argument — single-byte blocking receive on AVR.
- `led.direction = ...` property syntax also works; `set_direction()` is the
  PyMCU canonical form for the compat layer.

---

(cp-button-led)=
## Button-controlled LED

Hold a button to turn on the LED. Uses `digitalio.Pull.UP` for the internal
pull-up resistor.

```python
import board
import time
from digitalio import DigitalInOut, Direction, Pull
from pymcu.types import uint8

def main():
    led = DigitalInOut(board.LED)
    led.set_direction(Direction.OUTPUT)

    btn = DigitalInOut(board.D2)
    btn.set_direction(Direction.INPUT)
    btn.set_pull(Pull.UP)

    while True:
        state: uint8 = btn.get_value()
        # Button is active-low (pressed = 0)
        if state == 0:
            led.set_value(1)
        else:
            led.set_value(0)
        time.sleep_ms(10)
```

**Wiring:**

```
Arduino Uno     Button
-----------     ------
D2 (PD2)   ←→  one leg
GND        ←→  other leg   (internal pull-up active — no external resistor needed)
D13        ←→  built-in LED
```

---

(cp-dht-sensor)=
## DHT11 Sensor

Read temperature and humidity, output over `busio.UART`.

```python
import board
import time
import busio
from digitalio import DigitalInOut, Direction
from dht11 import DHT11

def main():
    uart   = busio.UART(board.TX, board.RX, baudrate=9600)
    led    = DigitalInOut(board.LED)
    sensor = DHT11(board.D2)

    led.set_direction(Direction.OUTPUT)

    uart.println("DHT11 ready")

    while True:
        sensor.measure()

        if sensor.failed:
            uart.println("read error")
            led.set_value(0)
        else:
            uart.write_str("H: ")
            uart.print_byte(sensor.humidity)
            uart.write_str("  T: ")
            uart.print_byte(sensor.temperature)
            led.set_value(1)
            time.sleep_ms(100)
            led.set_value(0)

        time.sleep_ms(2000)
```

**Wiring:**

```
Arduino Uno    DHT11
-----------    -----
D2  (PD2)  ←→ DATA  (4.7 kΩ pull-up to 5V recommended)
5V         ←→ VCC
GND        ←→ GND
```

---

(cp-morse-blinker)=
## Morse Blinker

Blinks SOS in Morse code. Demonstrates `@inline` for timing-critical helpers —
`DigitalInOut` is a zero-cost abstraction and cannot be passed to non-inlined
functions.

```python
import board
import time
from digitalio import DigitalInOut, Direction
from pymcu.types import inline

@inline
def dot(led):
    led.set_value(1)
    time.sleep_ms(200)
    led.set_value(0)
    time.sleep_ms(200)

@inline
def dash(led):
    led.set_value(1)
    time.sleep_ms(600)
    led.set_value(0)
    time.sleep_ms(200)

def main():
    led = DigitalInOut(board.LED)
    led.set_direction(Direction.OUTPUT)

    while True:
        # S: ...
        dot(led)
        dot(led)
        dot(led)
        time.sleep_ms(400)    # letter gap

        # O: ---
        dash(led)
        dash(led)
        dash(led)
        time.sleep_ms(400)    # letter gap

        # S: ...
        dot(led)
        dot(led)
        dot(led)
        time.sleep_ms(1200)   # word gap
```

**Why `@inline`?** `DigitalInOut` is a zero-cost abstraction — it holds no SRAM state.
Passing it to a regular function would require materializing it on the stack.
`@inline` expands the function body at every call site instead, preserving the
zero-cost property.

---

(cp-traffic-light)=
## Traffic Light

A three-LED traffic light state machine using only `digitalio` and `time`.
UK-style sequence: Red → Red+Yellow → Green → Yellow → repeat.

```python
import board
import time
from digitalio import DigitalInOut, Direction

def main():
    red    = DigitalInOut(board.D11)
    yellow = DigitalInOut(board.D12)
    green  = DigitalInOut(board.D13)

    red.set_direction(Direction.OUTPUT)
    yellow.set_direction(Direction.OUTPUT)
    green.set_direction(Direction.OUTPUT)

    while True:
        # Red — stop (3 s)
        red.set_value(1); yellow.set_value(0); green.set_value(0)
        time.sleep_ms(3000)

        # Red + Yellow — prepare to go (500 ms)
        red.set_value(1); yellow.set_value(1); green.set_value(0)
        time.sleep_ms(500)

        # Green — go (3 s)
        red.set_value(0); yellow.set_value(0); green.set_value(1)
        time.sleep_ms(3000)

        # Yellow — slow down (1 s)
        red.set_value(0); yellow.set_value(1); green.set_value(0)
        time.sleep_ms(1000)
```

**Wiring:**

```
D11 (PB3)  ←→  Red    LED + 220 Ω to GND
D12 (PB4)  ←→  Yellow LED + 220 Ω to GND
D13 (PB5)  ←→  Green  LED (built-in, or external)
```

---

(cp-adc-pwm)=
## ADC-controlled PWM Dimmer

Read a potentiometer on A0 and use the value to control LED brightness via PWM
on D6. The duty cycle tracks the ADC value in real time.

```python
import board
from analogio import AnalogIn
from pwmio import PWMOut
from time import sleep_ms

def main():
    pot = AnalogIn(board.A0)
    led = PWMOut(board.D6, duty_cycle=0)

    while True:
        adc_value = pot.value          # 0–65535 (16-bit scaled from 10-bit ADC)
        led.duty_cycle = adc_value     # directly drives OCR0A
        sleep_ms(10)
```

**Wiring:**

```
A0 (PC0)   ←→  potentiometer wiper
D6 (PD6)   ←→  LED + 220 Ω to GND   (OC0A — hardware PWM pin)
5V         ←→  potentiometer end
GND        ←→  potentiometer end
```

**Note:** `pot.value` returns a 16-bit scaled value. `PWMOut` maps this directly
to the 8-bit hardware compare register — no floating-point math is generated.
