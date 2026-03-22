# Whisnake Language Reference

Whisnake compiles a **statically-typed, allocation-free subset of Python** to bare-metal MCU machine
code. No runtime, no heap, no garbage collector. This document is the canonical reference for every
language feature the compiler accepts.

---

## 1. Quick Reference

| Category | Feature | Status |
|---|---|---|
| **Statements** | `if / elif / else` | Complete |
| | `while` + `break` / `continue` | Complete |
| | `for i in range(n)` | Complete |
| | `for x in array` | Complete |
| | `match / case` (literal, wildcard, OR patterns) | Complete |
| | `def` (typed params, defaults, keyword args) | Complete |
| | `class` (ZCA `@inline`, `@property`) | Complete |
| | `class Foo(Enum)` (zero-cost integer constants) | Complete |
| | `with obj:` (context manager, `__enter__`/`__exit__`) | Complete |
| | `assert condition, "msg"` (compile-time only) | Complete |
| | `return` | Complete |
| | `pass` | Complete |
| | `raise` (compile-time only) | Complete |
| | `import` / `from ... import` / `import ... as` | Complete |
| | `global` | Complete |
| **Expressions** | Integer literals (dec, hex, bin, oct, `_` separators) | Complete |
| | `True` / `False` / `None` | Complete |
| | String literals (double- and single-quoted) | Complete |
| | Arithmetic `+ - * / % //` | Complete |
| | Comparison `== != < <= > >=` | Complete |
| | Bitwise `& \| ^ ~ << >>` | Complete |
| | Logical `and` / `or` / `not` | Complete (full short-circuit evaluation) |
| | Augmented assignment `+= -= *= //= &= \|= ^= <<= >>=` | Complete (variables, subscripts, member targets) |
| | Ternary `val = x if cond else y` | Complete |
| | Type cast `uint8(val)`, `uint16(val)` | Complete (constant-fold at compile time) |
| | `abs(x)`, `min(a, b)`, `max(a, b)` | Complete |
| | `ord('A')`, `chr(65)` (compile-time) | Complete |
| | Multiple assignment `a = b = 0` | Complete |
| | `len(arr)` / `len([...])` (compile-time) | Complete |
| | Walrus `:=` `(c := uart.read())` | Complete |
| | Bit-index `reg[n]` | Complete |
| | Array index `arr[i]` (const and variable) | Complete |
| | Tuple literal `(a, b)` | Complete |
| | Tuple unpacking `a, b = func()` | Complete |
| | Member access `obj.x`, method calls `obj.m()` | Complete |
| **MCU extensions** | `uint8 / int8 / uint16 / int16 / uint32 / int32` | Complete |
| | `ptr[T]` pointer type | Complete |
| | `const[T]` compile-time constant | Complete |
| | `asm("instr")` inline assembly | Complete |
| | `delay_ms(n)` / `delay_us(n)` | Complete |
| | `@inline` decorator | Complete |
| | `@interrupt(vector)` ISR decorator | Complete |
| | `@property` / `@name.setter` | Complete |
| | Conditional compilation `__CHIP__` | Complete |
| **Arrays** | Fixed-size arrays `arr: uint8[N]` | Complete |
| | Constant-index access (zero overhead) | Complete |
| | Variable-index access (SRAM) | Complete |
| | List comprehension (compile-time constant only) | Complete |
| **Not supported** | Heap allocation, `list.append`, `dict`, `set` | No heap |
| | `try / except`, `async / await` | No runtime |
| | `float`, `complex`, `Decimal` | Planned T3 |
| | `f"..."` f-strings (runtime) | Planned T3 |
| | List comprehension (runtime bounds) | No heap |

---

## 2. Types and Annotations

All variables **must** have a type annotation at their first assignment or declaration. The
compiler infers no types — the annotation drives register width, instruction selection, and
memory layout.

### Primitive types

