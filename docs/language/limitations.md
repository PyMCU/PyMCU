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
| `dict` | Hash table requires heap | `match / case` key dispatch |
| `set` | Hash set requires heap | `uint8` bitmask |

**Supported:** `list[T]` (`x: list[uint8] = list()`) compiles to a bounded bump-allocator
with GC; supports `append()`, `len()`, `x[i]`, `for v in x:`.
`bytearray(N)` and `bytearray(b"...")` compile to SRAM `uint8[N]` arrays.
Fixed-size arrays `arr: uint8[N]` support both constant- and variable-index access.

**Rule of thumb:** if the size is not known at compile time, it cannot be compiled.

---

## String operations

| Feature | Why it fails | Alternative |
|---|---|---|
| `f"..."` assigned to a string **variable** | No runtime string object to hold the result | Stream it directly: `print(f"...")` / `uart.write_str(f"...")` |
| `str.split()`, `str.join()`, `str.format()` | Heap strings | Not available |
| `len(string_variable)` | Runtime string object required | Use fixed-size buffers |
| `str + str` concatenation | Heap allocation | Separate `uart.write_str()` calls |
| `str[i]` on a runtime string | No runtime string object | Use `const[str]` parameters |

**Supported:** String literals in flash, raw strings `r"\n"`, `uart.println("literal")`,
`for ch in "ABC":` (compile-time unroll), `const[str]` runtime subscript (reads byte from
flash), and **runtime f-strings streamed directly to a sink** — see below.

### f-strings (streamed)

`f"..."` with **runtime interpolations** is supported as long as the result goes straight to
a stream rather than into a string variable. The compiler lowers each piece to a direct
write — no heap, no format buffer:

```python
print(f"adc={raw} v={mv:04d}")
uart.write_str(f"t={temp:5d}")
uart.println(f"err 0x{code:02X}")
lcd.print_str(f"{hours:02d}:{mins:02d}")
```

**Format specs** supported in interpolations: `{x:02x}`, `{x:X}`, `{x:08b}`, `{x:o}`,
`{x:5d}`, `{x:04d}` (width, zero-pad, and `x`/`X`/`b`/`o`/`d` bases). Compile-time constant
interpolations (`f"text={const}"`) are folded into the flash string as before.

---

## Exception handling

`try / except / raise / finally` are **supported** on AVR targets via a zero-cost
**T-flag error-propagation** model (the `SET` / `CLT` / `BRTS` ABI) — *not* `setjmp` /
`longjmp`. A function that raises sets the AVR T flag and returns normally; every call site
inside a `try` tests the flag and branches to the matching `except`. There is no `jmp_buf`
and no stack unwinding, so the happy path costs a single skipped branch per guarded call.

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
| AVR only | PIC and other backends: use return codes or sentinel values instead |
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
(`__enter__`/`__exit__`), `@staticmethod`, operator dunder methods (`__add__`, `__sub__`,
`__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, all comparison / bitwise
dunders).

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
zero; NaN and Inf propagate correctly.

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
| Variable-index dereference | `p[i]` where `i` is a runtime variable | `ptr` address is baked in at compile time |
| Pointer as function parameter | `def f(p: ptr[uint8])` | No `ptr` variable type in ABI |
| Pointer difference | `p - q` | Not in IR |

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

---

## Iterators and comprehensions

| Feature | Why it fails | Alternative |
|---|---|---|
| List comprehension over a **runtime** iterable | Length not known at compile time | `for` loop with fixed-size array |
| Dict comprehension | Heap allocation | Not available |
| Set comprehension | Heap allocation | Not available |
| Generator expressions / `yield` | Coroutine frame requires heap | Not available |
| `map()` / `filter()` with runtime iterables | Lazy iterator requires heap | Explicit `for` loop |

**Supported:** `for i in range(N)` (runtime or constant N), `for x in array`,
`for x in [...]`, `for i, x in enumerate(iterable)`, `for x, y in zip(list1, list2)`,
`for x in reversed([...])`, list comprehensions with compile-time constant bounds,
nested list comprehensions, `if`-filtered list comprehensions,
`for pin in [DigitalInOut(p) for p in (...)]` and
`for bit, pin in enumerate([DigitalInOut(p) for p in (...)])` (CT unroll of ZCA instance arrays).

---

## Async and concurrency

| Feature | Why it fails | Alternative |
|---|---|---|
| `async def` / `await` | Async runtime / event loop required | `@interrupt` ISRs + polling loop |
| `asyncio` | Not available | Not available |
| `threading` / `multiprocessing` | OS required | `@interrupt` ISRs |

**Supported:** `@interrupt` decorator for hardware ISRs, `Pin.irq(trigger, handler)` for
external pin interrupts, atomic flag patterns via `GPIOR0`.

:::{admonition} Timer0 and millis / ticks_ms
:class: warning

`millis_init()` (auto-injected when `ticks_ms()` is detected) configures **Timer0** in
normal overflow mode.  Do **not** use Timer0 for PWM, CTC, or other purposes when
`ticks_ms()` / `millis()` is active in the same program.

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

**Supported:** `import foo`, `from foo import Bar`, `from foo import Bar as B`,
relative imports, multi-module projects, `pymcu` stdlib, `pymcu-circuitpython` and
`pymcu-micropython` compat packages.

---

## Built-ins summary

| Built-in | Status | Notes |
|---|---|---|
| `print(str)` / `print(int)` | ✅ Supported | Routes to UART |
| `range(n)` | ✅ Supported | For-loop bounds; runtime or constant |
| `len(arr)` / `len(b"...")` | ✅ Supported | Compile-time constant fold |
| `abs(x)` | ✅ Supported | Intrinsic |
| `min(a, b)` / `max(a, b)` | ✅ Supported | Intrinsic |
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