# Language Limitations

**Read this before writing your first project.**

PyMCU compiles a statically-typed, allocation-free subset of Python to bare-metal machine code.
There is no runtime, no heap, no garbage collector, and no interpreter. Many standard Python
features are therefore incompatible with this model.

:::{admonition} Standard Library Philosophy
:class: note

Because of the architectural differences between a PC and a bare-metal microcontroller, PyMCU **does not attempt to replicate the CPython standard library 1:1**.

Instead, PyMCU adopts the philosophy and API design of **MicroPython and CircuitPython** (specifically the `machine` and `board` modules) as its official user-facing standard library. This ensures that code written for PyMCU looks familiar to developers coming from the broader Python-on-hardware ecosystem, even though it executes entirely differently.
:::

This page lists every known unsupported feature, explains *why* it cannot be compiled, and
suggests the idiomatic PyMCU alternative where one exists.

---

## Dynamic memory and containers

| Feature | Why it fails | Alternative |
|---|---|---|
| `list.append(x)` on a **fixed-size** array | Fixed arrays have no append | `list[uint8]` heap-bounded list, or `uint8[N]` fixed-size array |
| **Growing** `dict` (unbounded) | Hash table requires heap | `pymcu.collections.FixedDict(capacity)` (mutable, fixed footprint), a closed dict literal (below), or `match / case` key dispatch |
| **Mutable** `set` (`.add()`) | Hash set requires heap | Closed set literal (below), or a `uint8` bitmask |

**Supported:** `list[T]` (`x: list[uint8] = list()`) compiles to a bounded bump-allocator
with GC; supports `append()`, `len()`, `x[i]`, `for v in x:`.
`bytearray(N)` and `bytearray(b"...")` compile to SRAM `uint8[N]` arrays.
**Closed dict/set literals** (`d = {0: 10, "mid": 2}` / `OK = {1, 3, 5}`) bind compile-time
lookup tables with no storage: `d[const]` folds to its value, `d[runtime_key]` lowers to a
compare chain that raises `KeyError` (catchable with `try/except`) on no match, `x in d` /
`x in {...}` test membership, and `len(d)` folds. They are read-only.
**`pymcu.collections.FixedDict(capacity)`** is the mutable counterpart: a fixed-capacity
integer dict (open addressing over per-instance fixed arrays — no heap, no GC) with Python
semantics where they fit a fixed footprint: `d[k]` / `d[k] = v`, `KeyError` on a missing
key, `ValueError` when inserting into a full dict, `k in d`, `len(d)`, `get(k, default)`,
`pop(k)`, `clear()`. The capacity is a compile-time constant.
Fixed-size arrays `arr: uint8[N]` support both constant- and variable-index access.

**Rule of thumb:** if the size is not known at compile time, it cannot be compiled.

---

## String operations

| Feature | Why it fails | Alternative |
|---|---|---|
| `f"..."` inline in arbitrary expressions | No general runtime string objects | Assign it to a name first (`s = f"..."` builds a fixed buffer), or stream it: `print(f"...")` |
| `str.split()`, `str.format()` | Heap strings | Not available |
| `str.join()` outside an assignment | The result needs a home | `s = sep.join([...])` folds compile-time strings; `s = ''.join([chr(b) for b in buf])` builds a runtime string from a fixed buffer |
| `len(string_variable)` | Runtime string object required | Use fixed-size buffers |
| `str + str` concatenation | Heap allocation | Separate `uart.write_str()` calls |
| `str[i]` on a runtime string | No runtime string object | Use `const[str]` parameters |

**Supported:** String literals in flash, raw strings `r"\n"`, `uart.println("literal")`,
`for ch in "ABC":` (compile-time unroll), `const[str]` runtime subscript (reads byte from
flash), and **runtime f-strings streamed directly to a sink** — see below.

### A str that different paths bind differently

A string is a compile-time value, so a name normally *is* its text. When two paths bind the
same name to different texts, what the name holds at run time is the id of the text, and a
read picks the matching one:

```python
s: str = "idle"
if seed > 10:
    s = "running"
print(s)              # writes "running" or "idle", decided at run time
if s == "running":    # compares the ids, also at run time
    ...
```

