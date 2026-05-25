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
| `list.append(x)` | Heap allocation required | `uint8[N]` fixed-size array |
| `dict` | Hash table requires heap | `match / case` key dispatch |
| `set` | Hash set requires heap | `uint8` bitmask |

**Supported:** `bytearray(N)` and `bytearray(b"...")` compile to SRAM `uint8[N]` arrays.
Fixed-size arrays `arr: uint8[N]` support both constant- and variable-index access.

**Rule of thumb:** if the size is not known at compile time, it cannot be compiled.

---

## String operations

| Feature | Why it fails | Alternative |
|---|---|---|
| `f"prefix {value}"` at runtime | Requires a heap format buffer | `uart.write_str("prefix"); uart.write(value)` |
| `str.split()`, `str.join()`, `str.format()` | Heap strings | Not available |
| `len(string_variable)` | Runtime string object required | Use fixed-size buffers |
| `str + str` concatenation | Heap allocation | Separate `uart.write_str()` calls |
| `str[i]` on a runtime string | No runtime string object | Use `const[str]` parameters |

**Supported:** String literals in flash, raw strings `r"\n"`, `uart.println("literal")`,
`for ch in "ABC":` (compile-time unroll), `f"text={const}"` where all interpolations are
compile-time constants, `const[str]` runtime subscript (reads byte from flash).

---

## Exception handling

| Feature | Why it fails | Alternative |
|---|---|---|
| `try / except` | Exception table + unwinding stack | Return error codes; `match/case` |
| `raise` (runtime) | No exception runtime | `return ERROR_CODE` |
| `finally` | Requires exception unwinding | Restructure control flow |

**Supported:** `assert condition, msg` as a compile-time check — a statically false assertion
is a `CompileError`; a true or runtime assertion is stripped. Compile-time `raise` is supported.

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
| `None` as a runtime-checked value | Folds to `Constant{-1}` | Use a sentinel value (e.g. `0xFF`) |
| `Optional[T]` at runtime | No heap, no runtime type tag | Sentinel value pattern |
| `Union` types | Runtime type tag required | Separate functions per type |
| `TypeVar` / `Generic` | Runtime generics | Separate `@inline` functions per type |

**Note on `float`:** Soft-float (IEEE 754 single-precision) is supported on AVR via a
pure-assembly helper library. Expect ~200-400 cycles per operation. Subnormals are treated as
zero; NaN and Inf propagate correctly.

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