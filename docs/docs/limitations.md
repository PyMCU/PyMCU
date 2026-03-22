# Whipsnake Language Limitations

Whipsnake compiles a **statically-typed, allocation-free subset of Python** to bare-metal MCU machine
code. There is no runtime, no heap, no garbage collector, and no interpreter. Many standard Python
features are therefore incompatible with this model.

This document lists every known unsupported feature, explains *why* it cannot be compiled, and
suggests the idiomatic Whipsnake alternative where one exists.

---

## Dynamic Memory / Containers

| Feature | Why it fails | Alternative |
|---|---|---|
| `list` as a dynamic container (`lst.append(x)`) | Heap allocation required; no `malloc` | `uint8[N]` fixed-size array |
| `dict` | Hash table requires heap | `match / case` for key dispatch |
| `set` | Hash set requires heap | `uint8` bitmask |

**Supported:** `bytearray(N)` and `bytearray(b"...")` are compiled to SRAM `uint8[N]` arrays.
Fixed-size arrays `arr: uint8[N]` are fully supported with constant- and variable-index access.

**Rule of thumb:** if the size is not known at compile time, it cannot be compiled.

---

## String Operations

| Feature | Why it fails | Alternative |
|---|---|---|
| `f"prefix {value}"` at runtime | Requires heap format buffer | `uart.write_str("prefix"); uart.write(value)` |
| `str.split()`, `str.join()`, `str.format()` | Heap strings | Not available |
| `len(string_variable)` | Runtime string object required | Use fixed-size buffers |
| `str + str` concatenation | Heap allocation | Separate `uart.write_str()` / `uart.println()` calls |
| `str[i]` indexing on a runtime string | No runtime string object | Iterate compile-time literals: `for ch in "literal":` |

**Supported:** String literals in flash, raw string literals `r"\n"` (no escape processing),
`uart.println("literal")`, `uart.write_str("text")`, `for ch in "ABC":` (compile-time unroll),
`f"text={const}"` where all interpolations are compile-time constants.

---

## Exception Handling

| Feature | Why it fails | Alternative |
|---|---|---|
| `try / except` | Exception table + unwinding stack required | Return error codes; use `match/case` |
| `raise` (runtime) | No exception runtime | `return ERROR_CODE`; compile-time `raise` is supported |
| `finally` | Requires exception unwinding | Restructure control flow |

**Supported:** `assert condition, msg` is supported as a compile-time check — a statically false
assertion is a CompileError; a true or runtime assertion is stripped.

---

## Functions and Closures

| Feature | Why it fails | Alternative |
|---|---|---|
| Closures capturing mutable vars (`def inner():`) | Closure cell requires heap | Pass captured values as explicit parameters; use `nonlocal` in `@inline` nested functions |
| `*args` / `**kwargs` | Variadic convention needs stack inspection | Fixed parameter lists |
| `functools.partial` | Runtime partial object | Wrapper `@inline` function |
| Higher-order functions (passing functions as values) | No function pointer type | `match / case` dispatch table |
| Recursion (unbounded depth) | Stack overflow undefined on MCU | Iterative equivalent; fixed-depth allowed |

**Supported:** `@inline` functions expand at call sites — zero call overhead, zero stack.
Non-`@inline` functions use a conventional call/ret ABI and can recurse up to the stack
depth limit (~80 frames on ATmega328P with 2KB SRAM).
`lambda x: expr` (no closure capture) is supported and inlined at the call site.
`nonlocal` is supported inside nested `@inline` functions (one level of nesting).

---

## Classes and Inheritance

| Feature | Why it fails | Alternative |
|---|---|---|
| Multiple inheritance / MRO | C3 linearization is a runtime concept | Single-level inheritance only |
| Runtime polymorphism (vtable dispatch) | Requires vtable + heap class objects | Compile-time `match / case` dispatch |
| `isinstance()` / `type()` | No type tags at runtime | Not available |
| Abstract base classes | ABC machinery requires runtime | Document the interface convention |
| `__repr__`, `__str__` | No runtime string formatting | `uart.println()` with explicit fields |
| `dataclass` / `namedtuple` | Metaclass + runtime heap | Manual `@inline` class |
| Abstract base classes | ABC machinery requires runtime | Document the interface convention |

**Supported:** ZCA `@inline` classes (zero SRAM), `@property` / `@name.setter`, single-level
class inheritance with `super()`, `with obj:` context managers (`__enter__`/`__exit__`),
`@staticmethod` (silently treated as a module-level function), operator dunder methods
(`__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`,
and all comparison / bitwise dunders).

---

## Types and Type System

| Feature | Why it fails | Alternative |
|---|---|---|
| `float` arithmetic (native) | No FPU on AVR; soft-float not yet implemented | Fixed-point `int16` with manual scaling (`fixed16` planned v0.8) |
| `complex` numbers | Requires float | Not available |
| `Decimal` | Requires heap | Not available |
| `None` as a runtime-checked value | Folds to `Constant{-1}` at compile time | Use a sentinel value (e.g. `0xFF`) |
| `Optional[T]` at runtime | No heap, no runtime type tag | Sentinel value pattern |
| `Union` types | Runtime type tag required | Separate functions per type |
| `TypeVar` / `Generic` | Runtime generics | Separate `@inline` functions per type |