The two texts stay in flash and the name costs one 16-bit slot; nothing is copied into RAM.
This covers the three ways a name can end up with more than one text: a run-time branch, a
loop body that rebinds it, and a module-level `str` that a function rebinds through `global`.

Only `print()`, `uart.write_str()` / `println()` and `==` / `!=` against a literal can read
such a name. Anything else (`len(s)`, `s[i]`, `s + t`, passing it to a `const[str]`
parameter) is a compile error naming the texts it can hold, because there is no single text
to hand over.

### f-strings (streamed)

`f"..."` with **runtime interpolations** is supported streamed to a sink — the compiler
lowers each piece to a direct write (no heap, no format buffer) — **and as a value**:
`s = f"t={t} C"` builds the string into a compiler-managed fixed `bytearray` whose size is
statically bounded per part (`pymcu.strfmt` lowering, auto-injected by the build). On the
value form, `len(s)` is the formatted length, `s[i]` indexes bytes, `print(s)` /
`uart.write_str(s)` stream it, and re-assigning `s` in a loop reuses the buffer (assign the
longest f-string first — the buffer is sized at the first assignment). Not yet supported in
the value form: float interpolations, `s == "lit"` comparison, and f-strings inline in
other expression positions (assign to a name first). Streamed examples:

```python
print(f"adc={raw} v={mv:04d}")
uart.write_str(f"t={temp:5d}")
uart.println(f"err 0x{code:02X}")
lcd.print_str(f"{hours:02d}:{mins:02d}")
```

**Format specs** supported in interpolations: `{x:02x}`, `{x:X}`, `{x:08b}`, `{x:o}`,
`{x:5d}`, `{x:04d}` (width, zero-pad, and `x`/`X`/`b`/`o`/`d` bases). Compile-time constant
interpolations (`f"text={const}"`) are folded into the flash string as before.

A **streamed** interpolation accepts a `float` and prints it the way CPython does for the
common cases — two decimals, rounded, with a trailing zero trimmed but never past the first
decimal (`3.25`, `-2.25`, `0.05`, `123.75` and `1234.5` all print exactly). `print(x)` on a
`float` uses the same formatter. The **value** form (`s = f"..."`) still has no float
lowering — stream it, or convert to a scaled integer first.

### `print()` of a buffer

`print()` renders a `bytearray`, a fixed-size array slice (`print(arr[a:b])`) and a slice of
an object with `__getitem__` / `__len__` (`print(obj[a:b])`) as the faithful CPython repr,
escapes and all:

```python
buf: bytearray = bytearray(b"\xcc\x10\xca\xfe")
print(buf)              # bytearray(b'\xcc\x10\xca\xfe')
print(buf[0:2])         # bytearray(b'\xcc\x10')
```

The length has to be a compile-time constant — the repr is unrolled into direct writes, so
`print(buf[0:n])` with a runtime `n` has nothing to unroll.

---

## Exception handling

`try / except / raise / finally` are **supported** on AVR and ARM (RP2040/RP2350) targets
via a zero-cost **T-flag error-propagation** model — *not* `setjmp` / `longjmp`. A function
that raises marks the error (AVR: the SREG T flag via `SET`/`CLT`/`BRTS`; ARM: an internal
flag + code global pair) and returns normally; every call site inside a `try` tests the
flag and branches to the matching `except`. There is no `jmp_buf` and no stack unwinding,
so the happy path costs a single skipped branch per guarded call.

Because propagation rides on the function return, raise from a helper and catch it where you
call that helper:

```python
def read_sensor(raw: uint16) -> uint8:
    if raw > 1000:
        raise ValueError        # sets the T flag, returns to the caller
    return uint8(raw)

try:
    v: uint8 = read_sensor(adc.read())   # caught here if read_sensor raised
    handle(v)
except ValueError:
    handle_error()
finally:
    cleanup()
```

`ValueError`, `TypeError`, `IndexError`, `KeyError`, `NotImplementedError` and
`ZeroDivisionError` are builtins — no import required, exactly like CPython.
`ZeroDivisionError` is raised automatically on a runtime `//` or `%` by zero.

The full statement is supported: `try` / `except` / **`else`** / **`finally`**, plus a bare
`raise` to re-raise the active exception. `finally` runs on **every** exit path — normal
completion, a caught exception, propagation to an outer scope, and `return` / `break` /
`continue` out of the `try` (including a `break` or `return` inside `finally` that discards
the in-flight exception):