| Type | Width | Range | Notes |
|---|---|---|---|
| `uint8` | 8-bit | 0 – 255 | Default for pin values, flags, bytes |
| `int8` | 8-bit | -128 – 127 | Signed byte |
| `uint16` | 16-bit | 0 – 65535 | Counters, UART baud divisors |
| `int16` | 16-bit | -32768 – 32767 | Signed 16-bit |
| `uint32` | 32-bit | 0 – 4294967295 | Timestamps, large counters |
| `int32` | 32-bit | — | Signed 32-bit |
| `bool` | 8-bit | 0 / 1 | Aliases `uint8`; `True`/`False` fold to 1/0 |

```python
x: uint8 = 0
counter: uint16 = 0
flag: bool = False
```

### Pointer type `ptr[T]`

Maps directly to a memory-mapped register address. Dereferencing with `.value` reads or writes
the full register. Subscript access `reg[n]` reads or writes individual bits.

```python
from whisnake.types import ptr, uint8

PORTB: ptr[uint8] = ptr(0x25)   # ATmega328P PORTB DATA address
PORTB.value = 0xFF               # write whole register
PORTB[5] = 1                     # set bit 5 (SBI on AVR I/O space)
bit: uint8 = PORTB[5]            # read bit 5 (SBIS/SBIC)
```

### Constant type `const[T]`

Declares a value that must be resolvable at compile time. The compiler rejects any attempt to
assign a runtime expression to a `const[T]` variable.

```python
BAUD: const[uint16] = 9600
```

### Arrays

Fixed-size arrays are allocated in SRAM. Size must be a compile-time constant.

```python
buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]

# Constant-index access: zero overhead (synthesized scalars)
buf[0] = 42
x: uint8 = buf[0]

# Variable-index access: SRAM + Z-register indirect load/store
i: uint8 = 3
buf[i] = 99
```

#### List comprehensions (compile-time only)

List comprehensions with compile-time constant bounds are supported and unroll at compile time:

```python
# Supported: compile-time constant range
powers: uint8[5] = [2**i for i in range(5)]   # [1, 2, 4, 8, 16]
doubled: uint8[10] = [i*2 for i in range(10)] # [0, 2, 4, 6, ...]

# NOT supported: runtime variable bounds
n: uint8 = get_size()
values: uint8[10] = [i for i in range(n)]  # CompileError — use a for loop instead
```

The comprehension must have a constant range bound known at compile time. The result is a fixed-size array.

### Tuple returns

Functions may return multiple values as a tuple. Values are stack-allocated.

```python
def divmod8(a: uint8, b: uint8) -> (uint8, uint8):
    q: uint8 = a // b
    r: uint8 = a - q * b
    return (q, r)

q, r = divmod8(10, 3)   # q=3, r=1
```

---

## 3. Variables and Scope

- **Local variables** — allocated to registers (R4-R15) or SRAM stack slots. Scope is the
  enclosing `def`.
- **Global variables** — declared with `global name` inside a function, allocated to named SRAM
  labels (`_global_name`). Accessible across functions.
- **Inline prefix** — inside an `@inline` expansion, every variable is prefixed with the inline
  call chain (`inline1.func.varname`). This prevents name collisions across multiple call sites.
- **No closures, no `nonlocal`** — inner `def` is not supported; use module-level or `@inline`
  class methods.

```python
count: uint16 = 0    # global in main module

def increment():
    global count
    count += 1
```

---

## 4. Operators

### Arithmetic

| Operator | Description | Notes |
|---|---|---|
| `+` | Addition | Wraps on overflow (no UB) |
| `-` | Subtraction | |
| `*` | Multiplication | |
| `/` | Division (integer) | Truncates toward zero |
| `//` | Floor division | Same as `/` for unsigned; explicit in source |
| `%` | Modulo | |
| `-x` | Unary negate | |

### Comparison

`==  !=  <  <=  >  >=` — all produce `uint8` (0 or 1).

### Bitwise

| Operator | Description |
|---|---|
| `&` | Bitwise AND |
| `\|` | Bitwise OR |
| `^` | Bitwise XOR |
| `~` | Bitwise NOT |
| `<<` | Left shift |
| `>>` | Right shift |

### Logical

