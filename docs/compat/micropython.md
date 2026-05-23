# MicroPython Compatibility Layer

The `pymcu-micropython` package lets you write firmware with familiar MicroPython
APIs — `machine`, `utime`, `micropython` — and compile it directly to native AVR machine
code with **zero runtime overhead**.

:::{important} Compiled, not interpreted
There is no MicroPython interpreter on the device. Every `machine.*` call is a
compile-time shim that expands directly to HAL instructions. The MCU receives only
the resulting machine code — typically a few hundred bytes of flash, 0 bytes SRAM.
:::

---

## Quick start

```bash
pip install pymcu pymcu-micropython
```

```toml
# pyproject.toml
[tool.pymcu]
stdlib = ["micropython"]
board  = "arduino_uno"
chip   = "atmega328p"
```

```python
# src/main.py  — identical to a real MicroPython script
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)    # D13 = PB5
while True:
    led.toggle()
    sleep_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex  (blink: ~130 bytes flash, 0 bytes SRAM)
pymcu flash
```

Top-level statements are automatically wrapped in a `main()` entry point by the
compiler — no need to add `if __name__ == "__main__":`.

---

## Supported modules

| Module | API surface | Status |
|---|---|---|
| `machine.Pin` | `__init__`, `high/low/on/off/toggle`, `value`, `irq`, `mode`, `init`, `__call__` | ✅ Complete |
| `machine.UART` | `write`, `read`, `write_str`, `println`, `print_byte` | ✅ Complete |
| `machine.ADC` | `read` (10-bit), `read_u16` (16-bit) | ✅ Complete |
| `machine.PWM` | `freq`, `duty_u16`, `duty`, `init`, `deinit` | ✅ Complete |
| `machine.SPI` | `write`, `read`, `write_readinto` | ✅ Complete |
| `machine.I2C` | `scan`, `writeto`, `readfrom` | ✅ Complete |
| `machine.Timer` | `__init__`, `init`, `deinit`, `start`, `irq` | ✅ |
| `machine.WDT` | `__init__`, `feed` | ✅ Complete |
| `machine.Signal` | `on`, `off`, `value` | ✅ Complete |
| `machine.mem8` / `machine.mem16` | `[]` get/set | ✅ Complete |
| `machine.freq()` | Returns CPU Hz | ✅ Complete |
| `machine.idle/lightsleep/deepsleep` | Sleep modes | ✅ Complete |
| `machine.disable_irq/enable_irq` | IRQ control | ✅ Complete |
| `machine.time_pulse_us` | Pulse measurement | ✅ Complete |
| `utime` | `sleep_ms`, `sleep_us`, `sleep`, `ticks_ms`, `ticks_diff` | ✅ Complete |
| `micropython` | `const`, `native` (stub), `viper` (stub) | ✅ Complete |
| `machine.RTC` | Real-time clock | ✗ Not planned |

---

## Module reference

### `machine.Pin`

```python
from machine import Pin

led = Pin(13, Pin.OUT)        # Arduino integer pin number
btn = Pin(2,  Pin.IN, Pin.PULL_UP)
led2 = Pin("PB5", Pin.OUT)   # port-string also accepted

led.high()        # or led.on()
led.low()         # or led.off()
led.toggle()
v = led.value()   # read → uint8
led.value(1)      # write

# Shortcut: Pin is callable
led(1)            # same as led.value(1)
v = led()         # same as led.value()
```

| Constant | MicroPython value |
|---|---|
| `Pin.IN` | 1 |
| `Pin.OUT` | 0 |
| `Pin.PULL_UP` | 1 |
| `Pin.PULL_DOWN` | 2 |
| `Pin.IRQ_FALLING` | 1 |
| `Pin.IRQ_RISING` | 2 |

Hardware interrupt configuration via `Pin.irq()` sets the trigger mode in hardware
(`EICRA`/`EIMSK`). The ISR itself must be declared with `@interrupt(vector)`:

```python
from machine import Pin
from pymcu.types import interrupt

count: int = 0

@interrupt(0x0002)          # INT0 vector — Arduino D2
def on_press():
    global count
    count += 1

def main():
    btn = Pin(2, Pin.IN, Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING) # configures hardware only
    # count is incremented by ISR
```

---

### `machine.UART`