```python
try:
    v: uint8 = read_sensor(adc.read())
except ValueError:
    handle_error()
    raise                # bare re-raise — propagates to the caller
else:
    handle(v)            # runs only if no exception
finally:
    cleanup()            # always runs
```

**How it works / limits:**

| Property | Notes |
|---|---|
| Zero SRAM, zero happy-path cost | No `jmp_buf`; each guarded call is followed by one `BRTS`, skipped when no error was raised |
| Propagates across calls | A `raise` inside a called function is caught at the call site in the caller's `try` — cross-function propagation **is** the model; there is no same-function restriction |
| Propagates to any depth | An unmatched exception re-propagates to the **enclosing** `try`, then the caller, and so on — there is no single-nesting-level limit |
| Caught at call sites | An exception is detected after a **function call** inside the `try`. Raise from a helper and catch it where you call it (rather than `raise`-ing directly in the `try` body) |
| AVR + ARM (RP2040/RP2350) | PIC and other backends: use return codes or sentinel values instead |
| Exception types are integer codes | Builtins (`ValueError` etc.); no message strings at runtime; handlers match by integer code |
| Unmatched at top level | An exception that reaches `main` with no handler hits `__pymcu_unhandled_exn` — `E:<TypeName>` to UART0 then a halt, never a silent continue |

:::{admonition} Return codes are still often clearer for firmware
:class: note

`try / except` is now zero-cost on the happy path (no `jmp_buf`, one skipped branch per
guarded call), so the old "21 bytes of SRAM per `try`" objection no longer applies. Even so,
an explicit status return is frequently the clearest bare-metal style, reads the same on
every backend (not just AVR), and makes the error path obvious at each call:

```python
# Idiomatic: zero SRAM overhead, works across any call depth
STATUS_OK:    uint8 = 0
STATUS_RANGE: uint8 = 1

def read_sensor() -> uint8:
    if adc.read() > 1000:
        return STATUS_RANGE
    return STATUS_OK

match read_sensor():
    case STATUS_OK:    ...
    case STATUS_RANGE: ...
```
| One type per handler | `except ValueError:`, without parentheses and without `as`. A raise carries the type code and nothing else, so there is no exception object for `except E as e:` to bind, and `except (A, B):` has no single code to compare. Both are refused by name, as is `except*` (exception groups) |
:::

**`CompileError` — compile-time intrinsic:**

`raise CompileError("msg")` is intercepted by the compiler and **aborts compilation** with a
`CompileError:` diagnostic. It never generates any runtime code or error-propagation
instruction. Used in all HAL modules to reject unsupported configurations at compile time:

```python
from pymcu.exceptions import CompileError

match __CHIP__.arch:
    case "avr":
        ...
    case _:
        raise CompileError("SPI not supported on this architecture")
```

`CompileError` **cannot be caught** by `try / except` — compilation aborts before any binary
is produced.

**Unhandled exception output (AVR with UART0):**

When a `raise` has no active `except` handler, PyMCU prints `"E:<TypeName>\r\n"` to UART0
(if initialized) then halts with `cli; rjmp .-2`. Useful for debugging from a serial monitor:

```
E:ValueError
```

Only exception types actually raised in the program have their name strings emitted in flash
— no overhead for unused exception codes. Chips without UART0 (attiny85 etc.) skip output
and go directly to the halt loop.

**Supported:** `assert condition, msg` as a compile-time check — a statically false assertion
is a `CompileError`; a true or runtime assertion is stripped.

---

## Functions and closures

| Feature | Why it fails | Alternative |
|---|---|---|
| Closures capturing mutable vars | Closure cell requires heap | Pass captured values as explicit parameters |
| `*args` / `**kwargs` | Variadic convention needs stack inspection | Fixed parameter lists |
| `functools.partial` | Runtime partial object | Wrapper `@inline` function |
| Higher-order functions (passing functions as values) | No function pointer type | `match / case` dispatch |
| Unbounded recursion | Stack overflow on MCU | Iterative equivalent |

**Supported:** `@inline` functions expand at call sites — zero call overhead, zero stack.

