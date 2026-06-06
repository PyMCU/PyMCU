# MicroPython Compatibility Layer

The `pymcu-micropython` package lets you write firmware using the familiar MicroPython
APIs — `machine`, `utime`, `micropython`, and the AVR-specific `avr` port module — and
compile it directly to native AVR machine code with **zero runtime overhead**.

:::{important} Compiled, not interpreted
There is no MicroPython interpreter on the device. Every `machine.*` or `avr.*` call is a
compile-time shim that expands directly to HAL instructions. The MCU receives only the
resulting machine code — typically a few hundred bytes of flash, 0 bytes SRAM overhead.
:::

The layer ships board definitions for **`arduino_uno`**, **`arduino_nano`**,
**`arduino_micro`**, **`arduino_mega`**, **`digispark`**, and the ATtiny family
(`attiny13`/`13a`, `attiny24`/`44`/`84`, `attiny25`/`45`/`85`, `attiny2313`/`4313`).
Select one with `board = "…"` in `[tool.pymcu]`. The examples below target the
**ATmega328P** (Arduino Uno / Nano). The pin numbering in `machine.Pin` follows the
Arduino integer convention (D0–D13, A0–A5); port strings such as `"PB5"` are also
accepted directly.

---

## Quick start

```bash
pip install pymcu-compiler pymcu-micropython
```

:::{note}
The compiler/CLI is currently published as **`pymcu-compiler`** on PyPI (the
`pymcu` command comes from it). A [PEP 541][pep541] request for the shorter
`pymcu` name is in progress — the original owner has agreed to transfer it —
so a future release may install simply as `pip install pymcu`.

[pep541]: https://peps.python.org/pep-0541/
:::

```toml
# pyproject.toml
[tool.pymcu]
stdlib    = ["micropython"]
board     = "arduino_uno"     # implies the chip (atmega328p) and pin map
frequency = 16000000
sources   = "src"
entry     = "main.py"
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

Top-level statements are automatically wrapped in a `main()` entry point — no `if __name__ == "__main__":` needed.

---

## Supported modules

| Module | API surface | Status |
|---|---|---|
| `machine.Pin` | `__init__`, `high/low/on/off/toggle`, `value`, `irq`, `mode`, `init`, `__call__` | ✅ Complete |
| `machine.UART` | `write(byte)`, `write(str)`, `read`, `any`, `write_str`, `println`, `print_byte` | ✅ Complete |
| `machine.ADC` | `read` (10-bit), `read_u16` (16-bit scaled) | ✅ Complete |
| `machine.PWM` | `freq`, `duty_u16`, `duty`, `init`, `deinit` | ✅ Complete |
| `machine.SPI` | `write`, `read`, `write_readinto`, `init`, `deinit` | ✅ Complete |
| `machine.I2C` | `scan`, `writeto`, `readfrom` | ✅ Complete |
| `machine.Timer` | `__init__`, `init`, `deinit`, `start`, `irq` | ✅ Complete |
| `machine.WDT` | `__init__`, `feed` | ✅ Complete |
| `machine.Signal` | `on`, `off`, `value` | ✅ Complete |
| `machine.mem8` / `machine.mem16` | `[]` get/set | ✅ Complete |
| `machine.freq()` | Returns CPU Hz | ✅ Complete |
| `machine.reset()` | Software reset (via watchdog) | ✅ Complete |
| `machine.idle/lightsleep/deepsleep` | Sleep modes | ✅ Complete |
| `machine.disable_irq/enable_irq` | IRQ control | ✅ Complete |
| `machine.time_pulse_us` | Pulse measurement | ✅ Complete |
| `utime` | `sleep_ms`, `sleep_us`, `sleep`, `ticks_ms`, `ticks_us`, `ticks_cpu`, `ticks_diff`, `ticks_add` | ✅ Complete |
| `micropython` | `const`, `native` (stub), `viper` (stub) | ✅ Complete |
| `avr.EEPROM` | `read`, `write` | ✅ Complete |
| `avr.SoftSPI` | `transfer`, `write`, `select`, `deselect` | ✅ Complete |
| `avr.SoftI2C` | `scan`, `writeto`, `readfrom`, `ping` | ✅ Complete |
| `print()` | UART output — auto-injects UART init | ✅ Complete |
| `input()` | UART input — reads line from UART | ✅ Complete |
| `machine.RTC` | Real-time clock | ✗ Not planned |

---

## Module reference

### `machine.Pin`

```python
from machine import Pin

led  = Pin(13, Pin.OUT)              # Arduino D13 = PB5
btn  = Pin(2,  Pin.IN, Pin.PULL_UP)
led2 = Pin("PB5", Pin.OUT)          # port-string also accepted

led.high()        # or led.on()
led.low()         # or led.off()
led.toggle()
v = led.value()   # read → uint8
led.value(1)      # write

