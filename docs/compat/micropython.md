# MicroPython Compatibility Layer

The `pymcu-micropython` package lets you write firmware using the familiar MicroPython
APIs — `machine`, `utime`, `micropython`, and the AVR-specific `avr` port module — and
compile it directly to native AVR machine code with **zero runtime overhead**.

:::{important} Compiled, not interpreted
There is no MicroPython interpreter on the device. Every `machine.*` or `avr.*` call is a
compile-time shim that expands directly to HAL instructions. The MCU receives only the
resulting machine code — typically a few hundred bytes of flash, 0 bytes SRAM overhead.
:::

PyMCU targets the **ATmega328P** (Arduino Uno / Nano / Pro Mini). The pin numbering in
`machine.Pin` follows the Arduino integer convention (D0–D13, A0–A5). Port strings such as
`"PB5"` are also accepted directly.

---

## Quick start

```bash
pip install pymcu pymcu-micropython
```

```toml
# pyproject.toml
[tool.pymcu]
stdlib  = ["micropython"]
board   = "arduino_uno"
chip    = "atmega328p"
f_cpu   = 16000000
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
| `machine.UART` | `write`, `read`, `write_str`, `println`, `print_byte` | ✅ Complete |
| `machine.ADC` | `read` (10-bit), `read_u16` (16-bit scaled) | ✅ Complete |
| `machine.PWM` | `freq`, `duty_u16`, `duty`, `init`, `deinit` | ✅ Complete |
| `machine.SPI` | `write`, `read`, `write_readinto` | ✅ Complete |
| `machine.I2C` | `scan`, `writeto`, `readfrom` | ✅ Complete |
| `machine.Timer` | `__init__`, `init`, `deinit`, `start`, `irq` | ✅ Complete |
| `machine.WDT` | `__init__`, `feed` | ✅ Complete |
| `machine.Signal` | `on`, `off`, `value` | ✅ Complete |
| `machine.mem8` / `machine.mem16` | `[]` get/set | ✅ Complete |
| `machine.freq()` | Returns CPU Hz | ✅ Complete |
| `machine.idle/lightsleep/deepsleep` | Sleep modes | ✅ Complete |
| `machine.disable_irq/enable_irq` | IRQ control | ✅ Complete |
| `machine.time_pulse_us` | Pulse measurement | ✅ Complete |
| `utime` | `sleep_ms`, `sleep_us`, `sleep`, `ticks_ms`, `ticks_diff` | ✅ Complete |
| `micropython` | `const`, `native` (stub), `viper` (stub) | ✅ Complete |
| `avr.EEPROM` | `read`, `write` | ✅ Complete |
| `avr.SoftSPI` | `transfer`, `write`, `select`, `deselect` | ✅ Complete |
| `avr.SoftI2C` | `scan`, `writeto`, `readfrom`, `ping` | ✅ Complete |
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

Hardware interrupt configuration via `Pin.irq()` sets the trigger mode in the `EICRA`/`EIMSK`
hardware registers. The ISR itself must be declared with `@interrupt`:

```python
from machine import Pin
from pymcu.types import interrupt

count: int = 0

@interrupt(0x0002)           # INT0 vector — Arduino D2
def on_press():
    global count
    count += 1

def main():
    btn = Pin(2, Pin.IN, Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)  # configures EICRA/EIMSK only
    while True:
        pass
```

---

### `machine.UART`

The ATmega328P has one hardware UART (USART0). `id=0` is the only valid value.

```python
from machine import UART
from pymcu.types import uint8

uart = UART(0, 9600)           # id=0 → USART0; baud rate set at compile time
uart.write(65)                 # send single byte  (ASCII 'A')
uart.write_str("hello\n")      # send string literal from PROGMEM
uart.println("ready")          # write_str + newline

b: uint8 = uart.read()         # blocking read — waits for RXC flag
uart.print_byte(42)            # sends "42\n" as decimal ASCII digits
```

| Method | Description |
|---|---|
| `write(byte)` | Send a single byte |
| `write_str(s)` | Send a compile-time string constant |
| `println(s)` | Send string + `\r\n` |
| `read()` | Blocking read — spins until RXC is set |
| `print_byte(n)` | Print uint8 as decimal ASCII |

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

Hardware SPI on the ATmega328P uses fixed pins: D13 (SCK), D11 (MOSI), D12 (MISO).
The chip-select pin must be managed manually — this matches standard MicroPython behaviour.

```python
from machine import SPI, Pin

spi = SPI()
cs  = Pin(10, Pin.OUT)

cs.low()
spi.write(0x9F)                 # send command byte
device_id = spi.read(0xFF)      # send dummy 0xFF, return MISO byte
cs.high()
```

| Method | Description |
|---|---|
| `write(byte)` | Send one byte (discard MISO) |
| `read(write_byte)` | Send `write_byte`, return received byte |
| `write_readinto(out, in_val)` | Full-duplex single-byte transfer |

For bit-bang SPI with arbitrary GPIO pins, use `avr.SoftSPI` (see below).

---

### `machine.I2C`

Hardware I2C (TWI) uses fixed pins: A4 (SDA = PC4) and A5 (SCL = PC5).

```python
from machine import I2C