`and`, `or`, `not` use full short-circuit evaluation. The right-hand operand is not evaluated
when the result is already determined by the left-hand operand:

```python
if pin.value() and sensor.read():   # sensor.read() skipped if pin.value() == 0
    process()

if done or retry_count > 5:         # retry_count check skipped if done != 0
    finish()
```

### Ternary expression

```python
val: uint8 = HIGH if enabled else LOW
level: uint8 = 255 if saturated else measured
```

### Augmented assignment

`+=  -=  *=  //=  &=  |=  ^=  <<=  >>=` are supported on variable, subscript, and member targets:

```python
count += 1          # variable target
arr[i] += 1         # subscript target (SRAM array)
PORTB[5] ^= 1       # bit-index target (toggle pin)
sensor.value += 2   # member target
```

### Type casts

Explicit width conversion. Constant expressions are folded at compile time:

```python
x: uint16 = 1000
y: uint8 = uint8(x)     # truncate to low byte (232)
z: uint16 = uint16(y)   # zero-extend
```

### Built-in functions

| Function | Description |
|---|---|
| `abs(x)` | Absolute value |
| `min(a, b)` | Minimum of two values |
| `max(a, b)` | Maximum of two values |
| `ord('A')` | ASCII code of a single-character literal (compile-time) |
| `chr(65)` | Character literal to integer (compile-time identity) |
| `len(arr)` | Element count of a fixed-size array or list literal (compile-time constant) |

### Walrus operator `:=`

Assigns a value and returns it, enabling assignment inside conditions and loops:

```python
# UART line reader
i: uint8 = 0
while (c := uart.read()) != '\n':
    buf[i] = c
    i += 1

# Sensor polling loop
while (v := adc_read()) < threshold:
    process(v)
```

The variable must already be declared with a type annotation before the walrus expression.

### `range()` — full syntax

All three forms are supported with runtime or compile-time bounds:

```python
for i in range(8):          # 0..7
for i in range(1, 8):       # 1..7
for i in range(0, 16, 2):   # 0,2,4,...,14
for i in range(10, 0, -1):  # 10,9,...,1 (countdown)
n: uint8 = uart.read()
for i in range(n):          # runtime bound
```

---

## 5. Control Flow

### `if / elif / else`

Standard Python semantics. Compile-time dead-code elimination applies when the condition is a
`__CHIP__` attribute comparison — the compiler evaluates the condition at compile time and omits
the dead branch entirely.

```python
if x > 0:
    led.high()
elif x == 0:
    led.low()
else:
    led.toggle()
```

### `while`

```python
while not done:
    process()
    if error:
        break
```

`break` and `continue` are fully supported.

### `for`

```python
# range() with runtime or compile-time bound
for i in range(8):
    buf[i] = 0

# Iterate over a fixed-size array
for val in buf:
    uart.write(val)

# enumerate() — compile-time index counter
for i, val in enumerate(buf):
    process(i, val)
```

### `match / case`

Supports literal patterns, wildcard `_`, and OR patterns (`|`). Compile-time DCE applies when
matching on `__CHIP__` attributes.

```python
match state:
    case 0:
        init()
    case 1 | 2:
        run()
    case _:
        error()
```

Dotted-name patterns (e.g. `case State.IDLE:`) require the right-hand side to be a **dotted**
name (not a bare identifier) to avoid capture:

```python
match mode:
    case Pin.OUT:
        setup_output()
    case Pin.IN:
        setup_input()
```

### `with`

Calls `__enter__()` before the body and `__exit__()` after. Zero-cost when both methods are
`@inline`. This is the preferred pattern for SPI, I2C, and critical-section wrappers:

```python
with spi:
    spi.write(0xAB)         # select() before, deselect() after

with i2c:
    i2c.write(0xD0)         # start() before, stop() after
    i2c.write(0x3B)
```

`as` binding is also supported:

```python
with spi as bus:
    bus.write(0xAA)
```

### `assert`

Compile-time assertion. Evaluated at compile time — statically false expressions raise a
`CompileError`; runtime-variable conditions are silently stripped (no MCU code emitted):

