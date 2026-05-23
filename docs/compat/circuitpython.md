# CircuitPython Compatibility Layer

The `pymcu-circuitpython` package lets you write CircuitPython code and compile it
to bare-metal AVR firmware. `board`, `digitalio`, `analogio`, `busio`, `pwmio`,
`neopixel`, `time`, `supervisor`, `alarm`, and `microcontroller` are all available.

:::{important} Compiled, not interpreted
There is no CircuitPython interpreter on the device. Every `digitalio.*`, `busio.*`,
or `neopixel.*` call is a compile-time shim over the PyMCU HAL. The MCU runs only
native machine code — zero interpreter overhead.
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
import board
from digitalio import DigitalInOut, Direction
from time import sleep_ms

def main():
    led = DigitalInOut(board.LED)
    led.direction = Direction.OUTPUT
    while True:
        led.value = True
        sleep_ms(500)
        led.value = False
        sleep_ms(500)
```

```bash
pymcu build
```

---

## Supported modules

| Module | API surface | Status |
|---|---|---|
| `board` | `D0`–`D13`, `A0`–`A5`, `LED`, `LED_BUILTIN`, `TX`, `RX`, `SDA`, `SCL`, `SCK`, `MOSI`, `MISO`, `SS` | ✅ Complete |
| `digitalio` | `DigitalInOut`, `Direction`, `Pull`, `DriveMode` | ✅ Complete |
| `analogio` | `AnalogIn`, `AnalogOut` | ✅ Complete |
| `busio` | `UART`, `I2C`, `SPI` | ✅ Complete |
| `pwmio` | `PWMOut` | ✅ Complete |
| `neopixel` | `NeoPixel` | ✅ Complete |
| `time` | `sleep`, `sleep_ms`, `sleep_us`, `monotonic`, `monotonic_ns` | ✅ Complete |
| `supervisor` | `ticks_ms`, `ticks_add`, `ticks_diff`, `reload` | ✅ Complete |
| `alarm` | `time.TimeAlarm`, `pin.PinAlarm`, `sleep_until_alarms` | ✅ Complete |
| `microcontroller` | `cpu.frequency`, `cpu.voltage`, `cpu.uid`, `reset`, `delay_us` | ✅ Partial |

---

## Module reference

### `digitalio`

`DigitalInOut` wraps the PyMCU HAL `Pin` as a zero-cost abstraction. Every property
assignment (`direction`, `value`, `pull`, `drive_mode`) is inlined at compile time.

```python
import board
from digitalio import DigitalInOut, Direction, Pull

led = DigitalInOut(board.LED)
led.direction = Direction.OUTPUT
led.value = True

btn = DigitalInOut(board.D2)
btn.direction = Direction.INPUT
btn.pull = Pull.UP
if btn.value:
    led.value = True
```

**`switch_to_output()` / `switch_to_input()`** — shorter CircuitPython idiom:

```python
led = DigitalInOut(board.LED)
led.switch_to_output(value=0)          # output, starts LOW
btn = DigitalInOut(board.D2)
btn.switch_to_input(pull=Pull.UP)      # input with pull-up
```

**Context manager** — `deinit()` is called automatically on exit:

```python
with DigitalInOut(board.LED) as led:
    led.switch_to_output()
    led.value = True
# pin is released here
```

| Constant | Value | Description |
|---|---|---|
| `Direction.INPUT` | 0 | Configure as input |
| `Direction.OUTPUT` | 1 | Configure as output |
| `Pull.NONE` | 0 | No pull resistor |
| `Pull.UP` | 1 | Internal pull-up |
| `Pull.DOWN` | 2 | No hardware pull-down on AVR — stub |
| `DriveMode.PUSH_PULL` | 0 | Normal push-pull (default) |
| `DriveMode.OPEN_DRAIN` | 1 | Open-drain output |

---

### `analogio`

```python
import board
from analogio import AnalogIn
from pymcu.types import uint16

adc = AnalogIn(board.A0)
val: uint16 = adc.value          # 0–65535 (10-bit ADC scaled ×64)
vref: uint8 = adc.reference_voltage   # always 5 on 5 V boards
```

`AnalogOut` is not available — the ATmega328P has no DAC. Instantiating it raises
`NotImplementedError` at build time.

---

### `busio.UART`

```python
import board
import busio
from pymcu.types import uint8

uart = busio.UART(board.TX, board.RX, baudrate=9600)
uart.write_str("READY\n")

b: uint8 = uart.read()          # blocking receive of 1 byte
uart.println("received")
```

The `tx`/`rx` pin arguments are accepted for API compatibility; hardware pins on
ATmega328P are fixed (PD1/PD0).

---

### `busio.I2C`

```python
import board, busio
from pymcu.types import uint8

i2c = busio.I2C(board.SCL, board.SDA)

