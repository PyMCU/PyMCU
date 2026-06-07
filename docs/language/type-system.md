(language-type-system)=
# Type System

PyMCU's type system maps directly to MCU register widths and memory layout. Every variable
must be annotated; the compiler rejects unannotated assignments.

---

## Primitive types

| Type | Width | Signed | Range | Notes |
|---|---|---|---|---|
| `uint8` | 8-bit | No | 0 – 255 | Default for pin values, flags, byte I/O |
| `int8` | 8-bit | Yes | -128 – 127 | Signed byte |
| `uint16` | 16-bit | No | 0 – 65535 | Counters, baud divisors, ADC results |
| `int16` | 16-bit | Yes | -32768 – 32767 | Signed 16-bit arithmetic |
| `int` | 16-bit | Yes | -32768 – 32767 | Built-in alias for `int16`; **no import needed** |
| `uint32` | 32-bit | No | 0 – 4 294 967 295 | Timestamps, large counters |
| `int32` | 32-bit | Yes | — | Signed 32-bit |
| `bool` | 8-bit | — | 0 / 1 | Aliases `uint8`; `True`/`False` fold to 1/0 |
| `float` | 32-bit | Yes | IEEE 754 single | Soft-float via `__fp_*` helpers (~200-400 cycles/op, AVR only) |

Import everything except `int` from `pymcu.types`:

```python
from pymcu.types import uint8, uint16, uint32, int8, int16, int32

count: uint16 = 0
flag:  uint8  = 0
n:     int    = 0   # int16, no import needed
```

---

## Pointer type — `ptr[T]`

`ptr[T]` maps a memory address to a typed register. Use it for memory-mapped I/O on AVR
and PIC.

```python
from pymcu.types import ptr, uint8

PORTB: ptr[uint8] = ptr(0x25)   # ATmega328P PORTB DATA register

PORTB.value = 0xFF   # write whole register (OUT 0x05, r16 or STS)
PORTB[5] = 1         # set bit 5   (SBI 0x05, 5 in I/O range)
bit: uint8 = PORTB[5]# read bit 5  (SBIS / IN)
```

Bit-index access compiles to `SBI`/`CBI` in the I/O range (0x20–0x3F) or `LDS`/`STS` +
mask outside it — no manual bit manipulation needed.

---

## Constant type — `const[T]`

`const[T]` declares a value that must be resolvable at compile time. The compiler emits no
SRAM allocation — the constant is folded into every use site.

```python
from pymcu.types import const, uint16

BAUD: const[uint16] = 9600        # compile-time constant
F_CPU: const[uint32] = 16_000_000
```

Attempting to assign a runtime expression to a `const[T]` variable is a `CompileError`.

### PROGMEM arrays

`const[uint8[N]]` places a byte array in flash (PROGMEM on AVR). Indexed reads use `LPM Z`.

```python
from pymcu.types import const, uint8

SINE_TABLE: const[uint8[8]] = [0, 90, 180, 255, 180, 90, 0, 0]

val: uint8 = SINE_TABLE[i]   # LPM Z — reads from flash, not SRAM
```

---

## Fixed-size arrays

Fixed-size arrays are allocated in SRAM. The size must be a compile-time constant integer.

```python
buf: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]

# Constant-index access — zero overhead (no SRAM, synthesized scalars)
buf[0] = 42
x: uint8 = buf[0]

# Variable-index access — Z-register indirect load/store
i: uint8 = 3
buf[i] = 99
```

`bytearray(N)` and `bytearray(b"...")` compile to `uint8[N]` SRAM arrays.

---

## Tuple types

Functions may return multiple values as a tuple. Values are stack-allocated; no heap needed.

```python
def divmod8(a: uint8, b: uint8) -> (uint8, uint8):
    q: uint8 = a // b
    r: uint8 = a - q * b
    return (q, r)

q, r = divmod8(10, 3)
```

---

## Type casts

Cast expressions truncate or zero-extend at runtime. Constant operands fold at compile time.

```python
from pymcu.types import uint8, uint16

wide: uint16 = 1000
narrow: uint8 = uint8(wide)   # truncate to low 8 bits

raw: uint8 = adc_read()
result: uint16 = uint16(raw)  # zero-extend to 16 bits
```

---

## `@inline` classes — Zero-Cost Abstractions

PyMCU's HAL is built on `@inline` classes (ZCA — Zero-Cost Abstractions). An `@inline` class
has no SRAM representation — every method and property is expanded at the call site, equivalent
to writing the assembly inline.

```python
led = Pin("PB5", Pin.OUT)   # no struct in SRAM; compiles to DDR/PORT instructions
led.high()                   # SBI 0x05, 5 — single instruction
led.toggle()                 # IN + EOR + OUT — three instructions
```

User-defined `@inline` classes follow the same pattern:

```python
@inline
class Sensor:
    def __init__(self, pin: str):
        self._pin = Pin(pin, Pin.IN)

    def read(self) -> uint8:
        return self._pin.value()
```

---

## Differences from Python's type system

| Python | PyMCU |
|---|---|
| `int` is arbitrary precision | `int` is 16-bit (`int16`) |
| `float` has hardware support (most CPUs) | `float` is soft-float, ~200-400 cycles/op |
| Types are optional hints | Types are **required** annotations |
| `None` is a runtime object | `None` folds to `Constant{-1}` |
| `bool` is a subclass of `int` | `bool` aliases `uint8` |
| `list` is dynamic | Arrays are fixed-size at compile time |