```python
assert TABLE_SIZE <= 256, "TABLE_SIZE must fit in uint8"
assert __CHIP__.arch == "avr", "This file requires AVR"
```

---

## 6. Functions and Decorators

### Regular functions

All parameters must be type-annotated. Default values and keyword arguments are supported.

```python
def clamp(val: uint8, lo: uint8, hi: uint8) -> uint8:
    if val < lo:
        return lo
    if val > hi:
        return hi
    return val
```

### `@inline`

Marks a function for zero-cost inline expansion at every call site. No stack frame is allocated;
the body is emitted in-place. Ideal for small helper functions and ZCA class methods.

```python
@inline
def nibble_to_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48    # '0'
    return n + 55        # 'A'
```

**Constraints:**
- Inline functions containing `asm()` with labels must delegate asm to a non-inline sub-helper
  (labels would duplicate at multiple call sites).
- No recursion inside `@inline` functions.

### `@interrupt(vector)`

Declares an ISR. The compiler emits `ISR(vector)` in assembly. All registers used inside the ISR
are automatically saved/restored.

```python
@interrupt(0x0002)    # INT0 on ATmega328P
def on_button():
    global count
    count += 1
```

Global variables written from an ISR must be declared `global` inside the ISR and accessed
atomically (disable interrupts or use `GPIOR0` flag pattern).

### `@property` / `@name.setter`

Supported inside classes. Compile-time expansion only — no descriptor protocol at runtime.

```python
class Sensor:
    @property
    def value(self) -> uint8:
        return self._raw

    @value.setter
    def value(self, v: uint8):
        self._raw = v
```

---

## 7. Classes and Inheritance

### ZCA (Zero-Cost Abstraction) classes

Classes decorated (or called) with `@inline` are statically flattened. No SRAM is used for the
instance — all methods expand inline and member accesses resolve to registers or named globals.

```python
class LED:
    @inline
    def __init__(self, pin: str):
        self._pin = Pin(pin, Pin.OUT)

    @inline
    def on(self):
        self._pin.high()

    @inline
    def off(self):
        self._pin.low()
```

### Single-level inheritance

```python
class Base:
    @inline
    def init(self):
        self._x: uint8 = 0

class Derived(Base):
    @inline
    def run(self):
        self.init()
        self._x += 1
```

**Not supported:** multiple inheritance, runtime polymorphism, `isinstance()`, metaclasses.

### `Enum` classes (zero-cost)

Classes that inherit from `Enum` or `IntEnum` are flattened to compile-time integer constants.
No SRAM is allocated. Use them to name states, modes, or error codes:

```python
from enum import Enum

class State(Enum):
    IDLE    = 0
    RUNNING = 1
    ERROR   = 2

state: uint8 = State.IDLE

match state:
    case State.IDLE:
        init()
    case State.RUNNING:
        run()
    case State.ERROR:
        handle_error()
```

Enum fields are accessible as `ClassName.FIELD` anywhere in the file. No `import` of `Enum`
is needed if the class is defined in the same file — the compiler recognises the `(Enum)` base
directly.

---

## 8. Imports and Modules

```python
import foo
from foo import Bar
from foo import Bar as B
from foo.sub import helper
```

- **Relative imports** (`from . import foo`) are supported.
- **`__init__.py`** re-exports work at compile time.
- **Third-party PyPI packages** are not supported — only `pymcu` stdlib and compat packages
  (`whisnake-circuitpython`, `whisnake-micropython`).
- **Circular imports** are not supported.
- **`import X as Y`** — fully supported.

---

## 9. MCU-Specific Extensions

### `asm("instruction")`

Emits a single assembly instruction verbatim. The instruction is target-specific. String must
be a compile-time constant.

```python
asm("cli")    # disable interrupts (AVR)
asm("sei")    # enable interrupts (AVR)
asm("nop")    # no-operation
```

### `delay_ms(n)` / `delay_us(n)`

Busy-wait delays. `n` may be a runtime value. On AVR, the delay loop is calibrated to 16 MHz;
no hardware timer is consumed.

