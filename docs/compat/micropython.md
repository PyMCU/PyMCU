# MicroPython Compatibility Layer

The `pymcu-micropython` package provides `machine`, `utime`, and `micropython` module names so
most MicroPython firmware targeting Arduino Uno / ATmega328P compiles with minimal edits.

**Key difference:** PyMCU compiles your code to native machine code. There is no MicroPython
interpreter on the device. The `machine` module is a compile-time shim over the PyMCU HAL —
not the MicroPython runtime.

---

## Setup

```bash
pip install pymcu-micropython
```

```toml
# pyproject.toml
[project]
dependencies = ["pymcu", "pymcu-micropython"]

[tool.pymcu]
stdlib = ["micropython"]
board  = "arduino_uno"
```

```python
# src/main.py
from machine import Pin
from utime import sleep_ms

def main():
    led = Pin(13, Pin.OUT)    # Arduino D13 = PB5
    while True:
        led.value(1)
        sleep_ms(500)
        led.value(0)
        sleep_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex
```

---

## Supported modules

| Module | Classes / Functions | Status |
|---|---|---|
| `machine` | `Pin`, `UART`, `ADC`, `PWM`, `SPI`, `I2C` | Complete |
| `utime` | `sleep_ms()`, `sleep_us()` | Complete |
| `micropython` | `const()`, `native` (stub), `viper` (stub) | Complete |
| `machine.Timer` | `Timer(id, period, callback)` | Planned |
| `machine.RTC` | | Not planned |

---

## Module reference

### `machine.Pin`

```python
from machine import Pin

# Integer Arduino pin number (0–13)
led = Pin(13, Pin.OUT)
btn = Pin(2, Pin.IN, Pin.PULL_UP)

# String port name also accepted
led2 = Pin("PB5", Pin.OUT)

# Methods
led.high()
led.low()
led.toggle()
v = led.value()       # read (returns uint8)
led.value(1)          # write
```

| Constant | Value |
|---|---|
| `Pin.IN` / `Pin.OUT` | 0 / 1 |
| `Pin.PULL_UP` / `Pin.PULL_DOWN` | 1 / 2 |
| `Pin.IRQ_FALLING` / `Pin.IRQ_RISING` | 1 / 2 |

### `machine.UART`

```python
from machine import UART
from pymcu.types import uint8

uart = UART(0, 9600)        # id=0 → USART0
uart.write(65)              # send byte
b: uint8 = uart.read()     # blocking receive
uart.write_str("hello\n")
```

### `machine.ADC`

```python
from machine import ADC, Pin
from pymcu.types import uint16

adc = ADC(Pin("A0"))
val: uint16 = adc.read_u16()   # 0–65535 (10-bit × 64)
raw: uint16 = adc.read()       # 0–1023 raw
```

### `machine.PWM`

```python
from machine import PWM, Pin
from pymcu.types import uint16

pwm = PWM(Pin("PD6"), freq=1000, duty_u16=32768)
pwm.duty_u16(49152)    # 75%
pwm.duty(200)          # 8-bit direct
pwm.deinit()
```

### `machine.SPI`

```python
from machine import SPI
from pymcu.types import uint8

spi = SPI()
spi.write(0xAA)
b: uint8 = spi.read()
```

### `machine.I2C`

```python
from machine import I2C
from pymcu.types import uint8

i2c = I2C(freq=100000)
count: uint8 = i2c.scan()           # number of responding devices
i2c.writeto(0x68, 0x3B)
val: uint8 = i2c.readfrom(0x68)
```

### `utime`

```python
from utime import sleep_ms, sleep_us

sleep_ms(500)
sleep_us(100)
```

### `micropython`

```python
import micropython

BAUD = micropython.const(9600)   # treated as compile-time integer literal

@micropython.native              # silently ignored — PyMCU already emits native code
def fast():
    pass
```

---

## Porting guide

### Top-level scripts work unchanged

```python
# MicroPython script — compiles unchanged in PyMCU
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)
while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

The compiler synthesizes a `main` entry point from top-level statements automatically.

### Add type annotations

```python
# MicroPython
count = 0

# PyMCU — annotation required
count: int = 0
```

### Replace `Pin.irq(handler=callback)` with `@interrupt`

```python
# MicroPython
btn.irq(trigger=Pin.IRQ_FALLING, handler=on_press)

# PyMCU
@interrupt(0x0002)    # INT0 vector (ATmega328P, D2)
def on_press():
    global count
    count += 1

def main():
    btn = Pin(2, Pin.IN, Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)    # configures EICRA/EIMSK hardware only
```

### Replace `machine.mem8`

```python
# MicroPython
machine.mem8[0x25] = 0xFF

# PyMCU
from pymcu.types import ptr, uint8
PORTB: ptr[uint8] = ptr(0x25)
PORTB.value = 0xFF
```

### Replace `bytearray` (dynamic)

```python
# MicroPython
buf = bytearray(8)

# PyMCU — fixed-size array
buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]
```

---

## Differences from real MicroPython

| Feature | MicroPython | PyMCU MicroPython layer |
|---|---|---|
| Execution model | Interpreter (bytecode) | **Compiler (native machine code)** |
| Runtime on MCU | ~256 KB interpreter in flash | **None** |
| `Pin.irq(handler=)` | Supported | Hardware-only; use `@interrupt` for the handler |
| `Timer(callback)` | Supported | Use `@interrupt(vector)` |
| `machine.mem8[addr]` | Supported | Use `ptr(addr).value` |
| `ticks_ms()` / `ticks_diff()` | Supported | Returns 0 (stub) |
| `float` | Full support | Soft-float, ~200–400 cycles/op |
| `f"..."` runtime | Supported | Compile-time constant only |
| `try / except` | Supported | Use return-code sentinels |
| `bytearray` dynamic | Supported | Use `uint8[N]` fixed-size array |
| Target hardware | STM32, RP2040, ESP32, … | ATmega328P only |
