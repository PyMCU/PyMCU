# Language Overview

## PyMCU is a compiler, not an interpreter

This distinction shapes every design decision in PyMCU.

| | MicroPython / CircuitPython | **PyMCU** |
|---|---|---|
| Python runtime on MCU | Yes — ~256 KB interpreter | **No** |
| Bytecode executed at runtime | Yes | **No** |
| Types resolved | At runtime | **At compile time** |
| Heap allocation | Yes (garbage collected) | **No** |
| Flash usage | 256 KB + your code | **Your code only** |
| Execution speed | Bytecode (slow) | **Native machine code** |

When you run `pymcu build`, the **compiler** (running on your PC) reads your Python source,
type-checks it, and emits native AVR assembly for the ATmega328P. The MCU never sees Python
syntax — only machine instructions.

---

## Supported Python subset

PyMCU accepts a statically-typed, allocation-free subset of Python 3. The guiding rule is:

> **If the size and type of every value is not known at compile time, it cannot be compiled.**

### Supported statements

`if / elif / else`, `while`, `for`, `match / case`, `def`, `class`, `return`, `pass`,
`import`, `from … import`, `global`, `with`, `assert` (compile-time), `raise` (compile-time).

### Supported expressions

Arithmetic (`+ - * / % //`), comparison, bitwise, logical (`and`/`or`/`not`),
ternary, augmented assignment, type casts (`uint8(x)`), `abs`, `min`, `max`, `len`,
walrus (`:=`), tuple literals and unpacking, member access, array indexing, list
comprehensions (compile-time constant bounds only).

### MCU-specific extensions

- `uint8`, `int8`, `uint16`, `int16`, `uint32`, `int32` primitive types
- `ptr[T]` — memory-mapped I/O pointer
- `const[T]` — compile-time constant enforcement
- `asm("instr")` — inline assembly with register constraints
- `@inline` — zero-cost function expansion
- `@interrupt(vector)` — hardware ISR handler
- `__CHIP__` / `__FREQ__` — conditional compilation by chip name and clock frequency

---

## Static typing is mandatory

Every variable must carry a type annotation at its first assignment. The compiler does not
infer types — the annotation drives register width, instruction selection, and memory layout.

```python
# Correct — annotation at first assignment
count: uint16 = 0
flag:  uint8  = 0

# Correct — built-in int maps to int16, no import required
n: int = read_sensor()

# CompileError — no annotation
x = 42
```

The built-in `int` maps to `int16` and requires no import. All other sized types come from
`pymcu.types`:

```python
from pymcu.types import uint8, uint16, uint32, int8, int16, int32, ptr, const
```

---

## No heap — everything is stack or global

PyMCU targets microcontrollers with 32 bytes to 8 KB of SRAM. There is no `malloc`, no
garbage collector, and no heap. Every value must have a fixed size known at compile time:

```python
# OK — size is known at compile time
buf: uint8[8] = [0] * 8

# CompileError — dynamic size
n = get_size()
buf: uint8[n] = [0] * n    # cannot allocate variable-size array
```

Container types that require heap allocation (`list.append`, `dict`, `set`) are not available.
See {doc}`limitations` for the full list with alternatives.

---

## Module system

PyMCU compiles multi-file projects. Imports resolve to PyMCU stdlib modules or your own
source files:

```python
from pymcu.hal.gpio import Pin      # HAL module
from pymcu.types import uint8       # type definitions
from sensors import read_dht11      # your own module in src/
```

Third-party PyPI packages cannot be used — only the `pymcu` stdlib and modules you write
yourself (in PyMCU-compatible Python) are compiled.

---

## Entry point

PyMCU supports two entry-point styles:

**Explicit `def main():`** (recommended):

```python
def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.toggle()
        delay_ms(500)
```

**Top-level script style** (MicroPython / CircuitPython compatible):

```python
led = Pin("PB5", Pin.OUT)
while True:
    led.toggle()
    delay_ms(500)
```

The compiler synthesizes a `main` entry point from top-level executable statements
automatically.

---

## Conditional compilation

`__CHIP__` and `__FREQ__` are compile-time string/integer constants. Dead branches are
eliminated before code generation — no runtime cost:

```python
if __CHIP__.arch == "avr":
    asm("nop")    # only emitted; dead branches removed at compile time
```