# Pin is callable (MicroPython shortcut)
led(1)      # same as led.value(1)
v = led()   # same as led.value()
```

#### Pin number mapping (Arduino Uno)

| Arduino pin | Port/bit | AVR register |
|---|---|---|
| D0 | PD0 | PORTD bit 0 |
| D1 | PD1 | PORTD bit 1 |
| D2 | PD2 | PORTD bit 2 (INT0) |
| D3 | PD3 | PORTD bit 3 (INT1, OC2B) |
| D4 | PD4 | PORTD bit 4 |
| D5 | PD5 | PORTD bit 5 (OC0B) |
| D6 | PD6 | PORTD bit 6 (OC0A) |
| D7 | PD7 | PORTD bit 7 |
| D8 | PB0 | PORTB bit 0 |
| D9 | PB1 | PORTB bit 1 (OC1A) |
| D10 | PB2 | PORTB bit 2 (OC1B, SS) |
| D11 | PB3 | PORTB bit 3 (OC2A, MOSI) |
| D12 | PB4 | PORTB bit 4 (MISO) |
| D13 | PB5 | PORTB bit 5 (SCK, LED) |

| Constant | Value | Description |
|---|---|---|
| `Pin.IN` | 1 | Input (DDRx bit cleared) |
| `Pin.OUT` | 0 | Output (DDRx bit set) |
| `Pin.PULL_UP` | 1 | Enable internal pull-up (PORTx = 1 when IN) |
| `Pin.PULL_DOWN` | 2 | No hardware pull-down on AVR — stub |
| `Pin.IRQ_FALLING` | 1 | Falling edge trigger |
| `Pin.IRQ_RISING` | 2 | Rising edge trigger |

Hardware interrupt configuration via `Pin.irq()`. Pass a `handler` directly — the
build driver registers it as the hardware ISR for the corresponding INT vector.

```python
from machine import Pin
from pymcu.types import uint8

count: uint8 = 0

def on_press():
    global count
    count += 1

def main():
    btn = Pin(2, Pin.IN, Pin.PULL_UP)
    btn.irq(handler=on_press, trigger=Pin.IRQ_FALLING)   # INT0, falling edge
    while True:
        pass
```

The `handler` is passed as a keyword argument (MicroPython style) or positional.
Under the hood, `Pin.irq()` calls `pymcu.hal.gpio.Pin.irq(trigger, handler)` which
configures `EICRA`/`EIMSK` and registers the ISR at the correct AVR vector.

`@interrupt` is still available for low-level control, but `Pin.irq(handler=cb)` is
the idiomatic MicroPython-compatible form.

```python
# Both forms produce identical firmware:
btn.irq(handler=on_press, trigger=Pin.IRQ_FALLING)   # MicroPython style
btn.irq(Pin.IRQ_FALLING, on_press)                    # positional (HAL order)
```

---

### `machine.UART`

**Constructor:**

```python
UART(id=0, baudrate=9600)
```

The ATmega328P has one hardware UART (USART0). `id` is accepted for API
compatibility but ignored — `0` is the only meaningful value. The `baudrate` is
resolved at compile time against `frequency` in `pyproject.toml`; only standard
baud rates (9600, 19200, 38400, 57600, 115200) are guaranteed to produce exact
UBRR values with less than 0.5% error.

```python
from machine import UART
from pymcu.types import uint8

uart = UART(0, 9600)           # id ignored → USART0; baud rate set at compile time
uart.write(65)                 # send single byte  (ASCII 'A')
uart.write_str("hello\n")      # send string literal from PROGMEM
uart.println("ready")          # write_str + newline

