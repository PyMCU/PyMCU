# CircuitPython Flavor

The `whisnake-circuitpython` package lets you write CircuitPython-style firmware that compiles
directly to bare-metal AVR machine code -- no interpreter, no heap, no runtime overhead.

## Quick Start

```bash
pip install whisnake-circuitpython
```

```toml
# pyproject.toml
[project]
dependencies = ["whipsnake", "whisnake-circuitpython"]

[tool.whip]
stdlib  = ["circuitpython"]
board   = "arduino_uno"
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
whip build   # produces dist/firmware.hex
```

---

## Supported Modules

| Module | Classes / Functions | Status |
|--------|---------------------|--------|
| `board` | `D0`-`D13`, `A0`-`A5`, `LED`, `TX`, `RX`, `SDA`, `SCL` | Complete |
| `digitalio` | `DigitalInOut`, `Direction`, `Pull`, `DriveMode` | Complete |
| `analogio` | `AnalogIn` | Complete |
| `busio` | `UART` | Complete |
| `pwmio` | `PWMOut` | Complete |
| `time` | `sleep_ms()`, `sleep_us()`, `sleep()` | Complete |
| `microcontroller` | `cpu.frequency` | Partial -- compile-time constant only |
| `busio.SPI`, `busio.I2C` | | Planned (Beta) |

---

## Module Reference

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
|----------|-------|
| `Direction.INPUT` / `Direction.OUTPUT` | 0 / 1 |
| `Pull.UP` / `Pull.DOWN` | 1 / 2 |
| `DriveMode.PUSH_PULL` / `DriveMode.OPEN_DRAIN` | 0 / 1 |

### `analogio`

```python
import analogio, board
from whisnake.types import uint16

adc = analogio.AnalogIn(board.A0)
val: uint16 = adc.value   # 0-65535 (10-bit ADC scaled x64)
```

### `busio.UART`

```python
import busio, board
from whisnake.types import uint8

uart = busio.UART(board.TX, board.RX, baudrate=9600)
uart.write(b"hello\n")
b: uint8 = uart.read(1)
```

### `pwmio.PWMOut`

```python
import pwmio, board

pwm = pwmio.PWMOut(board.D6, duty_cycle=32768)  # 50%
pwm.duty_cycle = 49152                           # 75%
pwm.deinit()
```

Duty cycle is 16-bit (0-65535), mapped to 8-bit OCR internally.

### `time`

```python
import time

time.sleep_ms(500)   # 500 ms
time.sleep_us(100)   # 100 us
time.sleep(1)        # 1 second (integer only)
```

### `board` pin constants (Arduino Uno)

| Constant | Port | Notes |
|----------|------|-------|
| `D0` / `RX` | PD0 | USART0 RX |
| `D1` / `TX` | PD1 | USART0 TX |
| `D2` | PD2 | INT0 |
| `D3` | PD3 | INT1 / OC2B (Timer2) |
| `D5` / `D6` | PD5 / PD6 | OC0B / OC0A (Timer0 PWM) |
| `D9` / `D10` | PB1 / PB2 | OC1A / OC1B (Timer1 PWM) |
| `D11` | PB3 | OC2A / MOSI |
| `D13` / `LED` / `SCK` | PB5 | Built-in LED |
| `A0`-`A3` | PC0-PC3 | ADC0-ADC3 |
| `A4` / `SDA` | PC4 | I2C SDA |
| `A5` / `SCL` | PC5 | I2C SCL |

---

## Porting Guide

### Add type annotations

```python
# CircuitPython
count = 0

# Whisnake
count: uint16 = 0
```

### Replace float sleep with milliseconds

```python
time.sleep(0.5)      # CircuitPython
time.sleep_ms(500)   # Whisnake
```

### Replace float arithmetic with integer scaling

```python
# CircuitPython
temp_c = raw * 3.3 / 1024 * 100

# Whisnake -- multiply first, divide last
temp_c: uint16 = raw * 330 // 1024
```

### Replace `try / except` with error sentinels

```python
# CircuitPython
try:
    val = sensor.read()
except RuntimeError:
    val = 0

# Whisnake
val: uint16 = sensor.read()   # returns 0xFFFF on error
if val == 0xFFFF:
    val = 0
```

### Wrap top-level code in `def main():`

```python
# CircuitPython (top-level)
led = digitalio.DigitalInOut(board.LED)

# Whisnake
def main():
    led = digitalio.DigitalInOut(board.LED)
```

---

## Differences from Real CircuitPython

| Feature | CircuitPython | Whisnake flavor |
|---------|---------------|--------------|
| `time.sleep(s)` | Float seconds | Use `sleep_ms()` |
| `float` | Supported | Not yet (Beta: `fixed16`) |
| `f"..."` | Supported | Compile-time constant only (Beta) |
| `try / except` | Supported | Use return-code sentinels |
| `Pin.irq(handler=)` | Supported | Use `@interrupt(vector)` |
| `supervisor`, `storage` | Supported | Not available |
| Dynamic pin assignment | Supported | Compile-time constant required |
| Targets | SAMD21, RP2040, ESP32, ... | ATmega328P only |