# Context-manager style (CircuitPython idiomatic):
with i2c:
    i2c.write(0x68, 0x00)         # write one byte to device 0x68
    val: uint8 = i2c.read(0x68)   # read one byte from device 0x68

# Lock style:
if i2c.try_lock():
    addr: uint8 = i2c.scan()      # address of first responding device
    i2c.writeto(0x68, 0x6B)
    data: uint8 = i2c.readfrom_into(0x68)
    i2c.unlock()
```

| Method | Description |
|---|---|
| `write(addr, data)` | Write one byte; returns 1 on ACK |
| `read(addr)` | Read one byte |
| `writeto(addr, data)` | Alias for `write()` |
| `readfrom_into(addr)` | Read one byte (CircuitPython-style name) |
| `writeto_then_readfrom(addr, out)` | Repeated-start write + read |
| `scan()` | Returns first responding address (0 = none found) |
| `try_lock()` / `unlock()` | Bus locking (single-master on AVR; always succeeds) |

:::{note}
Hardware I2C on ATmega328P uses fixed pins: SCL = PC5 (A5), SDA = PC4 (A4).
The `scl`/`sda` arguments to `busio.I2C()` are accepted for API compatibility.
:::

---

### `busio.SPI`

```python
import board, busio
from pymcu.types import uint8

spi = busio.SPI(board.SCK, MOSI=board.MOSI, MISO=board.MISO)

# Context-manager style — asserts/deasserts CS automatically:
with spi:
    spi.write(0xAB)
    val: uint8 = spi.readinto()

# Manual CS control:
spi.select()
spi.write(0x01)
result: uint8 = spi.write_readinto(0xFF)
spi.deselect()
```

| Method | Description |
|---|---|
| `write(data)` | Transmit one byte |
| `readinto()` | Receive one byte (sends 0xFF) |
| `write_readinto(out)` | Full-duplex transfer — send and receive simultaneously |
| `select()` / `deselect()` | Assert / deassert chip-select |
| `configure(baudrate, polarity, phase, bits)` | Accepted for API compatibility |
| `try_lock()` / `unlock()` | Bus locking (always succeeds on bare metal) |

:::{note}
Hardware SPI on ATmega328P uses fixed pins: SCK = PB5, MOSI = PB3, MISO = PB4.
Pass a `cs` string to the constructor to control a specific CS pin.
:::

---

### `pwmio.PWMOut`

```python
import board
from pwmio import PWMOut

pwm = PWMOut(board.D6, duty_cycle=32768, frequency=1000)   # 50%, 1 kHz
pwm.duty_cycle = 49152      # 75%
pwm.frequency  = 490        # change frequency
pwm.deinit()                # stop PWM
```

Duty cycle is 16-bit (0–65535) mapped to an 8-bit OCR register internally.
Context manager is supported (`with PWMOut(...) as pwm:`).

---

### `neopixel`

```python
import board, neopixel

pixels = neopixel.NeoPixel(board.D6, 8)   # data pin, pixel count
pixels.fill(255, 0, 0)                     # fill all red
pixels.set_pixel(0, 0, 255, 0)            # pixel 0 → green
pixels.show()                              # latch (sends WS2812 reset pulse)
pixels.deinit()
```

| Constant | Value | Description |
|---|---|---|
| `neopixel.RGB` | 0 | RGB byte order |
| `neopixel.GRB` | 1 | GRB byte order (default, matches WS2812 hardware) |
| `neopixel.RGBW` | 2 | RGBW — W channel silently ignored on WS2812 |

:::{note}
The `brightness` parameter is accepted but not applied (zero-cost constraint).
`auto_write` is always `False` — call `pixels.show()` manually after updates.
`show()` disables global interrupts during the WS2812 reset pulse, then re-enables them.
:::

---

### `time`

```python
import time

time.sleep_ms(500)         # 500 ms
time.sleep_us(100)         # 100 µs
time.sleep(1)              # integer seconds only

from pymcu.types import uint32
t: uint32 = time.monotonic()      # seconds since boot (integer)
ns: uint32 = time.monotonic_ns()  # nanoseconds since boot (wraps ~71 min)
```

:::{warning}
`time.sleep(s)` takes an integer on PyMCU. Use `time.sleep_ms(500)` instead of
`time.sleep(0.5)` — float seconds are not supported.
:::

---

### `supervisor`

```python
import supervisor
from pymcu.types import uint32

start: uint32 = supervisor.ticks_ms()       # ms since boot
# ... do work ...
elapsed: uint32 = supervisor.ticks_diff(supervisor.ticks_ms(), start)

supervisor.reload()     # software reset via watchdog
```

`ticks_ms()` uses the Timer0 millis counter auto-injected by the build driver.
`ticks_add()` and `ticks_diff()` handle 32-bit wrap-around correctly.

---

### `alarm`

```python
import alarm

# Sleep for 500 ms then continue:
t = alarm.time.TimeAlarm(monotonic_time=500)
alarm.sleep_until_alarms(t)

