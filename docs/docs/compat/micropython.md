# MicroPython Flavor

The `pymcu-micropython` package provides `machine`, `utime`, and `micropython` module names so
most MicroPython firmware targeting Arduino Uno / ATmega328P compiles with minimal edits.

## Quick Start

```bash
pip install pymcu-micropython
```

```toml
# pyproject.toml
[project]
dependencies = ["pymcu-compiler", "pymcu-micropython"]

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
pymcu build   # produces dist/firmware.hex
```

---

## Supported Modules

| Module | Classes / Functions | Status |
|--------|---------------------|--------|
| `machine` | `Pin`, `UART`, `ADC`, `PWM`, `SPI`, `I2C` | Complete |
| `utime` | `sleep_ms()`, `sleep_us()` | Complete |
| `micropython` | `const()`, `native` (stub), `viper` (stub) | Complete |
| `machine.Timer` | `Timer(id, period, callback)` | Planned (Beta) |
| `machine.RTC` | | Not planned |

---

## Module Reference

### `machine.Pin`

```python
from machine import Pin

# Integer Arduino pin number (0-13)
led = Pin(13, Pin.OUT)
btn = Pin(2, Pin.IN, Pin.PULL_UP)

# String port name also accepted
led2 = Pin("PB5", Pin.OUT)

# Methods
led.high()
led.low()
led.toggle()
v = led.value()       # read
led.value(1)          # write

# Set interrupt trigger (handler via @interrupt, see Porting Guide)
btn.irq(Pin.IRQ_FALLING)
```

**Pin constants:**

| Constant | Value |
|----------|-------|
| `Pin.IN` / `Pin.OUT` | 0 / 1 |
| `Pin.PULL_UP` / `Pin.PULL_DOWN` | 1 / 2 |
| `Pin.IRQ_FALLING` / `Pin.IRQ_RISING` | 1 / 2 |

**Arduino Uno pin mapping:**

| Integer | Arduino silk | Port |
|---------|-------------|------|
| 0 | D0 / RX | PD0 |
| 1 | D1 / TX | PD1 |
| 2 | D2 | PD2 |
| 3 | D3 | PD3 |
| ... | ... | ... |
| 13 | D13 / LED | PB5 |

---

### `machine.UART`

```python
from machine import UART
from pymcu.types import uint8

uart = UART(0, 9600)          # id=0 -> USART0
uart.write(65)                # send byte
b: uint8 = uart.read()        # receive byte (blocking)
uart.write_str("hello\n")
```

---

### `machine.ADC`

```python
from machine import ADC, Pin
from pymcu.types import uint16

adc = ADC(Pin("A0"))          # or ADC(Pin("PC0"))
val: uint16 = adc.read_u16()  # 0-65535 (10-bit scaled x64)
raw: uint16 = adc.read()      # 0-1023 (10-bit raw)
```

---

### `machine.PWM`

```python
from machine import PWM, Pin
from pymcu.types import uint16

pwm = PWM(Pin("PD6"), freq=1000, duty_u16=32768)
pwm.duty_u16(49152)   # 75%
pwm.duty(200)         # 8-bit direct
pwm.init()
pwm.deinit()
```

---

### `machine.SPI`

```python
from machine import SPI
from pymcu.types import uint8

spi = SPI()
spi.write(0xAA)
b: uint8 = spi.read()
```

---

### `machine.I2C`

```python
from machine import I2C
from pymcu.types import uint8

i2c = I2C(freq=100000)
count: uint8 = i2c.scan()         # returns number of responding devices
i2c.writeto(0x68, 0x3B)
val: uint8 = i2c.readfrom(0x68)
```

---

### `utime`

```python
from utime import sleep_ms, sleep_us

sleep_ms(500)
sleep_us(100)
```

---

### `micropython`

```python
import micropython

BAUD = micropython.const(9600)   # treated as integer literal 9600

@micropython.native              # silently ignored -- PyMCU already emits native code
def fast():
    pass
```

---

## Porting Guide

### Wrap top-level code in `def main():`

```python
# MicroPython (top-level execution)
led = Pin(13, Pin.OUT)

# PyMCU
def main():
    led = Pin(13, Pin.OUT)
```

### Add type annotations

```python
# MicroPython
count = 0

# PyMCU
count: uint16 = 0
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

### Replace `Timer(callback)` with `@interrupt`

```python
# MicroPython
tim = Timer(0, freq=1, callback=on_tick)

# PyMCU
from pymcu.hal.timer import Timer
from pymcu.types import ptr, uint8

TIMSK0: ptr[uint8] = ptr(0x6E)

@interrupt(0x0020)    # Timer0 OVF
def on_tick():
    global tick
    tick += 1

def main():
    TIMSK0[1] = 1     # enable TOIE0
    t = Timer(0, 64)
    t.start()
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

### Replace `bytearray` with fixed-size array

```python
# MicroPython
buf = bytearray(8)

# PyMCU
buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]
```

### Replace float arithmetic

```python
# MicroPython
temp = adc.read_u16() * 3.3 / 65536 * 100

# PyMCU
temp: uint16 = adc.read_u16() * 330 // 65536
```

---

## Differences from Real MicroPython

| Feature | MicroPython | PyMCU flavor |
|---------|-------------|--------------|
| `Pin.irq(handler=)` | Supported | Hardware-only; use `@interrupt` for the handler |
| `Timer(callback)` | Supported | Use `@interrupt(vector)` |
| `machine.mem8[addr]` | Supported | Use `ptr(addr).value` |
| `ticks_ms()` / `ticks_diff()` | Supported | Stub -- returns 0 |
| `float` | Supported | Not yet (Beta: `fixed16`) |
| `f"..."` | Supported | Compile-time constant only (Beta) |
| `try / except` | Supported | Use return-code sentinels |
| `bytearray` | Supported | Use `uint8[N]` fixed-size array |
| Top-level execution | Supported | Wrap in `def main():` |
| Targets | STM32, RP2040, ESP32, ... | ATmega328P only |