```python
delay_ms(500)          # 500 ms busy-wait
delay_us(10)           # 10 µs busy-wait
delay_ms(interval)     # runtime variable is OK
```

### `ptr(addr)` / `ptr[T]`

Creates a typed pointer to a memory-mapped address. Used for direct register access.

```python
from whisnake.types import ptr, uint8

SREG: ptr[uint8] = ptr(0x5F)    # Status register (ATmega328P)
SREG[7] = 1                      # sei — enable global interrupts
saved: uint8 = SREG.value        # read whole register
```

### `const[T]`

Compile-time constant marker. The compiler enforces that the value is statically known.

```python
from whisnake.types import const, uint8

BAUD: const[uint16] = 9600
TABLE_SIZE: const[uint8] = 16
```

### `__CHIP__` — Conditional compilation

`__CHIP__` is a special compile-time object with attributes `arch`, `name`, `ram_size`, and
`flash_size`. Comparisons against `__CHIP__` attributes in `if` / `match` are evaluated at
compile time and dead branches are eliminated.

```python
match __CHIP__.arch:
    case "avr":
        init_avr()
    case "pic14":
        init_pic14()
    case _:
        raise RuntimeError("Unsupported architecture")
```

---

## 10. Standard Library Overview

### `whisnake.hal.gpio` — GPIO

```python
from whisnake.hal.gpio import Pin

led = Pin("PB5", Pin.OUT)       # output pin
btn = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)   # input with pull-up

led.high()                       # set high
led.low()                        # set low
led.toggle()                     # toggle
led.value(1)                     # write value
v: uint8 = led.value()           # read value
led.irq(Pin.IRQ_FALLING)         # configure interrupt (setup only)
w: uint16 = led.pulse_in(1, timeout_us=1000)   # measure pulse width
```

**Mode constants:** `Pin.IN`, `Pin.OUT`, `Pin.OPEN_DRAIN`
**Pull constants:** `Pin.PULL_UP`, `Pin.PULL_DOWN`
**IRQ constants:** `Pin.IRQ_FALLING`, `Pin.IRQ_RISING`, `Pin.IRQ_LOW_LEVEL`, `Pin.IRQ_HIGH_LEVEL`

### `whisnake.hal.uart` — UART

```python
from whisnake.hal.uart import UART

uart = UART(9600)
uart.write(65)                  # send byte
b: uint8 = uart.read()          # receive byte (blocking)
uart.write_str("hello")         # flash string → UART
uart.println("done")            # write_str + newline
uart.print_byte(42)             # print decimal uint8 + newline
```

### `whisnake.hal.adc` — ADC

```python
from whisnake.hal.adc import AnalogPin

adc = AnalogPin("A0")
adc.start()
# poll ADCSRA[6] (ADSC) to wait for conversion, then read ADCL/ADCH
```

### `whisnake.hal.timer` — Timer

```python
from whisnake.hal.timer import Timer

# n is a compile-time constant — the compiler emits only the code for the selected timer
t0 = Timer(0, 64)     # Timer0 (8-bit), prescaler 64 — AVR + PIC
t1 = Timer(1, 1024)   # Timer1 (16-bit), prescaler 1024 — ATmega328P
t2 = Timer(2, 256)    # Timer2 (8-bit async), prescaler 256 — ATmega328P

t0.start()            # enable overflow interrupt
t0.stop()             # disable overflow interrupt + stop clock
t0.clear()            # reset counter to 0
ovf: uint8 = t0.overflow()   # poll overflow flag (1 if fired)
```

Use `@interrupt(vector)` to handle the overflow in an ISR:

```python
@interrupt(0x0010)    # Timer0 OVF (ATmega328P)
def on_tick():
    global ticks
    ticks += 1
```

### `whisnake.hal.pwm` — PWM

```python
from whisnake.hal.pwm import PWM

pwm = PWM("PD6", duty=128)   # OC0A, 50% duty cycle
pwm.start()
pwm.set_duty(200)
pwm.stop()
```

### `whisnake.hal.spi` — SPI (AVR)