```python
from machine import UART
from pymcu.types import uint8

uart = UART(0, 9600)         # id=0 → USART0 (only one on ATmega328P)
uart.write(65)               # send single byte
uart.write_str("hello\n")    # send string literal (PROGMEM)
uart.println("ready")        # write_str + newline

b: uint8 = uart.read()       # blocking read
uart.print_byte(42)          # sends "42\n" as decimal ASCII
```

---

### `machine.ADC`

```python
from machine import ADC, Pin
from pymcu.types import uint16

adc = ADC(Pin("A0"))
raw: uint16 = adc.read()       # 0–1023  (10-bit)
val: uint16 = adc.read_u16()   # 0–65472 (scaled ×64 to approximate 0–65535)
```

---

### `machine.PWM`

```python
from machine import PWM, Pin
from pymcu.types import uint16

pwm = PWM(Pin("PD6"), freq=1000, duty_u16=32768)  # 50% at 1 kHz
pwm.freq(490)          # change frequency (Timer0 Fast PWM)
pwm.duty_u16(49152)    # 75%  (16-bit)
pwm.duty(200)          # 78%  (8-bit direct OCR value)
pwm.deinit()
```

:::{note}
`freq()` sets the Timer0 prescaler and top value. Changing frequency affects both
OC0A (D6) and OC0B (D5) simultaneously since they share Timer0.
:::

---

### `machine.Timer`

```python
from machine import Timer
from pymcu.types import uint8

ticks: uint8 = 0

@interrupt(0x001A)    # TIMER1_COMPA vector
def on_tick():
    global ticks
    ticks += 1

def main():
    t = Timer(1, prescaler=64)   # Timer1, prescaler /64 → ~1 kHz tick at 16 MHz
    t.irq(on_tick, Timer.IRQ_COMPA)
    t.start()
    while True:
        pass
```

| Constant | Description |
|---|---|
| `Timer.ONE_SHOT` | 0 — fire once (manual stop after ISR) |
| `Timer.PERIODIC` | 1 — reload automatically |
| `Timer.IRQ_OVF` | 1 — overflow interrupt |
| `Timer.IRQ_COMPA` | 2 — compare-match A interrupt |

:::{note}
`Timer(id=0)` uses Timer0 (also used by `delay_ms` / PWM on D5/D6).
`Timer(id=1)` uses Timer1 (16-bit, best for precise periods).
`Timer(id=2)` uses Timer2 (also used by `PWM` on D11).
:::

---

### `machine.WDT`

```python
from machine import WDT

wdt = WDT(timeout=2000)   # 2-second watchdog
while True:
    wdt.feed()             # reset counter — must call within 2 s
    do_work()
```

---

### `machine.Signal`

Active-high / active-low pin abstraction. Useful for active-low LEDs or relays:

```python
from machine import Pin, Signal

relay = Signal(Pin(8, Pin.OUT), invert=True)   # active-low relay
relay.on()    # drives pin LOW  (activates relay)
relay.off()   # drives pin HIGH (deactivates relay)
relay.value(1)  # logical ON
```

---

### `machine.mem8` / `machine.mem16`

Direct register access, identical syntax to real MicroPython:

```python
from machine import mem8, mem16

# Toggle PB5 (Arduino D13 LED) by writing PORTB directly
mem8[0x25] = mem8[0x24] | 0x20    # PORTB = PINB | PB5
```

:::{tip}
For new code, prefer `from pymcu.types import ptr` — it is typed and checked at
compile time. `mem8` / `mem16` are provided purely for MicroPython source compatibility.
:::

---

### `machine` — IRQ and sleep

```python
from machine import disable_irq, enable_irq, idle, lightsleep, deepsleep

# Critical section
state = disable_irq()
# ... atomic operation ...
enable_irq(state)

# Sleep modes (wake on any interrupt)
idle()           # CPU halted, peripherals running
lightsleep()     # power-save (async timer kept running)
deepsleep()      # power-down (wake on INT0/INT1 or WDT)
```

---

### `machine.time_pulse_us`

Measure pulse duration on a pin — mirrors MicroPython's `machine.time_pulse_us`:

```python
from machine import Pin, time_pulse_us
from pymcu.types import int16

echo = Pin(7, Pin.IN)
duration: int16 = time_pulse_us(echo, 1, timeout_us=30000)
if duration == -1:
    pass  # timeout
else:
    distance_cm: int = duration // 58    # HC-SR04 approximation
```

---

### `machine.freq`

```python
from machine import freq
from pymcu.types import uint32

clk: uint32 = freq()    # returns 16000000 for Arduino Uno
```

---

### `utime`

