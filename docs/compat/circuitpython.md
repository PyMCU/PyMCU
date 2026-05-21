# CircuitPython Compatibility Layer

The `pymcu-circuitpython` package provides `board`, `digitalio`, `analogio`, `busio`,
`pwmio`, and `time` modules so most CircuitPython firmware targeting Arduino Uno / ATmega328P
compiles with minimal edits.

**Key difference:** PyMCU compiles your code to native machine code. There is no CircuitPython
interpreter on the device. The compatibility modules are compile-time shims over the PyMCU HAL.

---

## Setup

```bash
pip install pymcu-circuitpython
```

```toml
# pyproject.toml
[project]
dependencies = ["pymcu", "pymcu-circuitpython"]

[tool.pymcu]
stdlib = ["circuitpython"]
board  = "arduino_uno"
```

```python
# src/main.py
import board, digitalio, time

def main():
    led = digitalio.DigitalInOut(board.LED)
    led.direction = digitalio.Direction.OUTPUT
    while True:
        led.value = True
        time.sleep_ms(500)
        led.value = False
        time.sleep_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex
```

---

## Supported modules

| Module | Classes / Functions | Status |
|---|---|---|
| `board` | `D0`–`D13`, `A0`–`A5`, `LED`, `TX`, `RX`, `SDA`, `SCL` | Complete |
| `digitalio` | `DigitalInOut`, `Direction`, `Pull`, `DriveMode` | Complete |
| `analogio` | `AnalogIn` | Complete |
| `busio` | `UART` | Complete |
| `pwmio` | `PWMOut` | Complete |
| `time` | `sleep_ms()`, `sleep_us()`, `sleep()` | Complete |
| `microcontroller` | `cpu.frequency` | Partial — compile-time constant only |
| `busio.SPI`, `busio.I2C` | | Planned |

---

## Module reference

### `digitalio`

```python
import board, digitalio

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
led.value = True

btn = digitalio.DigitalInOut(board.D2)
btn.direction = digitalio.Direction.INPUT
btn.pull = digitalio.Pull.UP
if btn.value:
    pass
```

| Constant | Value |
|---|---|
| `Direction.INPUT` / `Direction.OUTPUT` | 0 / 1 |
| `Pull.UP` / `Pull.DOWN` | 1 / 2 |
| `DriveMode.PUSH_PULL` / `DriveMode.OPEN_DRAIN` | 0 / 1 |

### `analogio`

```python
import analogio, board
from pymcu.types import uint16

adc = analogio.AnalogIn(board.A0)
val: uint16 = adc.value    # 0–65535 (10-bit × 64)
```

### `busio.UART`

```python
import busio, board
from pymcu.types import uint8

uart = busio.UART(board.TX, board.RX, baudrate=9600)
uart.write(b"hello\n")
b: uint8 = uart.read(1)
```

### `pwmio.PWMOut`

```python
import pwmio, board

pwm = pwmio.PWMOut(board.D6, duty_cycle=32768)   # 50%
pwm.duty_cycle = 49152                            # 75%
pwm.deinit()
```

Duty cycle is 16-bit (0–65535), mapped to 8-bit OCR internally.

### `time`

```python
import time

time.sleep_ms(500)    # 500 ms
time.sleep_us(100)    # 100 µs
time.sleep(1)         # 1 second (integer only)
```

### `board` pin constants (Arduino Uno)

| Constant | Port | Notes |
|---|---|---|
| `D0` / `RX` | PD0 | USART0 RX |
| `D1` / `TX` | PD1 | USART0 TX |
| `D2` | PD2 | INT0 |
| `D3` | PD3 | INT1 / OC2B |
| `D5` | PD5 | OC0B (Timer0 PWM) |
| `D6` | PD6 | OC0A (Timer0 PWM) |
| `D9` | PB1 | OC1A (Timer1 PWM) |
| `D11` | PB3 | OC2A / MOSI |
| `D13` / `LED` | PB5 | Built-in LED / SCK |
| `A0`–`A3` | PC0–PC3 | ADC0–ADC3 |
| `A4` / `SDA` | PC4 | I2C SDA |
| `A5` / `SCL` | PC5 | I2C SCL |

---

## Porting guide

### Top-level scripts work unchanged

```python
# CircuitPython script — compiles unchanged in PyMCU
import board, digitalio, time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = True
    time.sleep_ms(500)
    led.value = False
    time.sleep_ms(500)
```

### Add type annotations

```python
# CircuitPython
count = 0

# PyMCU — annotation required
count: int = 0    # int maps to int16; no import needed
```

### Replace float sleep with milliseconds

```python
time.sleep(0.5)       # CircuitPython
time.sleep_ms(500)    # PyMCU
```

### Replace float arithmetic with integer scaling

```python
# CircuitPython
temp_c = raw * 3.3 / 1024 * 100

# PyMCU — multiply first, divide last
temp_c: int = raw * 330 // 1024
```

### Replace `try / except` with error sentinels

```python
# CircuitPython
try:
    val = sensor.read()
except RuntimeError:
    val = 0

# PyMCU
val: int = sensor.read()    # sensor returns 0xFFFF on error
if val == 0xFFFF:
    val = 0
```

---

## Differences from real CircuitPython

| Feature | CircuitPython | PyMCU CircuitPython layer |
|---|---|---|
| Execution model | Interpreter (bytecode) | **Compiler (native machine code)** |
| Runtime on MCU | ~256 KB interpreter in flash | **None** |
| `time.sleep(s)` | Float seconds | Use `sleep_ms()` — integer only |
| `float` | Full support | Soft-float, ~200–400 cycles/op |
| `f"..."` runtime | Supported | Compile-time constant only |
| `try / except` | Supported | Use return-code sentinels |
| `Pin.irq(handler=)` | Supported | Use `@interrupt(vector)` |
| `supervisor`, `storage` | Supported | Not available |
| Dynamic pin assignment | Supported | Compile-time constant required |
| Target hardware | SAMD21, RP2040, ESP32, … | ATmega328P only |