```python
from whisnake.hal.spi import SPI

spi = SPI()
with spi:
    result: uint8 = spi.transfer(0xAA)   # select() before, deselect() after
    spi.write(0x55)
```

### `whisnake.hal.i2c` — I2C / TWI (AVR)

```python
from whisnake.hal.i2c import I2C

i2c = I2C()
if i2c.ping(0x68):           # check if device responds
    with i2c:
        i2c.write(0xD0)      # address + write (start() before, stop() after)
        i2c.write(0x3B)      # register
```

### `pymcu.time` — Delays

```python
from whisnake.time import delay_ms, delay_us

delay_ms(1000)     # 1 second
delay_us(100)      # 100 µs
```

### `pymcu.drivers.dht11` — DHT11 sensor

```python
from pymcu.drivers.dht11 import DHT11

sensor = DHT11("PD4")
result: uint16 = sensor.read()    # high byte = humidity, low byte = temp
# result == 0xFFFF means read error
```

### `whisnake.hal.eeprom` — Non-volatile storage

```python
from whisnake.hal.eeprom import EEPROM

ee = EEPROM()
ee.write(0x00, 42)         # persist a byte (~3.4 ms write latency)
val: uint8 = ee.read(0x00) # read it back (instant)
```

ATmega328P: 1024 bytes at addresses `0x000`–`0x3FF`, ~100 k write endurance per cell.

### `whisnake.hal.watchdog` — Watchdog timer

```python
from whisnake.hal.watchdog import Watchdog

wdt = Watchdog(timeout_ms=500)   # compile-time constant only
wdt.enable()

while True:
    do_work()
    wdt.feed()    # must be called within 500 ms or MCU resets
```

`disable()` stops the watchdog. See the [Watchdog reference](stdlib/watchdog.md) for the full
timeout table.

### `whisnake.hal.power` — Sleep modes

```python
from whisnake.hal.power import sleep_idle, sleep_power_down

# Deepest sleep — ~0.1 µA; wake on external interrupt
asm("sei")
while True:
    sleep_power_down()
```

| Function | Current | Wake sources |
|---|---|---|
| `sleep_idle()` | ~6 mA | All interrupts |
| `sleep_adc_noise()` | ~1 mA | ADC, ext-int, Timer2, TWI |
| `sleep_power_down()` | ~0.1 µA | INT0/1, WDT, TWI |
| `sleep_power_save()` | ~0.1 µA | Same + Timer2 async |
| `sleep_standby()` | ~0.1 µA | Same as power-down, faster wake |

Global interrupts (`sei`) must be enabled before calling any sleep function.

### `pymcu.boards.arduino_uno` — Arduino Uno pin names

```python
from pymcu.boards.arduino_uno import D13, D2, A0, LED_BUILTIN

led = Pin(LED_BUILTIN, Pin.OUT)    # PB5
```

---

## 11. Comparison: Whisnake vs Python vs MicroPython vs CircuitPython

