# PyMCU Language Limitations

PyMCU compiles a **statically-typed, allocation-free subset of Python** to bare-metal MCU machine
code. There is no runtime, no heap, no garbage collector, and no interpreter. Many standard Python
features are therefore incompatible with this model.

This document lists every known unsupported feature, explains *why* it cannot be compiled, and
suggests the idiomatic PyMCU alternative where one exists.

---

## Dynamic Memory / Containers

| Feature | Why it fails | Alternative |
|---|---|---|
| `list` as a dynamic container (`lst.append(x)`) | Heap allocation required; no `malloc` | `uint8[N]` fixed-size array |
| `dict` | Hash table requires heap | `match / case` for key dispatch |
| `set` | Hash set requires heap | `uint8` bitmask |
| `bytearray` as mutable buffer | Dynamic allocation | `uint8[N]` array |
| `bytes` object operations | Runtime length + allocation | Flash string pool (`__str_N`) |
| `str` as a mutable or runtime value | No heap string buffer | String literals in flash only |

**Rule of thumb:** if the size isn't known at compile time, it cannot be compiled.

---

## String Operations

| Feature | Why it fails | Alternative |
|---|---|---|
| `f"prefix {value}"` at runtime | Requires heap format buffer | `uart.write(value)` + `print("prefix")` |
| `str.split()`, `str.join()`, `str.format()` | Heap strings | Not available |
| `len(string_variable)` | Runtime string object required | Avoid; use fixed-size buffers |
| `str + str` concatenation | Heap allocation | Separate `print()` calls |
| `str[i]` indexing on a runtime string | No runtime string object | Flash-only strings can be iterated at compile time via `for ch in "literal"` |

**Supported:** String literals in flash, `print("literal")`, `uart.write(byte)`, `for ch in "literal"` (compile-time unroll).

---

## Exception Handling

| Feature | Why it fails | Alternative |
|---|---|---|
| `try / except` | Exception table + unwinding stack required | Return error codes; use match/case |
| `raise` (runtime) | No exception runtime | `return ERROR_CODE`; compile-time `raise` is supported |
| `assert` | Raises `AssertionError` at runtime | Not yet available (planned T2.3 for compile-time validation) |
| `finally` | Requires exception unwinding | Restructure control flow |
| `with` (context managers) | Requires `__enter__`/`__exit__` protocol | Not yet available (planned T2.2 for inline expansion) |

---

## Functions and Closures

| Feature | Why it fails | Alternative |
|---|---|---|
| `lambda` | Closure capture requires heap | Inline helper function |
| Closures (`def inner():` capturing outer vars) | Closure cell requires heap | Pass captured values as explicit parameters |
| `*args` / `**kwargs` | Variadic convention needs stack inspection | Fixed parameter lists |
| `functools.partial` | Runtime partial object | Wrapper `@inline` function |
| Higher-order functions (passing functions as values) | No function pointer type | `match / case` dispatch table |
| Recursion (unbounded depth) | Stack overflow undefined on MCU | Iterative equivalent; fixed-depth allowed |
| `nonlocal` | Requires closure cell | Not available |
| Nested `def` inside a function | Closure capture required | Module-level or class `@inline` |

**Supported:** `@inline` functions are expanded at call sites — zero call overhead, zero stack.
Non-`@inline` functions use a conventional call/ret ABI and can call themselves recursively up
to the stack depth limit (~80 frames on ATmega328P with default stack).

---

## Classes and Inheritance

| Feature | Why it fails | Alternative |
|---|---|---|
| Multiple inheritance / MRO | C3 linearization is a runtime concept | Single-level inheritance only |
| Runtime polymorphism (vtable dispatch) | Requires vtable + heap class objects | Compile-time `match / case` dispatch |
| `isinstance()` / `type()` | No type tags at runtime | Not available |
| `__dunder__` magic methods beyond `__init__` | Operator overloading requires dispatch | Explicit `@inline` helpers |
| `__repr__`, `__str__` | No runtime string formatting | `print()` with explicit fields |
| `classmethod` / `staticmethod` | Runtime descriptor protocol | Module-level `@inline` function |
| `dataclass` / `namedtuple` | Metaclass + runtime heap | Manual `@inline` class |
| Abstract base classes | ABC machinery requires runtime | Document the interface convention |

**Supported:** ZCA `@inline` classes (zero SRAM), `@property` / `@name.setter`, single-level
class inheritance, method resolution on the inlined class only.

---

## Types and Type System

| Feature | Why it fails | Alternative |
|---|---|---|
| `float` arithmetic (native) | No FPU on AVR; soft-float not yet implemented | Fixed-point `int16` with manual scaling (soft float planned for Phase 3) |
| `complex` numbers | Requires float | Not available |
| `Decimal` | Requires heap | Not available |
| `None` as a value (runtime check) | Folds to `Constant{-1}` at compile time | Use a sentinel value (e.g. `0xFF`) or `None` literal |
| `Optional[T]` at runtime | No heap, no runtime type tag | Sentinel value pattern |
| `Union` types | Runtime type tag required | Separate functions per type |
| `TypeVar` / `Generic` | Runtime generics | Separate `@inline` functions per type |