A **free function that takes a class instance** (`def blink_twice(led: Pin)`) is supported and
expands at the call site, the way an explicit `@inline` of the same shape does: the instance
fields live in the caller's frame, so there is no subroutine to call. The parameter must be
annotated with the class name; an unannotated one is not an instance parameter.
Non-`@inline` functions use a conventional call/ret ABI and can recurse to a fixed depth
(~80 frames on ATmega328P with 2 KB SRAM). `lambda x: expr` (no closure capture) is inlined
at the call site. `nonlocal` is supported inside nested `@inline` functions.

---

## Classes and inheritance

| Feature | Why it fails | Alternative |
|---|---|---|
| Multiple inheritance / MRO | C3 linearization is a runtime concept | Single-level inheritance only |
| Runtime polymorphism (vtable dispatch) | Requires vtable + heap class objects | Compile-time `match / case` dispatch |
| `isinstance()` / `type()` | No type tags at runtime | Not available |
| `__repr__`, `__str__` | No runtime string formatting | `uart.println()` with explicit fields |
| `dataclass` / `namedtuple` | Metaclass + runtime heap | Manual `@inline` class |

**Supported:** ZCA `@inline` classes (zero SRAM), `@property` / `@name.setter`,
single-level class inheritance with `super()`, `with obj:` context managers
(`__enter__`/`__exit__`), operator dunder methods (`__add__`, `__sub__`,
`__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, all comparison / bitwise
dunders). A class-typed field dispatches correctly through a **value-returning** method too
(`self.pin.read()` on a nested ZCA field), which is what the compat layers are built on —
`machine.Pin` wrapping the HAL `Pin` is exactly this shape.

---

## Type system limitations

| Feature | Why it fails | Alternative |
|---|---|---|
| `complex` numbers | Requires float | Not available |
| `Decimal` | Requires heap | Not available |
| `None` assigned to a scalar (`int` / `uintN`) | `None` is a real null literal, not the integer `-1` | Use a sentinel value (e.g. `0xFF`), or keep `None` for reference / optional-typed values where `is None` / `== None` checks work |
| `Optional[T]` at runtime | No heap, no runtime type tag | Sentinel value pattern |
| `Union` types | Runtime type tag required | Separate functions per type |
| `TypeVar` / `Generic` | Runtime generics | Separate `@inline` functions per type |

**Note on `float`:** Soft-float (IEEE 754 single-precision) is supported on AVR via a
pure-assembly helper library. Expect ~200-400 cycles per operation. Subnormals are treated as
zero; NaN and Inf propagate correctly. `uint32(x * 100.0 + 0.5)` and the other float→int
casts truncate toward zero on the real value, not on its raw bit pattern.

**Note on `const[T]`:** a `const[T]` parameter accepts compile-time **float** constants as
well as integers and strings, so `Timer(freq=2.5)` binds. What it does not accept is a value
that varies at runtime: passing one is a located `CompileError` naming the parameter, rather
than a silent fold of whatever the variable happened to hold. `Pin(n)` where `n` is a runtime
variable is the case you are most likely to hit — a pin identity has to be known at compile
time for the GPIO access to stay zero-cost.

---

## Pointer arithmetic

`ptr[T]` in PyMCU is a **compile-time constant address alias**, not a runtime pointer.
It is equivalent to a C volatile register macro:

```c
// C: compile-time constant pointer — what ptr[T] models
volatile uint8_t* const PINB = (volatile uint8_t*)0x36;
```

This means the following operations are **not supported**:

| Operation | Example | Why it fails |
|---|---|---|
| Pointer advance | `p = p + 1` | `ptr` has no runtime address value |
| Runtime **bit** index through a ptr variable | `p[i]` where `i` is a runtime variable | rejected with a clear error (constant-index bits and chip registers are fine) |
| Pointer difference | `p - q` | Not in IR |
| Bare assignment | `PORTB = 0xFF` | rebinds the name, never writes — the compiler rejects it; use `PORTB.value = 0xFF` |

The following, previously listed here as unsupported, **do work**:

- **`ptr` as a function parameter and return type** — `def f(reg: ptr[uint8])` and
  compile-time selectors returning `-> ptr[uint8]` are used throughout the HAL; a bare
  register name in those positions contributes its address.
- **Runtime-offset dereference** — `ptr(BASE + off).value` with a runtime `off`
  compiles to indirect loads/stores (register-base + runtime offset remains
  unsupported).

**Idiomatic alternative — fixed arrays with variable index:**

```python
buf: uint8[16] = [0] * 16
i: uint8 = 0
while i < 16:
    buf[i] = compute(i)   # compiles to: LDD / STD with Y+offset
    i = i + 1
```

`uint8[N]` arrays with a runtime index already compile to efficient `ld`/`st` with
Y+offset addressing on AVR — no pointer arithmetic needed.

**For performance-critical pointer walks in asm:** use the Z register (`r30:r31`)
with `ld r24, Z+` / `st Z+, r24` for auto-increment through a buffer.

```python
asm("""
ldi  r30, lo8(my_buf)
ldi  r31, hi8(my_buf)
ldi  r18, 16          ; length
_loop:
    ld   r24, Z+      ; load byte and advance pointer
    ...
    dec  r18
    brne _loop
""")
```

---

## Iterators and comprehensions

| Feature | Why it fails | Alternative |
|---|---|---|
| List comprehension over a **runtime** iterable | Length not known at compile time | `for` loop with fixed-size array |
| `if`-filtered comprehension with a **runtime** condition | The result length would vary at runtime | Keep the filter compile-time constant, or `for` loop + explicit index |
| Runtime tuples | A tuple is a compile-time construct here | Separate variables, or a fixed-size array |
| Dict comprehension | Heap allocation | Not available |
| Set comprehension | Heap allocation | Not available |
| Generator expressions | Coroutine frame requires heap | A `yield` generator function (supported — see Async and concurrency) |
| `map()` / `filter()` with runtime iterables | Lazy iterator requires heap | Explicit `for` loop |

**Supported:** `for i in range(N)` (runtime or constant N), `for x in array`,
`for x in [...]`, `for i, x in enumerate(iterable)`, `for x, y in zip(list1, list2)`,
`for x in reversed([...])`, list comprehensions with compile-time constant bounds,
nested list comprehensions, `if`-filtered list comprehensions (constant condition),
`for pin in [DigitalInOut(p) for p in (...)]` and
`for bit, pin in enumerate([DigitalInOut(p) for p in (...)])` (CT unroll of ZCA instance arrays).

### Slices

| Form | Status |
|---|---|
| `b = arr[1:3]` / `arr[::2]` (slice **read**) | Compile-time constant bounds only — the result is a fixed-size array sized at compile time |
| `arr[a:b] = src` (slice **assignment**) | Supported, equal length, from a list / `bytes` literal / array / slice, including overlapping copies of the same array (snapshot semantics) |
| `obj[a:b] = src` through `__setitem__` | Supported — lowers to one `__setitem__` call per byte |
| `for x in buf[lo:hi]` (slice **iteration**) | Supported with **runtime** bounds; rewritten to a `range` loop over the backing array |
| `for x in buf[lo:hi:step]` with a runtime `step` | Rejected with a diagnostic — the step has to be a compile-time constant |

A slice *read* with runtime bounds (`b = buf[0:n]`) has no lowering: the result would need a
runtime-sized array. Iterate it instead, or index the backing array directly.

The `__setitem__` form is what makes the canonical CircuitPython persistence pattern compile:

```python
import microcontroller

microcontroller.nvm[0:4] = b"\xcc\x10\xca\xfe"   # one byte-write per element
```

---

## Async and concurrency

| Feature | Why it fails | Alternative |
|---|---|---|
| Awaiting another coroutine/future, `await` as an expression | Sub-future fields need ZCA construction outside `__init__` (not supported yet) | Call the coroutine and poll it, or restructure with asyncio.gather |
| `threading` / `multiprocessing` | OS required | `@interrupt` ISRs |

**Supported:** `async def` / `await` (compiled to a zero-cost state machine; requires
`import asyncio`; `await asyncio.sleep/sleep_ms` anywhere — if/elif/else, `while <cond>`,
`for i in range(...)`, break/continue, `return expr` via `._value`; executors
`asyncio.run` / `asyncio.gather`),
`@interrupt` decorator for hardware ISRs, `Pin.irq(trigger, handler)` for external pin
interrupts, atomic flag patterns via `GPIOR0`.

:::{admonition} Timer0 and millis / ticks_ms
:class: warning

`millis_init()` (auto-injected when `ticks_ms()` — or, on ATmega, an `async def` — is
detected) configures **Timer0** in normal overflow mode.  Do **not** use Timer0 for
PWM, CTC, or other purposes when `ticks_ms()` / `millis()` / `async`-`await` is active
in the same program.

On AVR the clock `await asyncio.sleep_ms(...)` waits against is that same Timer0
counter, so its resolution is **4 µs at 16 MHz** (1 µs on RP2040/RP2350, which have a
hardware microsecond timer).  On architectures with no time base — PIC, RISC-V —
`asyncio.ticks()` is 0 and an `await` never completes.

A Timer0 overflow is 1024 µs, not 1000 µs.  `millis()` — and everything layered on it:
`ticks_ms()`, `time.monotonic()`, `supervisor.ticks_ms()` — carries the Arduino-style
fractional correction (1 ms per overflow plus 3/125 accumulated in eighths), so it counts
real milliseconds rather than running 2.4% slow.  `micros()` reads the raw overflow count
plus `TCNT0` and is monotonic across an overflow.

`delay_ms()` and `delay_us()` are unaffected — they use a software busy-loop with no
hardware timer dependency.
:::

---

## Imports and modules

| Feature | Why it fails | Alternative |
|---|---|---|
| Third-party PyPI packages | Only `pymcu` stdlib is compiled | Implement in `pymcu` stdlib or use `@extern` |
| `importlib` / dynamic imports | Runtime module loading | Not available |
| Circular imports | Not supported | Restructure module dependencies |
| A function defined twice in one module | PyMCU compiles the first, Python binds the last | Rename one, or make every definition `@inline` with different parameter types |

**Supported:** `import foo`, `from foo import Bar`, `from foo import Bar as B`,
`from foo import *`, relative imports (`from .util import half`, `from . import util`),
multi-module projects, `pymcu` stdlib, `pymcu-circuitpython` and `pymcu-micropython`
compat packages.

`from foo import *` binds the public top-level names of `foo`: its functions, classes and
module-level variables, minus the ones whose name starts with `_`, which are private and
which a star never binds in CPython either. A module that declares `__all__` gets exactly
that list instead. A name `foo` re-exports (one it imported itself) resolves through the
star as well.

A module-level object in an imported module is constructed at startup, before the entry
file's own module-level statements, in the order the modules are imported. This applies to
the project's own modules, the ones under `sources`. An installed distribution (the `pymcu`
stdlib and the compat layers) is written knowing that only the entry file's module level
runs, and several guard their top level on the target chip, so theirs is deliberately left
alone.

---

## Built-ins summary

| Built-in | Status | Notes |
|---|---|---|
| `print(str)` / `print(int)` | ✅ Supported | Routes to UART |
| `print(float)` | ✅ Supported | Two rounded decimals, trailing zero trimmed (`3.25`, `1234.5`) |
| `print(bytearray)` / `print(arr[a:b])` | ✅ Supported | CPython repr — `bytearray(b'\xcc\x10')`; length must be compile-time |
| `range(n)` | ✅ Supported | For-loop bounds; runtime or constant |
| `len(arr)` / `len(b"...")` | ✅ Supported | Compile-time constant fold |
| `abs(x)` | ✅ Supported | Intrinsic |
| `min(a, b)` / `max(a, b)` | ✅ Supported | Intrinsic. Also over a fixed-size array, and with `key=f`: the key is called once per operand and the winner is the original value, not its key |
| `sum(iterable)` | ✅ Supported | Compile-time fold or unrolled additions |
| `enumerate(iterable)` | ✅ Supported | Compile-time index counter |
| `zip(a, b)` | ✅ Supported | Compile-time unroll over constant lists |
| `reversed(iterable)` | ✅ Supported | Compile-time reverse unroll |
| `any(iterable)` / `all(iterable)` | ✅ Supported | Compile-time fold |
| `divmod(a, b)` | ✅ Supported | Compile-time or runtime |
| `pow(x, n)` / `x ** n` | ✅ Supported | Compile-time constant fold |
| `hex(n)` / `bin(n)` | ✅ Supported | Compile-time only |
| `str(n)` | ✅ Supported | Compile-time only |
| `ord('A')` / `chr(n)` | ✅ Supported | Compile-time constant only |
| `int.from_bytes(b, e)` | ✅ Supported | Compile-time fold or runtime |
| `sorted()` | ❌ Not supported | No dynamic allocation |
| `map()` / `filter()` | ❌ Not supported | Use explicit `for` loops |
| `input()` | ✅ Supported | `line: bytearray = input("prompt")` — reads until newline from UART; prompt is optional compile-time string; max length is optional integer (default 64); UART preamble auto-injected |
| `open()` / file I/O | ❌ Not supported | No filesystem |
| `exec()` / `eval()` | ❌ Not supported | Interpreter required |

---

## Platform notes (ATmega328P / Arduino Uno)

- **Stack depth:** ~80 nested non-inline calls before overflow (2 KB SRAM, ~16 bytes/frame).
  Use `@inline` for leaf helpers.
- **Soft float:** `float` variables and arithmetic are supported via a pure-assembly
  soft-float library. No FPU required. ~200-400 cycles per operation.
- **No heap:** every variable must have a size known at compile time.
- **String literals are in flash:** read-only; sent to UART via flash string pool. Cannot be
  compared, indexed, or modified at runtime.
- **C/C++ interop:** supported via `@extern` and `[tool.pymcu.ffi]` in `pyproject.toml`.
  C sources use `avr-gcc`; C++ sources (`.cpp`/`.cc`/`.cxx`) use `avr-g++`
  with `-fno-exceptions -fno-rtti`, enabling use of Arduino libraries.
- **Capacity is checked at build time, not at flash time:** an image larger than the chip's
  flash fails the build with the exact overage (`firmware is 32864 bytes but atmega328p has
  32768 bytes of flash (96 bytes over)`), and static data that does not fit in SRAM fails in
  the backend with the same shape (`static data needs 2700 bytes but atmega328p has 2048
  bytes of SRAM`). The SRAM check reserves 64 bytes for the hardware call stack, which grows
  down into the same space.

## Platform notes (RP2040 / Raspberry Pi Pico) — alpha

The RP2040 backend lowers PyMCU's IR to **LLVM IR** (target `thumbv6m-none-eabi`)
rather than emitting assembly directly, so LLVM does register allocation, instruction
selection and optimization. `pymcu build` emits a flat flash image (`firmware.bin`,
with the stage-2 boot loader at offset 0). It is **alpha** and intentionally limited:

- **MVP peripherals only:** GPIO (`pymcu.hal.gpio.Pin`, via single-cycle IO) and
  UART0 (`pymcu.hal.uart.UART`, PL011) are supported. SPI, I2C, PWM, ADC, PIO, USB,
  timers, EEPROM/flash and the watchdog are **not** wired up on this backend yet.
- **Single core:** only core 0 runs. Dual-core launch and the SIO FIFO are not
  exposed.
- **No GC / exceptions / soft-float yet:** `list[T]`, `try/except/raise`, and `float`
  arithmetic compile on AVR but are **not supported** on the RP2040 backend — the
  codegen rejects the corresponding IR with a clear "not supported yet" error.
  Virtual-method dispatch, runtime-indexed arrays and operand-form inline `asm()` are
  likewise deferred.
- **Delays:** `delay_ms` / `delay_us` poll the hardware **TIMER** (the
  free-running 1 MHz microsecond counter), so timing is accurate on real silicon
  regardless of CPU clock and pipeline, not a calibrated busy-loop. In the
  emulator the wall-clock measured by `RunMilliseconds` reads the wait slightly
  short, because that harness budgets execution by retired instruction count
  while the timer advances by elapsed cycles — the firmware delay itself is
  exact.
- **UART clock assumption:** the baud divisors assume `clk_peri = 125 MHz`
  (`clk_sys` at the pico-sdk default). A configurable clocks HAL is future work.
- **Toolchain:** the backend ships in the `pymcu-arm` package (`pip install pymcu-arm`),
  which registers the `rp2040` target. It requires **LLVM** (`opt`, `llc`, `llvm-mc`,
  `ld.lld`, `llvm-objcopy`) on the host, provided by the
  [`pymcu-arm-toolchain`](https://github.com/PyMCU/pymcu-arm) wheel (analogous to
  `pymcu-avr-toolchain`). If the wheel is not available for your platform the toolchain
  falls back to a system LLVM (e.g. `brew install llvm lld`).
- **No C/C++ interop (`@extern`) yet** on this backend.