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
pip install pymcu-compiler pymcu-circuitpython
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
import time
from digitalio import DigitalInOut, Direction

led = DigitalInOut(board.LED)
led.direction = Direction.OUTPUT
while True:
    led.value = True
    time.sleep(0.5)
    led.value = False
    time.sleep(0.5)
```

```bash
pymcu build
```

---

## Supported modules

| Module | API surface | Status |
|---|---|---|
| `board` | Per board, not one fixed set. The four Arduino boards define the full list: `D0`–`D13`, `A0`–`A5`, `LED`, `LED_BUILTIN`, `TX`, `RX`, `SDA`, `SCL`, `SCK`, `MOSI`, `MISO`, `SS`. The ATtiny dev boards (Digispark, Adafruit Trinket) use their own silk-screen numbering plus `LED`; the Pico uses `GP0`–`GP28` plus `LED`; the **bare ATtiny chips define no `LED` at all**, because the part has none. | ⚠️ Varies by board |
| `digitalio` | `DigitalInOut`, `Direction`, `Pull`, `DriveMode` | ✅ Complete |
| `analogio` | `AnalogIn`, `AnalogOut` | ✅ Complete |
| `busio` | `UART`, `I2C`, `SPI` | ✅ Complete |
| `pwmio` | `PWMOut` | ✅ Complete |
| `adafruit_motor.servo` | `Servo`, `ContinuousServo` | ✅ On D9/D10, where the frequency is exact. Import the submodule's members by name |
| `pulseio` | `PulseIn`, `PulseOut` | ✅ On the ATmega 48/88/168/328 family. `PulseOut.send()` takes the length as a second argument, and the carrier pin is fixed by the timer channel |
| `bitbangio` | `I2C`, `SPI` | ✅ The same API as `busio`, driven in software on any pins. SPI is mode 0 only |
| `countio` | `Counter`, `Edge` | ✅ A pin interrupt and a 32-bit count. One per program; telling a rising edge from a falling one needs D2 or D3 |
| `keypad` | `Keys`, `Event` | ⚠️ Takes a list of `digitalio.DigitalInOut`, not pin names. `KeyMatrix` is not written |
| `rainbowio` | `colorwheel` | ✅ Complete |
| `rotaryio` | `IncrementalEncoder` | ✅ On the ATmega 48/88/168/328 family. Both lines on one port; `divisor` is fixed when the encoder is built |
| `neopixel_write` | — | ❌ Not implemented as a module; the WS2812 timing lives in the `pymcu-lib-neopixel` library |
| `neopixel` | `NeoPixel` | ✅ Complete — ships in the `pymcu-lib-neopixel` library, pulled in as a dependency, so `import neopixel` works unchanged |
| `time` | `sleep`, `monotonic`, `monotonic_ns` | ✅ `sleep()` takes any duration, from microseconds to minutes; it used to wrap past 65.535 s and to round anything under a millisecond to zero. `monotonic_ns()` wraps at 4.295 s and says so. `sleep_ms()` / `sleep_us()` also compile, but they are **PyMCU extensions**: upstream `time` defines no such names |
| `supervisor` | `ticks_ms`, `ticks_add`, `ticks_diff`, `reload`, `runtime` | ✅ Complete |
| `alarm` | `time.TimeAlarm`, `pin.PinAlarm`, `sleep_until_alarms`, `light_sleep_until_alarms`, `exit_and_deep_sleep_until_alarms`, `wake_alarm` | ✅ Complete |
| `microcontroller` | `cpu.frequency`, `cpu.voltage`, `cpu.uid`, `cpu.reset_reason`, `nvm`, `watchdog`, `reset`, `delay_us` | ✅ Partial |

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
val: uint16 = adc.value               # 0–65535; full scale on the pin reads 65535
vref: float = adc.reference_voltage   # the reference the converter measures against
volts: float = val * vref / 65535.0
```

`value` covers the whole 16-bit range: the 10-bit reading is scaled by replicating its top
bits into the bottom ones, so 1023 counts map to exactly 65535. It used to be multiplied by
64, which stopped at 65472 and left every volts calculation low.