**Supported:** `uint8`, `uint16`, `uint32`, `int8`, `int16`, `int32`, `bool` (as `uint8`),
fixed-size arrays `uint8[N]`, tuple literals and tuple unpacking for multi-return functions.

---

## Iterators and Comprehensions

| Feature | Why it fails | Alternative |
|---|---|---|
| List comprehension over **runtime** iterable | Length not known at compile time | Use a `for` loop with a fixed-size array |
| List comprehension with **runtime** bounds | Cannot unroll variable-length comprehension | Compile-time constant comprehensions work: `[i*2 for i in range(10)]` |
| Dict comprehension | Heap allocation | Not available |
| Set comprehension | Heap allocation | Not available |
| Generator expressions | Coroutine frame required | Not available |
| `yield` / `yield from` | Generator state requires heap | Not available |
| `zip()`, `map()`, `filter()` with runtime iterables | Lazy iterator requires heap | Unrolled `for` loops |
| `range()` with non-constant bounds in comprehension | Cannot unroll | `for i in range(n):` loop (n can be runtime) |

**Supported:** `for i in range(N)` loop with runtime or constant N, `for x in array` over
fixed-size arrays, `for i, x in enumerate(iterable)` with compile-time index counter, list
comprehensions with compile-time constant bounds (e.g., `[i*2 for i in range(10)]`).

---

## Async / Concurrency

| Feature | Why it fails | Alternative |
|---|---|---|
| `async def` / `await` | Async runtime / event loop required | `@interrupt` ISRs + polling loop |
| `asyncio` | Not available | Not available |
| `threading` / `multiprocessing` | OS required | `@interrupt` ISRs |
| `concurrent.futures` | OS required | Not available |

**Supported:** `@interrupt` decorator for hardware ISRs, atomic flag patterns via `GPIOR0`.

---

## Imports and Modules

| Feature | Why it fails | Alternative |
|---|---|---|
| Third-party PyPI packages | Only pymcu stdlib is compiled | Implement equivalent in pymcu stdlib |
| `importlib` / dynamic imports | Runtime module loading | Not available |
| Circular imports | Not supported | Restructure module dependencies |
| `__all__` / `__init__.py` re-exports (at runtime) | No runtime | Place all symbols in the module file directly |

**Supported:** `import foo`, `from foo import Bar`, `from foo import Bar as B` (aliased imports),
relative imports (`from . import foo`, `from .sub import helper`), multi-module projects, `pymcu`
stdlib, `pymcu-circuitpython` and `pymcu-micropython` compat packages.

---

## Built-ins

| Built-in | Status | Alternative |
|---|---|---|
| `print(str)` | ✅ Supported (routes to UART) | — |
| `print(int)` | ✅ Supported | — |
| `range(n)` | ✅ Supported (for loops) | — |
| `len()` | ❌ Not supported | Use fixed array size constant |
| `abs()` | ❌ Not yet (planned T1.4) | Manual: `if x < 0: x = -x` |
| `min()` / `max()` | ❌ Not yet (planned T1.4) | Inline helper |
| `sum()` | ❌ Not supported | Accumulate in loop |
| `sorted()` / `reversed()` | ❌ Not supported | Not available |
| `enumerate()` | ✅ Supported (compile-time index counter) | — |
| `zip()` | ❌ Not supported (runtime) | Manual parallel loops |
| `map()` / `filter()` | ❌ Not supported | Explicit for loops |
| `input()` | ❌ Not supported | `uart.read()` |
| `chr()` / `ord()` | ❌ Not yet (planned T1.5) | Integer literal for ASCII value |
| `hex()` / `bin()` | ❌ Not supported | Custom nibble-to-hex helper |
| `id()` | ❌ Not supported | Not applicable on MCU |
| `globals()` / `locals()` | ❌ Not supported | Not applicable |
| `exec()` / `eval()` | ❌ Not supported | Interpreter required |
| `open()` / file I/O | ❌ Not supported | No filesystem |

**Note:** Features marked "planned" with a tier number (e.g., T1.4, T1.5) are scheduled for upcoming
releases. See `LANGUAGE_ROADMAP.md` for details.

---

## Platform-Specific Notes (ATmega328P / Arduino Uno)

- **Stack depth:** ~80 nested non-inline calls before stack overflow (2KB SRAM, ~16 bytes/frame).
  Use `@inline` for leaf helpers to avoid using stack frames.
- **No soft float yet:** Use integer arithmetic and manual fixed-point scaling.
- **No heap:** Every variable must have a size known at compile time. No `None`, no `Optional`.
- **Interrupts and globals:** Mutable globals written from an ISR must be declared `global` inside
  the ISR and accessed atomically (disable interrupts / use GPIOR0 flag pattern).
- **String literals are in flash:** They are read-only and can only be sent to UART via the flash
  string pool. They cannot be compared, indexed, or modified at runtime.

---

## Getting Help

If you hit a compile error on a Python construct not covered here, please open an issue at the
PyMCU repository. Include the source snippet and the compiler error message.

For feature requests, check the project roadmap in `docs/` for planned features.