**Supported:** `uint8`, `uint16`, `uint32`, `int8`, `int16`, `int32`, `bool` (as `uint8`),
fixed-size arrays `uint8[N]`, `bytearray`, `bytes` literal `b"..."`, tuple literals and
tuple unpacking for multi-return functions.

---

## Iterators and Comprehensions

| Feature | Why it fails | Alternative |
|---|---|---|
| List comprehension over a **runtime** iterable | Length not known at compile time | Use a `for` loop with a fixed-size array |
| List comprehension with **runtime** bounds | Cannot unroll variable-length comprehension | Compile-time constant bounds work: `[i*2 for i in range(10)]` |
| Dict comprehension | Heap allocation | Not available |
| Set comprehension | Heap allocation | Not available |
| Generator expressions | Coroutine frame required | Not available |
| `yield` / `yield from` | Generator state requires heap | Not available |
| `map()` / `filter()` with runtime iterables | Lazy iterator requires heap | Explicit `for` loop |

**Supported:** `for i in range(N)` (runtime or constant N), `for x in array`, `for x in [...]`,
`for x in b"..."`, `for i, x in enumerate(iterable)`, `for x, y in zip(list1, list2)`,
`for x in reversed([...])`, list comprehensions with compile-time constant bounds,
nested list comprehensions, `if`-filtered list comprehensions.

---

## Async / Concurrency

| Feature | Why it fails | Alternative |
|---|---|---|
| `async def` / `await` | Async runtime / event loop required | `@interrupt` ISRs + polling loop |
| `asyncio` | Not available | Not available |
| `threading` / `multiprocessing` | OS required | `@interrupt` ISRs |
| `concurrent.futures` | OS required | Not available |

**Supported:** `@interrupt` decorator for hardware ISRs, `Pin.irq(trigger, handler)` for
external pin interrupts, atomic flag patterns via `GPIOR0`.

---

## Imports and Modules

| Feature | Why it fails | Alternative |
|---|---|---|
| Third-party PyPI packages | Only pymcu stdlib is compiled | Implement equivalent in pymcu stdlib |
| `importlib` / dynamic imports | Runtime module loading | Not available |
| Circular imports | Not supported | Restructure module dependencies |
| `__all__` / `__init__.py` re-exports (at runtime) | No runtime | Place all symbols in the module file directly |

**Supported:** `import foo`, `from foo import Bar`, `from foo import Bar as B` (aliased imports),
relative imports, multi-module projects, `pymcu` stdlib, `whipsnake-circuitpython` and
`whipsnake-micropython` compat packages.

---

## Built-ins

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
| `any(iterable)` / `all(iterable)` | ✅ Supported | Compile-time fold or OR/AND chain |
| `divmod(a, b)` | ✅ Supported | Compile-time or runtime via `__div8`/`__mod8` |
| `pow(x, n)` / `x ** n` | ✅ Supported | Compile-time constant fold |
| `hex(n)` / `bin(n)` | ✅ Supported | Compile-time: `hex(255)` → `"0xff"` |
| `str(n)` | ✅ Supported | Compile-time: `str(42)` → `"42"` |
| `ord('A')` / `chr(n)` | ✅ Supported | Compile-time constant only |
| `int.from_bytes(b, e)` | ✅ Supported | Compile-time fold or runtime `(hi<<8)|lo` |
| `sorted()` | ❌ Not supported | No dynamic allocation |
| `map()` / `filter()` | ❌ Not supported | Explicit `for` loops |
| `input()` | ❌ Not supported | `uart.read()` or `uart.read_blocking()` |
| `open()` / file I/O | ❌ Not supported | No filesystem |
| `id()` | ❌ Not supported | Not applicable on MCU |
| `globals()` / `locals()` | ❌ Not supported | Not applicable |
| `exec()` / `eval()` | ❌ Not supported | Interpreter required |

---

## Platform-Specific Notes (ATmega328P / Arduino Uno)

- **Stack depth:** ~80 nested non-inline calls before stack overflow (2KB SRAM, ~16 bytes/frame).
  Use `@inline` for leaf helpers to avoid stack frames.
- **No soft float yet:** Use integer arithmetic and manual fixed-point scaling. `fixed16` (Q8.8)
  is planned for v0.8.
- **No heap:** Every variable must have a size known at compile time.
- **Interrupts and globals:** Mutable globals written from an ISR must be declared `global` inside
  the ISR and accessed atomically (disable interrupts or use `GPIOR0` flag pattern).
- **String literals are in flash:** Read-only; can only be sent to UART via the flash string pool.
  They cannot be compared, indexed, or modified at runtime.
- **C/C++ interop:** Supported via `@extern` and `[tool.whip.ffi]` in `pyproject.toml`.
  C sources use `avr-gcc`; C++ sources (`.cpp`/`.cc`/`.cxx`) use `avr-g++` with
  `-fno-exceptions -fno-rtti`, enabling use of Arduino libraries.

---

## Getting Help

If you hit a compile error on a Python construct not covered here, please open an issue at the
Whipsnake repository. Include the source snippet and the compiler error message.

For feature requests, see the [roadmap](roadmap.md).