i2c = I2C()
count = i2c.scan()          # returns number of responding devices (not a list)
i2c.writeto(0x3C, 0x00)     # write single byte to address 0x3C
val = i2c.readfrom(0x3C)    # read single byte from 0x3C
```

:::{note}
`scan()` returns a device *count* rather than a list of addresses — the MCU has no heap
for dynamic lists. Use `pymcu.hal.i2c.I2C` for `ping(addr)` to probe specific addresses.
:::

---

### `machine.Timer`

```python
from machine import Timer
from pymcu.types import uint8, interrupt

ticks: uint8 = 0

@interrupt(0x001A)    # TIMER1_COMPA vector address
def on_tick():
    global ticks
    ticks += 1

def main():
    t = Timer(1, prescaler=64)    # Timer1, /64 prescaler → ~1 kHz tick at 16 MHz
    t.irq(on_tick, Timer.IRQ_COMPA)
    t.start()
    while True:
        pass
```

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

```python
from machine import WDT

wdt = WDT(timeout=2000)    # 2-second watchdog window
while True:
    wdt.feed()              # must be called within 2 s or MCU resets
    do_work()
```

The `timeout` value is in milliseconds. Internally it maps to the nearest ATmega328P
watchdog prescaler value (16 ms to 8 s in binary steps).

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
| `0x29` | `PINC` | Port C input pins |
| `0x2A` | `DDRC` | Port C direction |
| `0x2B` | `PORTC` | Port C output |
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
from machine import disable_irq, enable_irq, idle, lightsleep, deepsleep

# Atomic / critical section
state = disable_irq()   # CLI instruction — disables global interrupts
# ... atomic operation ...
enable_irq(state)       # SEI instruction — restores interrupts

# Sleep modes (wake on any enabled interrupt)
idle()           # Idle: CPU halted, all peripherals running (~70% power reduction)
lightsleep()     # Power-save: async timer kept alive; I/O and Timer2 active
deepsleep()      # Power-down: only INT0/INT1 or WDT can wake (~99% reduction)
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

The value is a compile-time constant derived from `f_cpu` in `pyproject.toml`.

---

### `utime`

```python
from utime import sleep_ms, sleep_us, sleep, ticks_ms, ticks_diff
from pymcu.types import uint32

sleep_ms(500)       # busy-wait 500 ms  (uses _delay_ms loop)
sleep_us(100)       # busy-wait 100 µs
sleep(1)            # 1 second (integer only — no float on AVR)

t0: uint32 = ticks_ms()
sleep_ms(200)
elapsed: uint32 = ticks_diff(ticks_ms(), t0)   # elapsed ≈ 200
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
| `ticks_ms()` | Milliseconds since boot — Timer0 counter |
| `ticks_diff(a, b)` | `a - b` with uint32 wrap-around |

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
`scan()` returns a count rather than a list. The MCU has no heap for dynamic data
structures. Use `ping(addr)` to probe a known address directly.

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

```python
machine.mem8[0x25] = 0xFF          # works in PyMCU — raw mem access
from pymcu.types import ptr, uint8
PORTB: ptr[uint8] = ptr(0x25)      # typed, compile-time checked
PORTB.value = 0xFF
```

### Replace `Timer` callback lambdas with `@interrupt`

```python
# MicroPython
tim = Timer(period=100, mode=Timer.PERIODIC, callback=lambda t: led.toggle())

# PyMCU — use @interrupt + irq() method
@interrupt(0x001A)    # TIMER1_COMPA vector
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
| Execution model | Bytecode interpreter (~256 KB flash) | **Native compiler — zero runtime** |
| RAM overhead | ~10–40 KB | ~0 bytes (ZCA, compile-time expansion) |
| `Pin.irq(handler=cb)` | Supported | Hardware config only — use `@interrupt` for ISR |
| `Timer(period=ms, callback=cb)` | Supported | Use `Timer.irq(fn, trigger)` + `Timer.start()` |
| `ticks_ms()` | Hardware free-running counter | Timer0 counter via auto-injected `millis_init()` |
| `float` arithmetic | Full support | Soft-float (~200–400 cycles per op) |
| `f"..."` runtime format | Supported | Compile-time string constants only |
| `try / except` | Supported | Not available — use sentinel return values |
| `bytearray` | Dynamic heap | Fixed-size `uint8[N]` SRAM arrays |
| `I2C.scan()` | Returns list of addresses | Returns count (no heap) |
| `machine.mem8[addr]` | Supported | ✅ Supported (shim over `ptr`) |
| `avr.EEPROM` | Not in MicroPython core | ✅ AVR-specific extension |
| `avr.SoftSPI` / `avr.SoftI2C` | Not in MicroPython core | ✅ AVR-specific extension |
| Target hardware | STM32, RP2040, ESP32, … | ATmega328P (Arduino Uno) |