`reference_voltage` comes from the HAL, which knows what each part's converter is wired to
— the supply rail on the AVR and PIC parts, 3.3 V on the RP parts. It is a compile-time
constant on every target, so the arithmetic above folds.

A pin with no ADC channel behind it (`AnalogIn(board.D2)`) is refused where it is written.
It used to build clean and read A0 forever.

`AnalogOut` needs a digital-to-analog converter and the ATmega328P has none, so
constructing one is refused at build time with a message that names `pwmio.PWMOut` as the
way to get an analog-like output. It used to build with a warning and compile `.value = ...`
to nothing.

---

### `busio.UART`

```python
import board
import busio
from pymcu.types import uint16

uart = busio.UART(board.TX, board.RX, baudrate=9600, timeout=500)
uart.write(b"READY\r\n")

buf = bytearray(8)
n: uint16 = uart.readinto(buf)      # how many bytes actually arrived
```

Every parameter reaches the hardware. `bits`, `parity` and `stop` are the frame format:
parity is `None`, `busio.Parity.EVEN` or `busio.Parity.ODD`, and a frame the part cannot send
is refused where the UART is constructed. They used to be accepted and dropped, so a program
that asked for 7E1 ran 8N1 and said nothing.

`timeout` is in milliseconds here, not the float seconds CircuitPython uses, because a
parameter default cannot be a float in this compiler. `readinto` honours it and returns how
many bytes it got; it used to block on every byte for ever whatever the timeout said, so a
sensor that stopped answering hung the program.

`receiver_buffer_size` turns on the interrupt-driven receive ring in the HAL, which is what
makes `in_waiting` a count instead of a flag. It defaults to 64, the size of the ring; asking
for more is refused with the ring's size named. Pass `1` for the polled UART, which holds one
byte in the hardware register and costs no interrupt.

`read()` and `readline()` return a `bytes` object and there is no heap to build one on, so
both are refused at build time with a message naming `readinto(buf)`. They used to compile to
nothing and hand back a value that was never read.

The `tx`/`rx` pin arguments are accepted for API compatibility; the hardware pins on the
ATmega328P are fixed (PD1/PD0).

| Member | Behaviour |
|---|---|
| `write(buf) -> int` | Write every byte of a literal or an array; returns the count |
| `readinto(buf) -> int` | Fill `buf` or stop at the timeout; returns what it got |
| `in_waiting` | A count when buffered; 0 or 1 when polled, because the hardware has no count |
| `baudrate`, `timeout` | The values in force; `timeout` is settable |
| `reset_input_buffer()` | Drop everything waiting |
| `read()`, `readline()` | Refused at build time, naming `readinto` |

---

### `busio.I2C`

```python
import board, busio

i2c = busio.I2C(board.SCL, board.SDA, frequency=400000)

while not i2c.try_lock():
    pass
i2c.writeto(0x68, bytes([0x6B, 0x00]))
data = bytearray(6)
i2c.writeto_then_readfrom(0x68, bytes([0x3B]), data)
i2c.unlock()
```

`frequency` reaches the bit-rate register. It used to be dropped and the bus ran at 100 kHz
whatever the program asked for. A rate the TWI cannot clock (above about 444 kHz or below
about 30.5 kHz at 16 MHz) is refused where the bus is constructed, with the reachable range
named. `i2c.frequency` reports what the bus actually clocks, which is not always the request:
the bit-rate register is an integer.

`start` and `end` slice the buffer on every transfer method, as they do in CircuitPython.
They used to be accepted and the whole buffer sent, so a program writing one register out of a
packet wrote the packet.

`scan()` returns a list of the addresses that answered and there is no heap to build one on,
so it is refused at build time with a message naming `probe(address)`:

```python
for a in range(8, 120):
    if i2c.probe(a):
        print(hex(a))
```

It used to compile to nothing, so the scan found nothing and said nothing.

