# PyMCU vs Arduino

Arduino is the dominant platform for beginner and hobbyist embedded development.
This page shows how PyMCU compares — what works the same, what works differently,
and where PyMCU goes beyond what Arduino offers.

---

## The pitch

Arduino made microcontrollers accessible with a simplified C++ API.  PyMCU does
the same for Python: write familiar Python syntax, get the same tight native code
you'd get from C.

```{tab-set}
```{tab-item} Arduino (C++)
\`\`\`cpp
void setup() {
  pinMode(13, OUTPUT);
}

void loop() {
  digitalWrite(13, HIGH);
  delay(500);
  digitalWrite(13, LOW);
  delay(500);
}
\`\`\`
```{tab-item} PyMCU (Python)
\`\`\`python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)  # D13 on Uno
    while True:
        led.toggle()
        delay_ms(500)
\`\`\`
```
```

Both compile to the same 36-byte AVR toggle loop (142 bytes total firmware including interrupt vector table).

---

## Function equivalents

| Arduino function | PyMCU equivalent | Notes |
|------------------|-----------------|-------|
| `pinMode(n, OUTPUT)` | `Pin("PB5", Pin.OUT)` | Pin name is compile-time typed |
| `digitalWrite(n, HIGH)` | `led.high()` | Inlines to a single `SBI` |
| `digitalRead(n)` | `led.value` | Single `SBIS`/`SBIC` |
| `analogRead(n)` | `AnalogPin(n).read()` | 10-bit, same as Arduino |
| `analogWrite(n, v)` | `PWM("PD6", v).start()` | Hardware PWM channels only |
| `delay(ms)` | `delay_ms(ms)` | Exact same semantics |
| `millis()` | `millis()` | From `pymcu.time` |
| `micros()` | `micros()` | From `pymcu.time` |
| `tone(pin, freq)` | `tone(freq)` | Hardware toggle on OC2A (D11) |
| `noTone(pin)` | `noTone()` | |
| `map(x, ...)` | `map_range(x, ...)` | From `pymcu.math` |
| `constrain(x, lo, hi)` | `constrain(x, lo, hi)` | From `pymcu.math` |
| `random(n)` | `random(n)` | From `pymcu.random` |
| `randomSeed(s)` | `randomSeed(s)` | From `pymcu.random` |
| `attachInterrupt(pin, fn, FALLING)` | `pin.irq(fn, Pin.IRQ_FALLING)` | |
| `Serial.begin(baud)` | `UART(baud)` | |
| `Serial.print(x)` | `uart.write_str(x)` | |
| `Serial.println(x)` | `uart.println(x)` | |
| `EEPROM.read(addr)` | `eeprom.read(addr)` | |
| `EEPROM.write(addr, v)` | `eeprom.write(addr, v)` | |
| `Wire.begin()` | `I2C(0)` | master mode |
| `Wire.beginTransmission(addr)` + `Wire.write(b)` | `i2c.write_to(addr, buf, n)` | |
| `SPI.begin()` + `SPI.transfer(b)` | `SPI(SPI.CONTROLLER).transfer(b)` | |
| `Servo.write(degrees)` | `Servo("PB1").write(degrees)` | Timer1, D9 or D10 |

### MicroPython / CircuitPython compat layer

If you already know MicroPython, import the compat shim and use the API you know:

```python
from machine import Pin, UART, SPI, I2C, ADC, Timer, PWM
import utime
```

The compat layer is a thin compile-time alias — it produces identical machine code
to the native PyMCU HAL.

---

## What PyMCU adds

### 1. Static typing catches errors at compile time

```python
duty: uint8 = read_sensor()  # uint8 — compiler enforces the type
pwm.set_duty(duty)           # no runtime type check needed
```

Arduino has no type checking.  A silent integer truncation in C++ can cause subtle
hardware bugs that are nearly impossible to find with a serial monitor.

### 2. Source-level debugger

PyMCU ships a full **VS Code DAP debugger** — set breakpoints directly on Python
source lines, step through code, inspect all 32 registers and SRAM in real time.

Arduino's only debugging tool is `Serial.print()`.

```{image} ../_static/debugger-screenshot.png
:alt: PyMCU VS Code debugger showing breakpoint on line 42
:width: 600px
```

### 3. CPU profiler with flamegraphs

```bash
pymcu profile --ms 5000 -o profile.speedscope.json
```

Drag `profile.speedscope.json` onto [speedscope.app](https://speedscope.app) to see
exactly where your firmware spends CPU time, down to the individual function call.
Works with multi-task RTOS firmware — each task gets its own profile tab.

No equivalent exists for Arduino.

### 4. RTOS in Python

```python
from rtos import add_task, start_scheduler, Priority

add_task(sensor_task, Priority.HIGH)    # 50% CPU
add_task(display_task, Priority.NORMAL) # 25% CPU
add_task(blink_task,   Priority.IDLE)   # 12.5% CPU
start_scheduler()  # preemptive, 1 ms tick
```

A production-grade preemptive RTOS in pure Python, compiled to tight AVR assembly.
See the [rtos-multitask example](../examples/index.md).

### 5. Zero-cost abstractions

Every HAL method is `@inline` — no function call overhead, no virtual dispatch.
The compiler folds chip-dispatch `match` blocks at compile time and emits exactly
the instructions for the selected hardware.

A `led.toggle()` call on ATmega328P compiles to a single `SBIS PORTB,5 / RJMP +2 /
SBI PORTB,5 / RJMP +2 / CBI PORTB,5` sequence — the same as hand-written assembly.

### 6. C interop via `@extern`

```python
from pymcu.ffi import extern

@extern("crc8_maxim", header="crc.h")
def crc8(data: ptr[uint8], len: uint8) -> uint8: ...
```

Call any C library function directly from Python.  PyMCU handles the calling
convention; no wrapper glue code needed.

---

## What Arduino still has

| Arduino advantage | Notes |
|-------------------|-------|
| Library ecosystem | 4000+ libraries; PyMCU has a growing stdlib + FFI for C libs |
| More boards | ESP32, STM32, RP2040, SAMD, nRF52 (PyMCU: AVR + PIC + RISC-V, ARM coming) |
| Arduino IDE simplicity | PyMCU uses VS Code; more powerful but higher initial setup |
| Community / tutorials | Vast corpus of Arduino examples |

---

## Code size comparison

Compiling `blink` (toggle D13, delay 500 ms) for Arduino Uno (16 MHz):

| Toolchain | Flash | SRAM |
|-----------|-------|------|
| Arduino (avr-gcc -Os) | ~928 bytes | 9 bytes |
| PyMCU | **142 bytes** | **0 bytes** |

Both numbers are total firmware (user code + interrupt vector table). PyMCU's 142 bytes
breaks down as 36 bytes of user code + 106 bytes of IVT and startup stub — the same fixed
overhead any AVR toolchain emits.

PyMCU eliminates the Arduino runtime overhead (init code, millis ISR,
`main()` wrapper, `Serial` globals) that is linked in even for simple programs.

---

## Summary

PyMCU is the right choice when you want:

- **Python syntax** with zero runtime overhead
- **Static typing** to catch mistakes at compile time
- **Professional tooling**: VS Code debugger, Speedscope profiler
- **RTOS** without a heavy C library
- **Minimal flash footprint** on constrained chips (ATtiny, ATmega48)

Arduino is the right choice when you need:

- The vast existing library ecosystem
- Boards beyond AVR (ESP32, STM32, RP2040)
- A simpler zero-config IDE