# Block until D2 goes HIGH:
p = alarm.pin.PinAlarm(board.D2, value=1)
alarm.sleep_until_alarms(p)

# Light-sleep variant (identical on AVR):
alarm.light_sleep_until_alarms(t)
```

:::{note}
`TimeAlarm` calls `delay_ms()` under the hood — it blocks the CPU.
`PinAlarm` polls the pin in a tight loop. For interrupt-driven wake, use
`Pin.irq()` from the `machine` module or `pymcu.hal.gpio` directly.
:::

---

### `microcontroller`

```python
import microcontroller
from pymcu.types import uint32

freq: uint32 = microcontroller.cpu.frequency   # compile-time constant (e.g. 16000000)
vcc: uint8   = microcontroller.cpu.voltage     # always 5 on 5 V AVR boards
microcontroller.delay_us(50)                   # busy-wait 50 µs
microcontroller.reset()                        # watchdog reset
```

`cpu.temperature` and `cpu.uid` are accepted for API compatibility but not
functionally implemented on ATmega328P (no factory temperature sensor or UID).

---

## Board pin constants — Arduino Uno

`import board` gives you CircuitPython-style named constants for every pin:

| Constant | Port | Arduino label | Notes |
|---|---|---|---|
| `D0` / `RX` | PD0 | D0 | USART0 RX |
| `D1` / `TX` | PD1 | D1 | USART0 TX |
| `D2` | PD2 | D2 | INT0 |
| `D3` | PD3 | D3 | INT1 / OC2B |
| `D4`–`D7` | PD4–PD7 | D4–D7 | GPIO |
| `D8` | PB0 | D8 | GPIO |
| `D9` | PB1 | D9 | OC1A (Timer1 PWM) |
| `D10` / `SS` | PB2 | D10 | SPI SS |
| `D11` / `MOSI` | PB3 | D11 | SPI MOSI / OC2A |
| `D12` / `MISO` | PB4 | D12 | SPI MISO |
| `D13` / `LED` / `LED_BUILTIN` / `SCK` | PB5 | D13 | Built-in LED / SPI SCK |
| `A0`–`A3` | PC0–PC3 | A0–A3 | ADC0–ADC3 |
| `A4` / `SDA` | PC4 | A4 | I2C SDA |
| `A5` / `SCL` | PC5 | A5 | I2C SCL |

### Supported boards

| Board name (`board =`) | Chip | Status |
|---|---|---|
| `arduino_uno` | ATmega328P | ✅ Full support |
| `arduino_nano` | ATmega328P | ✅ Same pins as Uno |
| `attiny85` | ATtiny85 | ✅ 6-pin board definition |
| `attiny84` | ATtiny84 | ✅ 12-pin board definition |
| `attiny44` / `attiny24` / `attiny45` / `attiny25` | ATtiny family | ✅ Board definitions |
| `digispark` | ATtiny85 | ✅ Digispark pin aliases |
| `attiny2313` / `attiny4313` | ATtiny family | ✅ Board definitions |

---

## Porting guide

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

### Use integer arithmetic instead of float ADC conversion

```python
# CircuitPython
voltage = adc.value * 3.3 / 65535

# PyMCU — multiply first, divide last (integer, result in millivolts)
voltage_mv: int = adc.value * 330 // 65535
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

# PyMCU — check driver error sentinel
val: int = sensor.read()
if val == -32768:    # driver-specific error sentinel
    val = -1
```

### Replace lambda callbacks with named functions

```python
# CircuitPython — lambdas work here
timer = timer.Timer(period=100, callback=lambda t: led.toggle())

# PyMCU — named function required
def on_tick():
    led.value = not led.value
```

---

## Differences from real CircuitPython

These are the **actual gaps** — anything not listed here behaves identically.

| Feature | CircuitPython | PyMCU |
|---|---|---|
| Execution model | Bytecode interpreter | **Native compiled — zero runtime overhead** |
| `time.sleep(s)` | Float seconds | Integer only — use `sleep_ms()` |
| `float` arithmetic | Full support | Soft-float (~200–400 cycles/op) |
| `f"..."` format strings | Runtime evaluation | Compile-time constants only |
| `try / except` | Supported | ❌ Not available — use error sentinels |
| `bytearray` | Dynamic heap | Fixed-size `uint8[N]` only |
| Lambda expressions | Supported | ❌ Not available — use named functions |
| `AnalogOut` | Supported (SAMD DAC) | ❌ No DAC on ATmega328P |
| `busio.I2C.scan()` | Returns list of addresses | Returns first address (no heap) |
| `neopixel.brightness` | Applies scaling | Accepted but not applied (ZCA constraint) |
| `supervisor.ticks_ms()` | 29-bit counter | 32-bit uint32 (~49-day wrap) |
| Target hardware | SAMD21, RP2040, ESP32, … | ATmega328P (Arduino Uno / Nano) |