b: uint8 = uart.read()         # blocking read — spins until RXC flag is set
uart.print_byte(42)            # sends "42\n" as decimal ASCII digits
```

| Method | Signature | Description |
|---|---|---|
| `write(byte)` | `(byte: uint8)` | Send a single byte |
| `write(s)` | `(s: str)` | Send a compile-time string literal — `uart.write("OK\n")` |
| `read()` | `() -> uint8` | Blocking read — spins until RXC flag is set |
| `readline(buf, max_len)` | `(buf: bytearray, max_len: uint8) -> uint8` | Read until `\n` (or `max_len-1` bytes) into caller-provided buffer; returns byte count |
| `readinto(buf, nbytes)` | `(buf: bytearray, nbytes: uint8) -> uint8` | Read exactly `nbytes` bytes into buffer; returns `nbytes` |
| `any()` | `() -> uint8` | Returns `1` if at least one byte is waiting, `0` otherwise |
| `write_str(s)` | `(s: str)` | PyMCU extension — prefer `write(str)` for portability |
| `println(s)` | `(s: str)` | PyMCU extension — `write_str(s)` + newline |
| `print_byte(n)` | `(n: uint8)` | PyMCU extension — decimal ASCII digits + newline |

:::{note}
`print()` — the Python built-in — is the simplest way to write to the UART from
MicroPython-style code. The build driver detects `print(` in your source and
automatically injects `uart_init()` before `main()`. No manual `UART(0, ...)` required.
See [Built-in functions](#built-in-functions) below.
:::

---

### `machine.ADC`

```python
from machine import ADC, Pin
from pymcu.types import uint16

adc = ADC(Pin("A0"))
raw: uint16 = adc.read()       # 0–1023  (10-bit, ATmega328P native)
val: uint16 = adc.read_u16()   # 0–65472 (scaled ×64 to approximate MicroPython 0–65535)
```

The ATmega328P ADC is 10-bit. `read_u16()` scales the result by 64 to match MicroPython's
convention of returning a 16-bit value; the maximum is 65472 (1023 × 64), not 65535.

---

### `machine.PWM`

```python
from machine import PWM, Pin

pwm = PWM(Pin("PD6"), freq=1000, duty_u16=32768)  # D6, 50% at 1 kHz
pwm.freq(490)          # change frequency
pwm.duty_u16(49152)    # 75%  (16-bit, 0–65535 range)
pwm.duty(200)          # 78%  (8-bit OCR value, 0–255)
pwm.deinit()           # stop and detach timer
```

:::{note}
`freq()` sets the Timer0 prescaler. D5 (OC0B) and D6 (OC0A) share Timer0; changing
frequency on one affects both. For independent frequency control, use D9/D10 (Timer1)
or D11 (Timer2) via the HAL directly.
:::

#### AVR PWM pins

| Arduino pin | Timer | Channel | Notes |
|---|---|---|---|
| D5 | Timer0 | OC0B | Shares freq with D6 |
| D6 | Timer0 | OC0A | Shares freq with D5 |
| D9 | Timer1 | OC1A | 16-bit, independent |
| D10 | Timer1 | OC1B | 16-bit, independent |
| D11 | Timer2 | OC2A | 8-bit, independent |

---

### `machine.SPI`

**Constructor:**

```python
SPI()
```

Hardware SPI on the ATmega328P uses fixed pins: D13 (SCK), D11 (MOSI), D12 (MISO),
configured as controller (master) inside the constructor. The chip-select pin must
be managed manually with a `Pin` — this matches standard MicroPython behaviour.

```python
from machine import SPI, Pin
from pymcu.types import uint8

spi = SPI()                       # hardware SPI, controller mode
cs  = Pin(10, Pin.OUT)

# Single-byte variants (MicroPython-compatible signatures)
cs.low()
spi.write(0x9F)                   # send command byte (discard MISO)
device_id: uint8 = spi.read(0xFF) # send 0xFF dummy, return MISO byte
cs.high()

# Multi-byte variants (PyMCU deviation — explicit n required)
buf: bytearray = bytearray(4)
buf[0] = 0xAA
buf[1] = 0xBB
rxbuf: bytearray = bytearray(4)

cs.low()
spi.write(buf, 3)                          # send 3 bytes, discard MISO
spi.readinto(rxbuf, 3)                     # receive 3 bytes (clocks 0xFF as dummy)
spi.readinto(rxbuf, 3, 0x00)               # receive 3 bytes, clock 0x00 as dummy
spi.write_readinto(buf, rxbuf, 3)          # full-duplex: send 3 and receive 3
cs.high()
```

| Method | Signature | Description |
|---|---|---|
| `write(byte)` | `(byte: uint8)` | Send one byte, discard MISO |
| `write(buf, n)` | `(buf: bytearray, n: uint8)` | Send `n` bytes from buffer, discard MISO |
| `read(write_byte=0xFF)` | `(write_byte: uint8) -> uint8` | Send `write_byte`, return MISO byte |
| `readinto(buf, n, write_byte=0xFF)` | `(buf: bytearray, n: uint8, write_byte: uint8)` | Receive `n` bytes into buffer, clock `write_byte` as dummy |
| `write_readinto(out, in_val)` | `(out: uint8, in_val: uint8) -> uint8` | Full-duplex single-byte exchange |
| `write_readinto(write_buf, read_buf, n)` | `(write_buf: bytearray, read_buf: bytearray, n: uint8)` | Full-duplex `n`-byte exchange |
| `init()` / `deinit()` | `()` | No-ops (SPI is set up in the constructor) |

:::{note}
The constructor takes no arguments — the SPI clock divider, polarity, and bit order
use the HAL defaults (controller mode, MSB-first, f_osc/4). For a configurable clock
or arbitrary pins, use `avr.SoftSPI` (see below).
:::

---

### `machine.I2C`

**Constructor:**

```python
I2C(scl=None, sda=None, freq=100000)
```

Hardware I2C (TWI) uses fixed pins: A4 (SDA = PC4) and A5 (SCL = PC5). The `scl`
and `sda` arguments are accepted for MicroPython API compatibility but ignored —
the hardware pins are fixed on the ATmega328P. Requires 4.7 kΩ external pull-up
resistors on both lines.

```python
from machine import I2C
from pymcu.types import uint8

i2c = I2C(freq=100000)             # 100 kHz standard mode (scl/sda are fixed)
count: uint8 = i2c.scan()          # count of responding devices (not a list)

# Single-byte variants (MicroPython-compatible signatures)
i2c.writeto(0x3C, 0x00)            # write command byte to SSD1306
val: uint8 = i2c.readfrom(0x3C)    # read one byte

# Multi-byte variants (PyMCU deviation — explicit n required)
buf: uint8[4] = bytearray(4)
i2c.writeto(0x48, buf, 3)          # write 3 bytes from buf to 0x48
n: uint8 = i2c.readfrom_into(0x48, buf, 3)  # read 3 bytes into buf; returns 1=ok, 0=NACK
```

| Method | Signature | Description |
|---|---|---|
| `scan()` | `() -> uint8` | Count of responding devices (not a list) |
| `scan(buf, max_count)` | `(buf: bytearray, max_count: uint8) -> uint8` | Store addresses into caller buffer; returns count |
| `writeto(addr, data)` | `(addr: uint8, data: uint8)` | Write one byte to address |
| `writeto(addr, buf, n)` | `(addr: uint8, buf: bytearray, n: uint8)` | Write `n` bytes from buffer to address |
| `readfrom(addr)` | `(addr: uint8) -> uint8` | Read one byte from address |
| `readfrom_into(addr, buf, n)` | `(addr: uint8, buf: bytearray, n: uint8) -> uint8` | Read `n` bytes into buffer; returns 1 on ACK, 0 on NACK |

:::{note}
`scan()` returns a device *count* rather than a list of addresses — returning a `list[uint8]`
would pull in GC infrastructure for a result most programs discard immediately. Use
`scan(buf, max_count)` to capture addresses into a caller-owned buffer at zero GC cost,
or `pymcu.hal.i2c.I2C.ping(addr)` to probe a specific address directly.
:::

For bit-bang I2C with arbitrary GPIO pins, use `avr.SoftI2C` (see below).

---

### `machine.Timer`

```python
from machine import Timer
from pymcu.types import uint8

ticks: uint8 = 0

def on_tick():
    global ticks
    ticks += 1

def main():
    # MicroPython style: configure by frequency
    t = Timer(1, freq=10, callback=on_tick)       # 10 Hz == 100 ms

    # Alternative: configure by period in ms
    t = Timer(1, period=100, callback=on_tick)    # fires every 100 ms

    # Low-level style (still works):
    # t = Timer(1, prescaler=64)
    # t.irq(on_tick, Timer.IRQ_COMPA)
    # t.start()

    while True:
        pass
```

`Timer(id, freq=Hz)` and `Timer(id, period=ms)` both auto-select a prescaler and OCR
value and register the callback in one step — identical to the MicroPython API.

`Timer.init(freq=Hz)` prescaler selection for AVR @ 16 MHz (Timer1, 16-bit):

| `freq` (Hz) | Prescaler | OCR formula |
|---|---|---|
| ≥ 245 | 1 | `16 000 000 / freq − 1` |
| ≥ 31 | 8 | `2 000 000 / freq − 1` |
| ≥ 4 | 64 | `250 000 / freq − 1` (exact) |
| ≥ 1 | 1024 | `15 625 / freq − 1` |

`Timer.init(period=ms)` prescaler selection:

| `period` (ms) | Prescaler | OCR formula |
|---|---|---|
| ≤ 262 | 64 | `250 × period − 1` (exact at 16 MHz) |
| ≤ 4194 | 1024 | `15 × period` (~0.6 ms error per step) |

| Constant | Value | Description |
|---|---|---|
| `Timer.ONE_SHOT` | 0 | Fire once (stop after first interrupt) |
| `Timer.PERIODIC` | 1 | Reload automatically (default) |
| `Timer.IRQ_OVF` | 1 | Overflow interrupt |
| `Timer.IRQ_COMPA` | 2 | Compare-match A interrupt |

#### AVR Timer resources

| Timer id | Width | Vectors | Default use |
|---|---|---|---|
| 0 | 8-bit | OVF, COMPA, COMPB | `delay_ms`, PWM on D5/D6 |
| 1 | 16-bit | OVF, COMPA, COMPB | Best for precise intervals |
| 2 | 8-bit | OVF, COMPA, COMPB | PWM on D11 |

:::{warning}
`Timer(id=0)` shares resources with `delay_ms()` and PWM on D5/D6. Prefer `Timer(1)` for
general-purpose periodic interrupts.
:::

---

### `machine.WDT`

**Constructor:**

```python
WDT(id=0, timeout=5000)
```

The `id` argument is accepted for API compatibility and ignored (single watchdog
on AVR). Configures the ATmega328P hardware watchdog. The MCU resets if `feed()` is not called
within the configured window. Useful as a fault-recovery safety net.

```python
from machine import WDT

wdt = WDT(timeout=2000)    # 2-second watchdog window
while True:
    wdt.feed()              # must be called within 2 s or MCU resets
    do_work()
```

The `timeout` is in milliseconds and is rounded to the nearest ATmega328P prescaler step:

| `timeout` (ms) | Prescaler | Actual window |
|---|---|---|
| ≤ 16 | WDP0 | 16 ms |
| ≤ 32 | WDP1 | 32 ms |
| ≤ 64 | WDP2 | 64 ms |
| ≤ 125 | WDP1+WDP0 | 125 ms |
| ≤ 250 | WDP2+WDP0 | 250 ms |
| ≤ 500 | WDP2+WDP1 | 500 ms |
| ≤ 1000 | WDP2+WDP1+WDP0 | 1 s |
| ≤ 2000 | WDP3 | 2 s |
| ≤ 4000 | WDP3+WDP0 | 4 s |
| ≤ 8000 | WDP3+WDP1 | 8 s |

| Method | Description |
|---|---|
| `feed()` | Reset the watchdog counter (must be called periodically) |

---

### `machine.Signal`

Active-high / active-low pin abstraction. Useful for active-low LEDs or relay modules:

```python
from machine import Pin, Signal

relay = Signal(Pin(8, Pin.OUT), invert=True)   # active-low relay board
relay.on()     # drives pin LOW  (activates relay)
relay.off()    # drives pin HIGH (deactivates relay)
relay.value(1) # logical ON  → LOW
```

---

### `machine.mem8` / `machine.mem16`

Direct register access, identical syntax to real MicroPython:

```python
from machine import mem8, mem16

# Toggle the LED on D13 (PB5) by writing PORTB directly
mem8[0x25] = mem8[0x24] | 0x20    # PORTB = PINB | PB5

# Read/write a 16-bit SFR (e.g., Timer1 counter TCNT1 at 0x84)
mem16[0x84] = 0    # reset Timer1 counter
```

:::{tip}
For new code, prefer `from pymcu.types import ptr` — it is typed and verified at
compile time. `mem8` / `mem16` exist purely for MicroPython source compatibility.
:::

#### Commonly used ATmega328P register addresses

| Address | Register | Description |
|---|---|---|
| `0x23` | `PINB` | Port B input pins |
| `0x24` | `DDRB` | Port B direction |
| `0x25` | `PORTB` | Port B output |
| `0x26` | `PINC` | Port C input pins |
| `0x27` | `DDRC` | Port C direction |
| `0x28` | `PORTC` | Port C output |
| `0x29` | `PIND` | Port D input pins |
| `0x2A` | `DDRD` | Port D direction |
| `0x2B` | `PORTD` | Port D output |
| `0x78` | `ADCL` | ADC low byte |
| `0x79` | `ADCH` | ADC high byte |
| `0x7A` | `ADCSRA` | ADC control/status |
| `0x84` | `TCNT1` | Timer1 counter (16-bit) |

---

### `machine` — IRQ and sleep

```python
from machine import disable_irq, enable_irq, idle, lightsleep, deepsleep, reset

# Atomic / critical section
state = disable_irq()   # CLI instruction — disables global interrupts
# ... atomic operation ...
enable_irq(state)       # SEI instruction — restores interrupts

# Sleep modes (wake on any enabled interrupt)
idle()           # Idle: CPU halted, all peripherals running (~70% power reduction)
lightsleep()     # Power-save: async timer kept alive; I/O and Timer2 active
deepsleep()      # Power-down: only INT0/INT1 or WDT can wake (~99% reduction)

# Software reset (triggers a watchdog reset to restart the MCU)
reset()
```

---

### `machine.time_pulse_us`

Measure the duration of a pulse on a pin. Mirrors MicroPython's `machine.time_pulse_us`:

```python
from machine import Pin, time_pulse_us
from pymcu.types import int16

echo = Pin(7, Pin.IN)
duration: int16 = time_pulse_us(echo, 1, timeout_us=30000)
if duration == -1:
    pass   # timeout — no pulse detected
else:
    distance_cm: int = duration // 58    # HC-SR04: 58 µs ≈ 1 cm
```

Returns the pulse width in microseconds, or `-1` on timeout.

---

### `machine.freq`

```python
from machine import freq
from pymcu.types import uint32

clk: uint32 = freq()    # returns 16000000 for a 16 MHz Arduino Uno
```

The value is a compile-time constant derived from `frequency` in `pyproject.toml`.

---

### `utime`

```python
from utime import sleep_ms, sleep_us, sleep, ticks_ms, ticks_us, ticks_cpu, ticks_diff, ticks_add
from pymcu.types import uint32

sleep_ms(500)       # busy-wait 500 ms  (uses _delay_ms loop)
sleep_us(100)       # busy-wait 100 µs
sleep(1)            # 1 second (integer only — no float on AVR)

t0: uint32 = ticks_ms()
sleep_ms(200)
elapsed: uint32 = ticks_diff(ticks_ms(), t0)   # elapsed ≈ 200

# Deadline pattern (identical to real MicroPython):
deadline: uint32 = ticks_add(ticks_ms(), 500)  # 500 ms from now

us: uint32 = ticks_us()         # microseconds (4 µs resolution at 16 MHz)
cpu: uint32 = ticks_cpu()       # alias for ticks_us() on AVR
```

:::{note}
`ticks_ms()` requires the **millis counter** to be running. The `pymcu build` driver
detects `ticks_ms()` usage and automatically injects `millis_init()` before your code
runs — no manual setup needed. This is the same auto-injection pattern used for
`print()` / UART initialisation.

`millis_init()` configures **Timer0** in normal overflow mode at prescaler 64 (~1 ms
resolution at 16 MHz). Do not use Timer0 for PWM or CTC in the same project when
`ticks_ms()` is active. `delay_ms()` / `delay_us()` are unaffected (software busy-loop).
:::

| Function | Notes |
|---|---|
| `sleep_ms(n)` | Busy-wait via `_delay_ms` loop |
| `sleep_us(n)` | Busy-wait via `_delay_us` loop |
| `sleep(n)` | Integer seconds (`delay_ms(n * 1000)`) |
| `ticks_ms()` | Milliseconds since boot — Timer0 counter (`millis()`) |
| `ticks_us()` | Microseconds since boot — `micros()`, ~4 µs resolution at 16 MHz |
| `ticks_cpu()` | Alias for `ticks_us()` (no separate CPU timer on AVR) |
| `ticks_diff(a, b)` | `a - b` with uint32 wrap-around |
| `ticks_add(t, d)` | `t + d` with uint32 wrap-around — compute deadline from `ticks_ms()` |

---

### `micropython`

```python
import micropython

BAUD = micropython.const(9600)   # compile-time constant — same as writing 9600

@micropython.native              # silently ignored — PyMCU always emits native code
def fast():
    pass

@micropython.viper               # silently ignored — use @inline for zero-cost inlining
def also_fast():
    pass
```

`micropython.const()` is an identity function at the PyMCU level. All integer literals
annotated as `const[T]` are already compile-time folded by the optimizer.

---

(built-in-functions)=
## Built-in functions

PyMCU supports a subset of Python's built-in functions. Functions that require dynamic
memory allocation or a runtime type system are not available, but the two most common
I/O built-ins — `print()` and `input()` — are fully supported via the UART.

---

### `print()`

```python
print("hello, world")      # sends "hello, world\n" to UART0
print("value:", 42)        # multiple arguments — space-separated
```

`print()` is the simplest way to emit text to the serial monitor. It behaves identically
to Python's built-in `print()` for string and integer arguments.

**Auto-injection:** The `pymcu build` driver scans your source for `print(` and
automatically injects `uart_init(baud)` before `main()` runs. You do not need to
initialise the UART manually when `print()` is the only UART user.

| Behaviour | Python / MicroPython | PyMCU |
|---|---|---|
| `print("s")` | `s\n` to stdout | `s\n` to UART0 |
| `print("a", "b")` | `a b\n` | `a b\n` to UART0 |
| `print(42)` | `42\n` | `42\n` to UART0 |
| `print("n =", n)` | runtime format | compile-time if both constant |
| `print()` (no args) | empty line | empty `\n` |
| keyword args (`end`, `sep`, `file`) | supported | ❌ not supported |

---

### `input()`

Reads a line of text from UART0. The line is terminated by a newline character (`\n`).
Windows-style CRLF sequences (`\r\n`) are handled transparently — the `\r` is silently
stripped.

**Syntax:**

```python
line: bytearray = input()
line: bytearray = input("prompt")
line: bytearray = input("prompt", maxlen)
```

- `prompt` — optional compile-time string literal, sent to UART before reading
- `maxlen` — optional integer; default 64. The buffer is allocated as `uint8[maxlen]`
  on the stack — it is a compile-time constant, not a heap allocation.
- Return type **must** be annotated as `bytearray`.

```python
from pymcu.types import uint8

def main():
    name: bytearray = input("Enter name: ")    # prints prompt, reads until newline
    print("Hello, ")
    print(name)

    # Read with explicit buffer limit
    cmd: bytearray = input("> ", 16)           # max 16 characters
```

**Auto-injection:** Like `print()`, the driver detects `input(` and injects
the UART initialisation automatically. The output device and baud rate come from
the optional `stdout` / `stdout_baud` keys in `[tool.pymcu]` (defaults: `uart0`
at `115200` baud).

**Under the hood:** `input("prompt")` lowers to:

1. `uart_write_str("prompt")` — sends the prompt string from PROGMEM
2. `uart_read_line(buf, maxlen)` — reads bytes until `\n` or `maxlen−1` bytes consumed
3. Returns a `bytearray` pointing to the stack buffer

:::{note}
`input()` is a **blocking call** — execution pauses until the user presses Enter.
There is no timeout. If your application must remain responsive, read the
receive-complete flag yourself (e.g. via `machine.mem8` on `UCSR0A`) and
accumulate bytes one at a time with `machine.UART.read()`.
:::

:::{warning}
The buffer lives on the AVR stack frame. Do not let `maxlen` exceed the available
stack space (typically ~200–400 bytes on an ATmega328P with the default 2 KB SRAM).
:::

**Example — UART command loop:**

```python
from pymcu.types import uint8

def handle(cmd: bytearray) -> uint8:
    # compare against known commands
    return 0

def main():
    while True:
        cmd: bytearray = input("> ", 32)
        handle(cmd)
```

---

## `avr` — AVR port module

The `avr` module exposes AVR-specific peripherals not covered by the `machine` module:
non-volatile EEPROM storage, bit-bang SPI, and bit-bang I2C. Import from `avr` (no
package prefix needed when `stdlib = ["micropython"]` is set).

```python
from avr import EEPROM, SoftSPI, SoftI2C
```

### `avr.EEPROM`

The ATmega328P has 1 KB of EEPROM at addresses 0x000–0x3FF, retained across resets and
power cycles. Reads and writes go through the `EEAR`/`EEDR`/`EECR` register set.

```python
from avr import EEPROM
from pymcu.types import uint8, uint16

ee = EEPROM()

# Write a calibration constant at address 0
ee.write(0, 42)

# Read it back after a reset
val: uint8 = ee.read(0)    # → 42

# Store a 16-bit value across two bytes
high: uint8 = 0xAB
low:  uint8 = 0xCD
ee.write(10, high)
ee.write(11, low)
```

| Method | Signature | Description |
|---|---|---|
| `read(addr)` | `(addr: uint16) -> uint8` | Read one byte from EEPROM address |
| `write(addr, value)` | `(addr: uint16, value: uint8)` | Write one byte; blocks until complete |

:::{warning}
EEPROM write cycles are rated at 100,000 minimum. Avoid writing in tight loops.
Each write blocks for approximately 3.4 ms (EEPROM busy-wait).
:::

---

### `avr.SoftSPI`

Bit-bang SPI using arbitrary GPIO pins. Use when the hardware SPI pins (D11/D12/D13) are
occupied or when multiple SPI devices with separate CS lines are needed.

```python
from machine import Pin
from avr import SoftSPI
from pymcu.types import uint8

sck  = Pin(13, Pin.OUT)
mosi = Pin(11, Pin.OUT)
miso = Pin(12, Pin.IN)
cs   = Pin(10, Pin.OUT)

spi = SoftSPI(sck, mosi, miso, baudrate=500)   # 500 kHz

cs.low()
spi.select()
received: uint8 = spi.transfer(0x9F)   # full-duplex byte
spi.write(0x00)                         # send, discard MISO
spi.deselect()
cs.high()
```

| Constant | Value | Description |
|---|---|---|
| `SoftSPI.CONTROLLER` | 0 | Master mode (drives SCK) |
| `SoftSPI.PERIPHERAL` | 1 | Slave mode |

| Method | Signature | Description |
|---|---|---|
| `transfer(data)` | `(data: uint8) -> uint8` | Full-duplex byte exchange |
| `write(data)` | `(data: uint8)` | Send byte, discard received |
| `select()` | `()` | Assert CS low |
| `deselect()` | `()` | Release CS high |

---

### `avr.SoftI2C`

Bit-bang I2C using arbitrary GPIO pins. Requires external 4.7 kΩ pull-up resistors on
both SDA and SCL lines.

```python
from machine import Pin
from avr import SoftI2C
from pymcu.types import uint8

scl = Pin(5, Pin.OUT)
sda = Pin(4, Pin.OUT)

i2c = SoftI2C(scl, sda, freq=100000)    # 100 kHz standard mode

# Scan for devices
count: uint8 = i2c.scan()   # returns number of responding addresses (not a list)

# Probe a specific address
if i2c.ping(0x3C):           # SSD1306 OLED at 0x3C
    i2c.writeto(0x3C, 0x00)  # write command byte
    val: uint8 = i2c.readfrom(0x3C)
```

| Method | Signature | Description |
|---|---|---|
| `scan()` | `() -> uint8` | Count of responding I2C devices |
| `ping(addr)` | `(addr: uint8) -> uint8` | Returns 1 if device ACKs |
| `writeto(addr, data)` | `(addr: uint8, data: uint8) -> uint8` | Write one byte |
| `readfrom(addr)` | `(addr: uint8) -> uint8` | Read one byte |

:::{note}
`scan()` returns a count rather than a list — returning a `list[uint8]` would
pull in GC infrastructure for a result most programs discard. Use `ping(addr)` to
probe a known address directly.

`freq` is converted to a bit-bang half-period: `half_us = 500_000 // freq`.
At 100 kHz this gives 5 µs half-period; at 400 kHz ("fast mode"), 1 µs.
:::

---

## Porting guide

### Add type annotations to every variable

```python
count = 0          # MicroPython — runtime type inference
count: int = 0     # PyMCU — static annotation required
```

### Replace `float` sleep with integer milliseconds

```python
utime.sleep(0.5)       # MicroPython  — float seconds
utime.sleep_ms(500)    # PyMCU        — integer milliseconds
```

### Replace dynamic `bytearray` with fixed-size array

```python
buf = bytearray(8)                    # MicroPython — heap allocation
buf: uint8[8] = [0,0,0,0,0,0,0,0]   # PyMCU — SRAM fixed array
```

### Replace `machine.mem8` with typed `ptr` (optional but safer)

`machine.mem8[addr]` works in PyMCU, but the {ref}`ptr[T] type <language-type-system>`
is preferred for memory-mapped I/O — it is compile-time checked and documents the intent
of each register access.

```python
machine.mem8[0x25] = 0xFF          # works in PyMCU — raw mem access
from pymcu.types import ptr, uint8
PORTB: ptr[uint8] = ptr(0x25)      # typed, compile-time checked
PORTB.value = 0xFF
```

### Replace `Timer` callback lambdas with named functions

```python
# MicroPython — lambdas work here
tim = Timer(period=100, mode=Timer.PERIODIC, callback=lambda t: led.toggle())

# PyMCU — named function required (no lambda support)
def on_tick():
    led.toggle()

tim = Timer(1, period=100, callback=on_tick)   # identical API otherwise
```

:::{note}
Lambda expressions are not supported in PyMCU. Use a named function instead.
The `Timer(period=ms, callback=fn)` syntax is otherwise identical to MicroPython.
:::

---

## Differences from real MicroPython

These are the **actual gaps and trade-offs** — things that work differently or are
unavailable. Anything not listed here behaves identically to standard MicroPython.

| Feature | MicroPython | PyMCU |
|---|---|---|
| Execution model | Bytecode interpreter | **Native compiled — zero runtime overhead** |
| RAM overhead | ~10–40 KB for the VM | ~0 bytes (static dispatch, no GC) |
| `float` arithmetic | Full hardware/soft-float | Soft-float (~200–400 cycles per op) |
| `f"..."` format strings | Runtime evaluation | Compile-time constants only |
| `try / except / raise` | Supported (heap-based) | ✅ Supported — zero-cost T-flag error ABI, no heap |
| `bytearray` | Dynamic heap allocation | Fixed-size `uint8[N]` only |
| `UART.any()` | Number of bytes available | Returns `1` (non-zero) or `0` — not an exact count |
| `UART.read()` | Optional `nbytes` parameter | Single-byte blocking read only; use `readinto(buf, n)` for multi-byte |
| `UART.readline()` | Returns `bytes` object | `readline(buf, max_len)` — caller provides buffer, returns count |
| `UART.readinto(buf)` | Fills up to `len(buf)` | `readinto(buf, nbytes)` — explicit count required (fixed-size arrays have no runtime `len`) |
| `I2C.scan()` | Returns list of addresses | `scan()` returns count only; `scan(buf, max_count)` fills caller-provided buffer and returns count |
| `I2C.writeto(addr, buf)` | `buf` length inferred from object | `writeto(addr, buf, n)` — explicit byte count required (fixed arrays have no runtime `len`) |
| `I2C.readfrom(addr, nbytes)` | Returns `bytes` object (GC-allocated) | `readfrom_into(addr, buf, n)` — caller provides buffer; returns 1 on success, 0 on NACK |
| `SPI.write(buf)` | `buf` length inferred from object | `write(buf, n)` — explicit byte count required |
| `SPI.readinto(buf, write=0x00)` | Fills up to `len(buf)`; default write=0x00 | `readinto(buf, n, write_byte=0xFF)` — explicit count; default dummy is 0xFF |
| `SPI.write_readinto(write_buf, read_buf)` | Buffer lengths inferred | `write_readinto(write_buf, read_buf, n)` — explicit byte count required |
| `Timer.irq(handler)` | `handler(timer)` receives Timer | ✅ Supported — ZCA synthesis passes Timer instance |
| `Timer.init(freq=...)` | Hz-based config | ✅ Supported — auto-selects prescaler for 1 Hz – MHz range |
| `Pin(id, mode, pull)` | `pull` parameter | ✅ Supported — `Pin.PULL_UP` enables AVR pull-up resistor |
| `Pin("PB5", mode)` | String pin name | ✅ Supported — bypasses Arduino integer mapping |
| Lambda expressions | Supported | ❌ Not available — use named functions |
| Target hardware | STM32, RP2040, ESP32, … | ATmega328P (Arduino Uno / Nano) |

