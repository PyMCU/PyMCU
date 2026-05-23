# CircuitPython Compatibility Layer

The `pymcu-circuitpython` package provides `board`, `digitalio`, `analogio`, `busio`,
`pwmio`, and `time` modules so CircuitPython firmware targeting Arduino Uno / ATmega328P
compiles with minimal edits.

:::{important} Compiled, not interpreted
There is no CircuitPython interpreter on the device. Every `digitalio.*` or `busio.*` call
is a compile-time shim over the PyMCU HAL. The MCU runs only native machine code.
:::

---

## Quick start

```bash
pip install pymcu pymcu-circuitpython
```

```toml
# pyproject.toml
[tool.pymcu]
stdlib = ["circuitpython"]
board  = "arduino_uno"
chip   = "atmega328p"
```

```python
# src/main.py  — identical to a CircuitPython script
import board, digitalio, time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = True
    time.sleep_ms(500)
    led.value = False
    time.sleep_ms(500)
```

```bash
pymcu build
pymcu flash
```

---

## Supported modules

| Module | API surface | Status |
|---|---|---|
| `board` | `D0`–`D13`, `A0`–`A5`, `LED`, `TX`, `RX`, `SDA`, `SCL` | ✅ Complete |
| `digitalio.DigitalInOut` | `direction`, `value`, `pull`, `drive_mode` | ✅ Complete |
| `digitalio.Direction` | `INPUT`, `OUTPUT` | ✅ Complete |
| `digitalio.Pull` | `UP`, `DOWN` | ✅ Complete |
| `digitalio.DriveMode` | `PUSH_PULL`, `OPEN_DRAIN` | ✅ Complete |
| `analogio.AnalogIn` | `.value` (0–65535) | ✅ Complete |
| `busio.UART` | `write`, `read`, `baudrate` | ✅ Complete |
| `pwmio.PWMOut` | `duty_cycle`, `frequency`, `deinit` | ✅ Complete |
| `time` | `sleep_ms`, `sleep_us`, `sleep` (integer) | ✅ Complete |
| `busio.SPI` | Full SPI bus | 🔜 Planned |
| `busio.I2C` | Full I2C bus | 🔜 Planned |
| `neopixel.NeoPixel` | WS2812 driver | 🔜 Planned |

---

## Module reference

### `digitalio`

```python
import board, digitalio

led = digitalio.DigitalInOut(board.LED)    # PB5, Arduino D13
led.direction = digitalio.Direction.OUTPUT
led.value = True     # HIGH
led.value = False    # LOW

btn = digitalio.DigitalInOut(board.D2)
btn.direction = digitalio.Direction.INPUT
btn.pull = digitalio.Pull.UP
if btn.value:        # reads pin
    pass
```

| Constant | Meaning |
|---|---|
| `Direction.INPUT` / `Direction.OUTPUT` | 0 / 1 |
| `Pull.UP` / `Pull.DOWN` | Pull-up / pull-down resistor |
| `DriveMode.PUSH_PULL` | Normal output (default) |
| `DriveMode.OPEN_DRAIN` | Open-drain output |

---

### `analogio`

```python
import analogio, board
from pymcu.types import uint16

adc = analogio.AnalogIn(board.A0)
val: uint16 = adc.value    # 0–65535 (10-bit ADC scaled ×64)
```

---

### `busio.UART`

```python
import busio, board
from pymcu.types import uint8

uart = busio.UART(board.TX, board.RX, baudrate=9600)
uart.write(b"hello\n")     # bytes literal — stored in PROGMEM
b: uint8 = uart.read(1)    # blocking receive of 1 byte
```

---

### `pwmio.PWMOut`

```python
import pwmio, board

pwm = pwmio.PWMOut(board.D6, duty_cycle=32768, frequency=1000)
pwm.duty_cycle = 49152     # 75%
pwm.frequency  = 490       # change frequency
pwm.deinit()
```

Duty cycle is 16-bit (0–65535) mapped to an 8-bit OCR register internally.

---

### `time`

```python
import time

time.sleep_ms(500)    # 500 ms
time.sleep_us(100)    # 100 µs
time.sleep(1)         # integer seconds only (CircuitPython uses float — see porting guide)
```

---

### `board` pin constants — Arduino Uno

| Constant | Port | Arduino label | Notes |
|---|---|---|---|
| `D0` / `RX` | PD0 | D0 | USART0 RX |
| `D1` / `TX` | PD1 | D1 | USART0 TX |
| `D2` | PD2 | D2 | INT0 |
| `D3` | PD3 | D3 | INT1 / OC2B |
| `D4` | PD4 | D4 | |
| `D5` | PD5 | D5 | OC0B (Timer0 PWM) |
| `D6` | PD6 | D6 | OC0A (Timer0 PWM) |
| `D7` | PD7 | D7 | |
| `D8` | PB0 | D8 | |
| `D9` | PB1 | D9 | OC1A (Timer1 PWM) |
| `D10` | PB2 | D10 | SS |
| `D11` | PB3 | D11 | MOSI / OC2A |
| `D12` | PB4 | D12 | MISO |
| `D13` / `LED` | PB5 | D13 | Built-in LED / SCK |
| `A0`–`A3` | PC0–PC3 | A0–A3 | ADC0–ADC3 |
| `A4` / `SDA` | PC4 | A4 | I2C SDA |
| `A5` / `SCL` | PC5 | A5 | I2C SCL |

---

## Porting guide

### Top-level scripts work unchanged

```python
# CircuitPython script — compiles as-is
import board, digitalio, time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = not led.value
    time.sleep_ms(500)
```

### Add type annotations to variables

```python
count = 0          # CircuitPython — no annotation needed
count: int = 0     # PyMCU — required (int → int16 on AVR)
```

### Replace `time.sleep(float)` with `time.sleep_ms(int)`

```python
time.sleep(0.5)       # CircuitPython — float seconds
time.sleep_ms(500)    # PyMCU — integer milliseconds
```

### Replace float ADC conversion with integer scaling

```python
# CircuitPython
voltage = adc.value * 3.3 / 65535

# PyMCU — multiply first, divide last (integer)
voltage_mv: int = adc.value * 330 // 65535    # millivolts
```

### Replace dynamic buffers with fixed-size arrays

```python
buf = bytearray(8)                     # CircuitPython
buf: uint8[8] = [0,0,0,0,0,0,0,0]    # PyMCU
```

### Replace `try / except` with error sentinels

```python
# CircuitPython
try:
    val = sensor.read()
except RuntimeError:
    val = -1

# PyMCU
val: int = sensor.read()
if val == -32768:    # driver-specific error sentinel
    val = -1
```

---

## Differences from real CircuitPython

| Feature | CircuitPython | PyMCU compat layer |
|---|---|---|
| Execution model | Bytecode interpreter (~256 KB) | **Native compiler — zero runtime** |
| `time.sleep(s)` | Float seconds | Integer only — use `sleep_ms()` |
| `float` arithmetic | Full support | Soft-float (AVR, ~200–400 cycles/op) |
| `f"..."` runtime format | Supported | Compile-time constants only |
| `try / except` | Supported | Not available — use error-sentinel return values |
| `busio.SPI` / `busio.I2C` | Supported | 🔜 Planned |
| `neopixel` | Supported | 🔜 Planned |
| `supervisor`, `storage` | Supported | Not planned |
| Dynamic pin assignment | Supported | Compile-time constant required |
| Target hardware | SAMD21, RP2040, ESP32, … | ATmega328P (Arduino Uno) |