| Method | Behaviour |
|---|---|
| `probe(addr) -> int` | 1 if a device acknowledges at `addr` |
| `writeto(addr, buf, start, end)` | Write the slice |
| `readfrom_into(addr, buf, start, end)` | Read into the slice, NACK on the last byte |
| `writeto_then_readfrom(addr, out, in, ...)` | Repeated-start write then read |
| `frequency` | The SCL rate actually clocked |
| `try_lock()` / `unlock()` | Bus locking (single controller on AVR) |
| `scan()` | Refused at build time, naming `probe` |

:::{note}
Hardware I2C on the ATmega328P uses fixed pins: SCL = PC5 (A5), SDA = PC4 (A4).
The `scl`/`sda` arguments are accepted for API compatibility.
:::

---

### `busio.SPI`

```python
import board, busio, digitalio

spi = busio.SPI(board.SCK, MOSI=board.MOSI, MISO=board.MISO)
cs = digitalio.DigitalInOut(board.D10)
cs.switch_to_output(value=True)

out_buf = bytearray(b"\xAB\x00")
in_buf  = bytearray(2)

while not spi.try_lock():
    pass
spi.configure(baudrate=1000000, polarity=1, phase=1)   # mode 3 at 1 MHz
cs.value = False
spi.write_readinto(out_buf, in_buf)
cs.value = True
spi.unlock()
```

`configure()` programs the clock and the mode. It used to record `baudrate` and reprogram
nothing, so a display asked for mode 3 at 8 MHz ran mode 0 at 4 MHz. `spi.frequency` reports
the rate the bus actually runs at: the dividers are powers of two and the chosen one never
exceeds the request, so asking for 3 MHz on a 16 MHz part gets 2 MHz.

`bits` other than 8 is refused; the hardware shifts 8 bits per frame and has no other size.

`write_readinto` requires the two slices to be the same length, which SPI does: one byte is
clocked in for every byte clocked out. It used to index the read buffer with the write
buffer's index and check nothing, so a shorter read buffer was written past its end.

| Method | Behaviour |
|---|---|
| `write(buf, start, end)` | Clock the slice out, discarding what comes back |
| `readinto(buf, start, end, write_value)` | Clock `write_value` out, keep what comes back |
| `write_readinto(out, in, ...)` | Full duplex; the two slices must be the same length |
| `configure(baudrate, polarity, phase, bits)` | Programs the clock and the mode |
| `frequency` | The bit rate actually clocked |
| `try_lock()` / `unlock()` | Bus locking (always succeeds on bare metal) |

:::{note}
Hardware SPI on the ATmega328P uses fixed pins: SCK = PB5, MOSI = PB3, MISO = PB4.
Chip-select is the caller's, through a `digitalio.DigitalInOut`, exactly as in CircuitPython.
:::

---

### `pulseio.PulseIn`, `pulseio.PulseOut`

```python
import board, pulseio
from pymcu.types import uint8, uint16

pulses = pulseio.PulseIn(board.D2, maxlen=70)
while len(pulses) < 67:
    pass
leader: uint16 = pulses[0]          # microseconds
pulses.clear()

frame: uint16[4] = [560, 560, 1690, 560]
out = pulseio.PulseOut(board.D3, frequency=38000, duty_cycle=32768)
out.send(frame, 4)
```

`PulseIn` measures the pulses arriving on a pin, `PulseOut` sends a gated carrier: between
them they are how an infrared remote, an HC-SR04 rangefinder and a DHT sensor are read and
driven. Neither existed.

**`PulseIn`.** `maxlen` is how many pulses to hold. The buffer is a fixed array of 128 in the
HAL, allocated at compile time, so a larger `maxlen` is refused where it is written rather
than quietly given less; a pulse that arrives with the buffer full is dropped. `idle_state`
decides which edge starts the first pulse, and the capture takes that from the line itself:
the first edge after a clear only sets the reference, so the first stored interval follows
the line leaving rest.

One `PulseIn` per program on the AVR: the buffer and the edge timestamp are module state in
the HAL, so a second one would share them.

`resume(trigger_duration=...)` sends a pulse on the pin before recording again, to trigger a
sensor that answers on the same line. The pin is an input while it is being measured and this
HAL does not turn it round, so a non-zero duration is refused rather than dropped: drive the
trigger with a `digitalio.DigitalInOut` on the pin first.