```python
from utime import sleep_ms, sleep_us, sleep, ticks_ms, ticks_diff

sleep_ms(500)
sleep_us(100)
sleep(1)                    # 1 second (integer only — no float)

t0: uint16 = ticks_ms()    # returns 0 (stub — use Timer + @interrupt for real timestamps)
elapsed: uint16 = ticks_diff(ticks_ms(), t0)
```

:::{note}
`ticks_ms()` returns 0 in PyMCU — there is no free-running millisecond counter by
default. Use `Timer(0)` or `Timer(1)` with an `@interrupt` handler to maintain a
software `millis` counter if needed.
:::

---

### `micropython`

```python
import micropython

BAUD = micropython.const(9600)   # compile-time constant — identical to int literal

@micropython.native              # silently ignored (PyMCU always emits native code)
def fast():
    pass

@micropython.viper               # silently ignored
def also_fast():
    pass
```

---

## Drivers (pymcu-micropython)

### `LM35` — precision temperature sensor

```python
from pymcu_micropython.lm35 import LM35
from pymcu.types import uint16

sensor = LM35("A0")                    # LM35 VOUT → Arduino A0
raw: uint16  = sensor.read()           # 0–1023 raw ADC count
temp: float  = sensor.temperature()   # degrees Celsius (e.g. 24.8)
```

| Item | Detail |
|---|---|
| Import | `from pymcu_micropython.lm35 import LM35` |
| Sensor | LM35 (10 mV/°C, 5 V supply) |
| Range | 2 °C to 150 °C |
| Resolution | ~0.49 °C per ADC count |

---

## Porting guide

### Top-level scripts

MicroPython top-level scripts (no `def main():`) compile unchanged:

```python
# Works as-is in PyMCU
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)
while True:
    led.toggle()
    sleep_ms(500)
```

### Add type annotations to variables

```python
count = 0          # MicroPython — inferred at runtime
count: int = 0     # PyMCU — annotation required
```

### Replace float sleep with integer milliseconds

```python
import utime
utime.sleep(0.5)       # MicroPython  — float seconds
utime.sleep_ms(500)    # PyMCU        — integer ms
```

### Replace dynamic `bytearray` with fixed-size array

```python
buf = bytearray(8)                     # MicroPython
buf: uint8[8] = [0,0,0,0,0,0,0,0]    # PyMCU
```

### Replace `machine.mem8` with typed `ptr` (optional)

```python
machine.mem8[0x25] = 0xFF          # MicroPython — works in PyMCU too
# Typed alternative (compile-time checked):
from pymcu.types import ptr, uint8
PORTB: ptr[uint8] = ptr(0x25)
PORTB.value = 0xFF
```

### Replace `Timer` callback pattern

MicroPython-style `Timer(period=ms, callback=fn)` is **not** yet supported.
Use the `irq()` method instead:

```python
# MicroPython
tim = Timer(period=100, mode=Timer.PERIODIC, callback=lambda t: led.toggle())

# PyMCU
@interrupt(0x001A)   # TIMER1_COMPA
def on_tick():
    led.toggle()

tim = Timer(1, prescaler=64)
tim.irq(on_tick, Timer.IRQ_COMPA)
tim.start()
```

---

## Differences from real MicroPython

| Feature | MicroPython | PyMCU compat layer |
|---|---|---|
| Execution model | Bytecode interpreter (~256 KB) | **Native compiler — zero runtime** |
| `Pin.irq(handler=cb)` | Supported | Hardware config only — declare ISR with `@interrupt` |
| `Timer(period=ms, callback=cb)` | Supported | Use `Timer.irq(fn)` + `Timer.start()` |
| `ticks_ms()` | Hardware counter | Returns 0 — use Timer + ISR for real millis |
| `float` arithmetic | Full support | Soft-float (AVR, ~200–400 cycles/op) |
| `f"..."` runtime format | Supported | Compile-time constants only |
| `try / except` | Supported | Not available — use error-sentinel return values |
| `bytearray` | Dynamic heap | Fixed-size `uint8[N]` arrays |
| `machine.mem8[addr]` | Supported | ✅ Supported (shim over `ptr`) |
| `machine.Signal` | Supported | ✅ Supported |
| `machine.WDT` | Supported | ✅ Supported |
| `machine.idle/lightsleep/deepsleep` | Supported | ✅ Supported |
| Target hardware | STM32, RP2040, ESP32, … | ATmega328P (Arduino Uno) |