| Concept | Python 3 | MicroPython | CircuitPython | Whisnake |
|---|---|---|---|---|
| Integer type | `int` (arbitrary precision) | `int` (30-bit on most ports) | `int` (30-bit) | `uint8/16/32`, `int8/16/32` — annotation required |
| Float | `float` (64-bit IEEE) | `float` (32-bit) | `float` (32-bit) | Not yet (planned T3) |
| Heap / GC | ✅ `malloc` + GC | ✅ small heap + GC | ✅ small heap + GC | No heap at all |
| GPIO | `RPi.GPIO` or similar | `machine.Pin(13, OUT)` | `digitalio.DigitalInOut(board.D13)` | `Pin("PB5", Pin.OUT)` |
| GPIO via compat | — | `from machine import Pin` | `import digitalio` | `whisnake-micropython` / `whisnake-circuitpython` |
| UART | `pyserial` | `machine.UART(1, 9600)` | `busio.UART(TX, RX, baudrate=9600)` | `UART(9600)` |
| ADC | — | `machine.ADC(Pin(26))` | `analogio.AnalogIn(board.A0)` | `AnalogPin("A0")` |
| Delay | `time.sleep(s)` | `utime.sleep_ms(n)` | `time.sleep(s)` | `delay_ms(n)` |
| Constant | — | `micropython.const(0xFF)` | — | `const[T]` annotation |
| Zero-cost fn | — | `@micropython.native` | — | `@inline` |
| ISR | `signal` module | `Pin.irq()` callback | — | `@interrupt(vector)` |
| Inline asm | `ctypes` | `@micropython.viper` | — | `asm("instr")` |
| Type annotations | Optional (hints only) | Optional (hints only) | Optional (hints only) | **Required** — drives codegen |
| `for` loop | ✅ | ✅ | ✅ | ✅ (`range(n)`, arrays, `enumerate`) |
| `match / case` | ✅ (3.10+) | ❌ | ❌ | ✅ |
| Short-circuit `and/or` | ✅ | ✅ | ✅ | ✅ |
| `try / except` | ✅ | ✅ | ✅ | No runtime exceptions |
| `f"str {x}"` | ✅ | ✅ | ✅ | Not yet (T3.2 planned) |
| `list` / `dict` | ✅ | ✅ | ✅ | Fixed-size arrays only |
| Classes | ✅ full OOP | ✅ | ✅ | ZCA `@inline` only; no vtable |
| Multiple inheritance | ✅ | ✅ | ✅ | Not supported |
| `async / await` | ✅ | ✅ | ✅ | Not supported — use `@interrupt` |

---

## 12. Migration Guide

### CircuitPython → Whisnake

Install the `whisnake-circuitpython` compat package and add to `pyproject.toml`:

```toml
[tool.whip]
stdlib = ["circuitpython"]
```

Then most CircuitPython code compiles unchanged:

```python
# CircuitPython code — works as-is with whisnake-circuitpython
import board
import digitalio
import time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT

while True:
    led.value = True
    time.sleep_ms(500)
    led.value = False
    time.sleep_ms(500)
```

**Things that still need changes:**
- Type annotations: add `x: uint8 = 0` where the compiler needs a width.
- `float` arithmetic: replace with integer or fixed-point.
- `try / except`: use return codes + `match / case`.
- `f"..."` format strings: use `uart.write_str()` + `uart.print_byte()`.

### MicroPython → Whisnake

Install the `whisnake-micropython` compat package:

```toml
[tool.whip]
stdlib = ["micropython"]
```

```python
# MicroPython code — works with whisnake-micropython
from machine import Pin, UART
from utime import sleep_ms

led = Pin(13, Pin.OUT)   # D13 by Arduino Uno number
uart = UART(0, 9600)

while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

**Things that still need changes:**
- Integer pin numbers map to Arduino Uno digital pin numbers via the compat package.
- `micropython.const(0xFF)` is treated as an integer literal.
- `Pin.irq()` callbacks are not supported — use `@interrupt(vector)` ISRs.
- `Timer` callbacks are not supported — use `@interrupt` on the timer overflow vector.
- `read_u16()` on ADC returns a 16-bit value (0-1023 scaled to 0-65535).

### Plain Python → Whisnake

The biggest changes when porting generic Python:

1. **Add type annotations everywhere** — the compiler requires them.
2. **Remove dynamic allocation** — no `list.append()`, no `dict`, no `set`.
3. **Replace `float`** — use `int16` with manual fixed-point scaling (e.g. `× 100` for 2 decimal places).
4. **Replace `try/except`** — return sentinel values (`0xFF` for error), use `match / case`.
5. **Replace `print(f"val={x}")` f-strings** — use `uart.write_str("val="); uart.print_byte(x)`.
6. **Replace `time.sleep(s)`** — use `delay_ms(n)` or `delay_us(n)`.
7. **Avoid closures and nested `def`** — use module-level or `@inline` class methods.

---

## Getting Help

- Open an issue at the Whisnake repository with your source snippet and compiler error.
- Check `docs/LIMITATIONS.md` for a full list of unsupported features.
- Check `LANGUAGE_ROADMAP.md` for planned features and their implementation status.