**`PulseOut`.** The carrier comes out of one timer channel, which is `board.D3` on the AVR;
any other pin is refused where the `PulseOut` is written. 38 kHz comes out as 38 462 Hz, 1.2 %
high and inside any receiver's band-pass. The gaps are timed against a running counter rather
than counted out in delay calls: measured, 560 µs holds the carrier for 561.5 µs and 1690 µs
for 1692 µs.

`send()` takes the sequence and derives the count from it, the CircuitPython spelling. A
module-level constant list keeps its length and its elements across the parameter chain
(PyMCU#258): `len(pulses)` answers the element count and a run-time `pulses[i]` reads a
materialised flash table.

Both halves claim their timer, so a PWM or a servo that would reprogram it out from under
them is refused where it is written rather than silently changing every measurement.

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

`frequency` reports what the pin emits, not what was asked for. On a timer whose period is
fixed at 256 counts the frequencies on offer are a handful of buckets, so
`PWMOut(board.D6, frequency=5000)` emits 7812 Hz and used to say 5000.

`board.D9` and `board.D10` are the exception: asking either for a frequency that is not one
of those buckets reaches the timer mode whose period is a register, and it comes out exactly.
`PWMOut(board.D9, frequency=50)` really is 50 Hz, with 40 000 steps of duty across the
period instead of 256. It used to run at 61 Hz. A PWM on that path cannot be retuned at run
time and says so.

The first parameter is `pin`, as CircuitPython names it. It was `pin_name`, so
`PWMOut(pin=board.D9, ...)` did not compile.

---

### `bitbangio`

```python
import board, bitbangio

i2c = bitbangio.I2C(board.D2, board.D3, frequency=100000)
i2c.writeto(0x68, b"\xA5")

spi = bitbangio.SPI(board.D5, MOSI=board.D6, MISO=board.D7)
spi.configure(baudrate=250000)
spi.write(b"\x9F")
```

The same buses as `busio`, driven in software on any pins, which is what a board with two
sensors at the same address needs: an ATmega has one hardware TWI and one SPI. The API is
`busio`'s method for method, so a driver written against `busio.I2C` takes a `bitbangio.I2C`
without knowing.

Both I2C lines need external pull-ups: the bus is open-drain and neither pin is ever driven
high, only released. Measured on an Arduino Uno, 100 kHz asked comes out at about 85 kHz,
because the bit loop costs time on top of the half-period; `frequency` reports the rate the
half-period gives rather than the request.

SPI is mode 0 only, and `configure()` refuses any other polarity or phase naming `busio.SPI`,
which is the hardware peripheral and takes all four.

---

### `countio`

```python
import board, countio

flow = countio.Counter(board.D2, edge=countio.Edge.FALL)
while True:
    print(flow.count)
    flow.reset()
```

A pin interrupt and a 32-bit counter, not a hardware counter: every timer on the ATmega328P
is already spoken for, and a timer's external clock input is D4 or D5 and nothing else, while
an interrupt counts on any of 23 pins. About 30 cycles an edge.

Rising and falling are told apart by **D2 and D3** only. Every other pin has a pin-change
interrupt that fires on both and cannot say which, so asking one of those for a single edge
is refused rather than counted twice and quietly doubled.

`counter.count = 0` clears it, as CircuitPython allows; any other value is refused, because a
counter counts edges as they arrive and there is nowhere to start it from.

One `Counter` per program: the counter and the interrupt are module state in the HAL.

---

### `rotaryio`

```python
import board, rotaryio

knob = rotaryio.IncrementalEncoder(board.D2, board.D3)
last = knob.position
while True:
    now = knob.position
    if now != last:
        print(now)
        last = now
```

Two lines a quarter turn out of phase, an interrupt on each, and three lines of arithmetic:
which line changed first says which way the knob went. About 40 cycles an edge, so a
hand-turned knob costs nothing and an encoder fast enough to matter would swamp the part.

A common panel knob makes four line changes per click of detent, which is why `divisor` is 4
by default and why one click moves `position` by one. A knob with a detent every other change
takes `divisor=2` and a continuous one takes `divisor=1`. The division rounds towards zero, so
one click back from where the program started reads `-1`. The divisor is fixed when the
encoder is built, because the position is divided by it with a shift and a shift needs its
count known; assigning to `encoder.divisor` afterwards is refused and names the constructor.

**Both lines have to be on one port**: both among D0 to D7, or both among D8 to D13, or both
among A0 to A5. The handler that decodes them reads one port register, and a register address
is fixed when the firmware is built, so it cannot read a second port the program chose. A pair
on two ports is refused with the three groups named.

D2 and D3 are the pair to reach for. They are INT0 and INT1, they have a vector each, and they
are what every encoder guide wires a knob to.

Both lines get their pull-ups, and the decoder primes itself from them rather than starting at
zero: a knob idles with both lines released, so starting at zero made the very first edge look
like a step that never happened.

One `IncrementalEncoder` per program: the position and the interrupts are module state in the
HAL.

---

### `keypad`

```python
import board, digitalio, keypad

buttons = [digitalio.DigitalInOut(board.D4),
           digitalio.DigitalInOut(board.D5)]
for b in buttons:
    b.switch_to_input(pull=digitalio.Pull.UP)

keys = keypad.Keys(buttons, value_when_pressed=False)
event = keypad.Event()
while True:
    if keys.events.get_into(event):
        print(event.key_number, event.pressed)
```

`Keys` takes a list of `digitalio.DigitalInOut` the caller has already made inputs, not a
list of pin names: a list of names has no storage behind it and cannot be indexed at run
time.

**The queue holds no events of its own.** A key's stored state moves only when its change is
reported, so what is waiting to be read is exactly the set of keys whose pins disagree with
it. That costs one bit a key instead of a buffer, and it is why `overflowed` is always false
and `max_events` is refused: there is nothing to size.

CircuitPython scans in the background on a tick; there is none here, so the scan happens
inside `events.get_into()` and `interval` is refused for the same reason.

`events.get()` returns a new `Event` and there is no heap; it is refused naming `get_into`,
which is upstream's own allocation-free call. `keypad.KeyMatrix` is not written.

---

### `rainbowio`

```python
from rainbowio import colorwheel

pixels[i] = colorwheel(pos)
```

Red, green, blue and back to red across 0 to 255. Between the corners one channel falls by 3
a step while the next rises by 3, so two channels are lit at a time and they always add
to 255.

---

### The servo idiom

```python
import board, pwmio
from adafruit_motor.servo import Servo

pwm = pwmio.PWMOut(board.D9, frequency=50)
s = Servo(pwm, min_pulse=1000, max_pulse=2000)
s.angle = 90                      # a 1500 us pulse, measured
```

Put the servo on `board.D9` or `board.D10`. Those two reach the exact-frequency path, so the
period is 20 ms and one count is 0.5 us: a servo's travel has about 2000 steps. On any other
pin 50 Hz becomes 61 Hz, the period has 256 counts of 64 us, and the servo still moves in
about 16 steps with its timing 22 % fast.

`from adafruit_motor import servo`, which is what every guide writes, does not compile: a
submodule cannot be imported by name (PyMCU#323). Write
`from adafruit_motor.servo import Servo`.

`fraction` and `ContinuousServo.throttle` take whole numbers rather than floats -- 0 to 65535
and -32768 to 32767 -- because a parameter default cannot be a float here and soft-float on
this part costs hundreds of cycles an operation. `angle` is a whole number of degrees.

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

time.sleep(0.5)            # fractional seconds — folded to delay_ms(500)

# PyMCU extensions, NOT part of CircuitPython. Portable code should not use them:
time.sleep_ms(500)         # 500 ms
time.sleep_us(100)         # 100 us

from pymcu.types import uint32
t: float = time.monotonic()       # seconds since boot (float, as in CircuitPython)
ns: uint32 = time.monotonic_ns()  # nanoseconds since boot (wraps ~71 min)
```

:::{note}
`time.sleep(s)` takes the CircuitPython float, and the multiplication folds at compile time
— `sleep(0.5)` becomes `delay_ms(500)` with no soft-float in the firmware. A *runtime* float
argument does link the soft-float runtime, so prefer a literal in a hot path (or the
non-portable `sleep_ms()`, accepting that the result no longer runs under CircuitPython).

`time.monotonic()` returns a `float` like the real thing (`millis() / 1000.0`), which does
pull in soft-float; the compiler warns once. Use `supervisor.ticks_ms()` for integer
milliseconds when you do not need the float.
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
`ticks_add()` and `ticks_diff()` handle 32-bit wrap-around correctly. A Timer0 overflow is
1024 µs rather than 1000 µs, and the ISR carries the Arduino-style fractional correction, so
this counter — and `time.monotonic()` above it — measures real milliseconds instead of
running 2.4% slow.

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
from pymcu.types import uint8, uint32

freq: uint32 = microcontroller.cpu.frequency   # compile-time constant (e.g. 16000000)
vcc:  uint8  = microcontroller.cpu.voltage     # always 5 on 5 V AVR boards
microcontroller.delay_us(50)                   # busy-wait 50 µs
microcontroller.reset()                        # watchdog reset
```

`cpu.temperature` and `cpu.uid` are accepted for API compatibility but not
functionally implemented on ATmega328P (no factory temperature sensor or UID).

`cpu.reset_reason` reports what brought the chip up (`ResetReason.POWER_ON`,
`BROWNOUT`, `WATCHDOG`, `RESET_PIN`). It reads `MCUSR` live and PyMCU does not snapshot or
clear it at boot, so flags accumulate across resets — clear `MCUSR` early if you need a
single-event reading.

#### `microcontroller.nvm`

Byte-addressable persistent storage, backed by the on-chip EEPROM. Every accessor expands
inline to the EEPROM HAL:

```python
import microcontroller
from pymcu.types import uint8, uint16

size: uint16 = len(microcontroller.nvm)     # 1024 on ATmega328P
microcontroller.nvm[0] = 42                 # one byte
b: uint8 = microcontroller.nvm[0]

# Slice assignment — the canonical CircuitPython pattern, compiled to byte writes
microcontroller.nvm[0:4] = b"\xcc\x10\xca\xfe"

print(microcontroller.nvm[0:4])             # bytearray(b'\xcc\x10\xca\xfe')
```

Slice *assignment* goes through `__setitem__`, one write per byte, with the length checked
at compile time — so the source and the destination range must match. Binding a slice to a
name (`buf = microcontroller.nvm[0:4]`) is still not available: the result would need a
heap-allocated `bytearray`. Read it back a byte at a time, or `print()` it directly as
above.

#### `microcontroller.watchdog`

```python
import microcontroller
from microcontroller import WatchDogMode

microcontroller.watchdog.timeout = 2.0                  # seconds (soft-float)
microcontroller.watchdog.mode = WatchDogMode.RESET      # arms it
microcontroller.watchdog.feed()
microcontroller.watchdog.deinit()                       # disable
```

AVR only implements reset mode: `WatchDogMode.RAISE` is defined for API compatibility but
behaves as `RESET`. Assigning `timeout` pulls in the soft-float runtime for the
seconds↔milliseconds conversion; the compiler warns once so the cost is not a surprise.

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
| `arduino_mega` | ATmega2560 | ✅ D0–D53, A0–A15 |
| `arduino_micro` | ATmega32U4 | ✅ Board definition |
| `attiny85` | ATtiny85 | ✅ 8-pin DIP |
| `attiny45` / `attiny25` | ATtiny45 / ATtiny25 | ✅ 8-pin DIP (smaller flash) |
| `attiny84` | ATtiny84 | ✅ 14-pin DIP |
| `attiny44` / `attiny24` | ATtiny44 / ATtiny24 | ✅ 14-pin DIP (smaller flash) |
| `attiny2313` / `attiny4313` | ATtiny2313 / ATtiny4313 | ✅ 20-pin DIP |
| `attiny13` / `attiny13a` | ATtiny13 / ATtiny13A | ✅ 8-pin DIP (1 KB flash) |
| `digispark` | ATtiny85 | ✅ Digispark pin aliases |
| `adafruit_trinket` | ATtiny85 | ✅ Trinket pin aliases |

---

## Porting guide

### Add type annotations to variables

```python
count = 0          # CircuitPython — no annotation needed
count: int = 0     # PyMCU — required (int → int16 on AVR)
```

### Keep `time.sleep(float)` — it folds

```python
time.sleep(0.5)       # CircuitPython spelling; folds to delay_ms(500)
time.sleep_ms(500)    # PyMCU extension: same firmware, but not CircuitPython
```

A constant argument costs nothing, so `time.sleep(0.5)` is free and stays portable. The
`sleep_ms()` spelling exists because it avoids the seconds-to-milliseconds multiply when the
delay comes from a runtime variable, and that multiply would link the soft-float runtime.
Upstream CircuitPython has no `sleep_ms`, so reaching for it trades portability for those
bytes.

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

### `try / except` works — error sentinels are an alternative

`try / except / else / finally` and `raise` are supported on AVR (zero-cost T-flag model),
so CircuitPython error handling carries over. An explicit error sentinel is still a clean,
backend-portable bare-metal idiom when you prefer it:

```python
# CircuitPython style — supported on PyMCU/AVR
try:
    val = sensor.read()
except RuntimeError:
    val = -1

# Sentinel style — also fine, zero overhead on every backend
val: int = sensor.read()
if val == -32768:    # driver-specific error sentinel
    val = -1
```

### Lambda callbacks — named functions for `Timer`

`lambda x: expr` (without variable capture) is inlined at the call site. For a
`Timer` callback, use a named function so the ISR has a real entry point:

```python
# CircuitPython — lambdas work here
from machine import Timer
t = Timer(period=100, mode=Timer.PERIODIC, callback=lambda t: None)

# PyMCU — named function for the ISR callback
def on_tick():
    led.value = not led.value
```

---

## Differences from real CircuitPython

These are the **actual gaps** — anything not listed here behaves identically.

| Feature | CircuitPython | PyMCU |
|---|---|---|
| Execution model | Bytecode interpreter | **Native compiled — zero runtime overhead** |
| `time.sleep(s)` | Float seconds | ✅ Float accepted — a constant folds to `delay_ms()`; a runtime float links soft-float |
| `time.monotonic()` | Float seconds | ✅ Float — `millis() / 1000.0`; warns once about the soft-float cost |
| `float` arithmetic | Full support | Soft-float (~200–400 cycles/op) |
| `f"..."` format strings | Runtime evaluation | ✅ Runtime interpolation when **streamed** (`print(f"...")`, `uart.write_str(f"...")`) with format specs and `float` values; `s = f"..."` also works, into a compiler-sized fixed buffer (integers only) |
| `str.join` | Any iterable | ✅ In an assignment: `s = "".join([chr(b) for b in buf])` builds a runtime string from a fixed buffer; `sep.join([...])` of literals folds. Outside an assignment there is nowhere to put the result |
| `try / except` | Supported | ✅ Supported on AVR (zero-cost T-flag model) — error sentinels remain a valid bare-metal idiom |
| `bytearray` | Dynamic heap | ✅ Same spelling — `bytearray(8)` / `bytearray(b"...")` lower to a fixed `uint8[N]`; the size must be compile-time and cannot grow. `print(buf)` gives the CPython repr |
| `microcontroller.nvm[a:b] = ...` | Supported | ✅ Slice assignment compiles to byte writes; a slice *read* bound to a name still needs a heap |
| Lambda expressions | Supported | ✅ `lambda x: expr` (no capture) — inlined at the call site |
| `AnalogOut` | Supported (SAMD DAC) | ❌ No DAC on any AVR part — constructing one is refused at build time and names `pwmio.PWMOut` instead |
| `busio.I2C.scan()` | Returns list of addresses | Refused at build time; `probe(addr)` in a loop covers the same range |
| `neopixel.brightness` | Applies scaling | Accepted but not applied (ZCA constraint) |
| `supervisor.ticks_ms()` | 29-bit counter | 32-bit uint32 (~49-day wrap) |
| Target hardware | SAMD21, RP2040, ESP32, … | ATmega328P (Arduino Uno / Nano) |

