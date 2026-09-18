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
with GC; supports `append()`, `len()`, `x[i]`, `for v in x:`, and a parameter or return
annotation on any function (real subroutine or `@inline`) -- a plain function's parameter is
expanded at each call site, the same mechanism a class-instance parameter already uses,
because `list[T]` has no fixed-width ABI to give it a standalone one. It is **AVR-only** -- on
ARM, PIC and RISC-V it is refused at build time, naming AVR; use a fixed `uint8[N]` array
there. `bytearray(N)` and `bytearray(b"...")` compile to SRAM `uint8[N]` arrays.
**`import array`** / **`array.array(typecode)`** is this same `list[T]`, one call spelling
later: the typecode decides T (`B`/`b` uint8/int8, `H`/`h` uint16/int16, `I`/`L`/`i`/`l`
uint32/int32; `f`/`d`/`q` are refused by name, there is no float/double/64-bit element width
here). A parameter or return annotated bare `array.array` (no typecode) takes its element
width from whatever list the caller actually passes.
**Closed dict/set literals** (`d = {0: 10, "mid": 2}` / `OK = {1, 3, 5}`) bind compile-time
lookup tables with no storage: `d[const]` folds to its value, `d[runtime_key]` lowers to a
compare chain that raises `KeyError` (catchable with `try/except`) on no match, `x in d` /
`x in {...}` test membership, and `len(d)` folds. A class-body dict
(`gain_values = {ALS_GAIN_2: 2, ALS_GAIN_1_4: 0.25}` read as `self.gain_values[gain]`)
is the same table; mixed int/float values make the lookup a float. They are read-only.
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
| Exception types are integer codes | Builtins (`ValueError` etc.); handlers match by integer code. A string-literal message is one flash word (#369). A non-literal message (f-string, concatenation, call) is a deferred print: runtime pieces are stored at the raise and `print(e)` / `str(e)` / `e.args[0]` replay them (#435) |
| `raise X(...) from Y` | Accepted; compiled as `raise X(...)`. There is no traceback. `e.__cause__` / `e.__context__` are refused (#434) |
| Unmatched at top level | An exception with no handler hits `__pymcu_unhandled_exn` — `E:<TypeName>` to UART0 then a halt, never a silent continue. Whether it reached `main` from a callee or was raised in `main`'s own body (or in an `@inline` expansion there) makes no difference |

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
| A bounded exception object | `except E as e:` binds a name (#369). One exception is live at a time, so the object is two static facts and no allocation: the type code the dispatcher already compares, and the flash address of a string-literal message. `print(e)`, `str(e)` and `e.args[0]` read the message; `isinstance(e, X)` compares the code. Every other use of `e` is refused by a sentence naming those four. A message that is not a literal stays refused, and a field set in a user exception's `__init__` is not available yet. `except*` (exception groups) is still refused by name |
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
then halts with `cli; rjmp .-2`. Useful for debugging from a serial monitor:

```
E:ValueError
```

**The transmitter does not have to be on already.** UART0 is set up when the program calls
`print()`/`input()` or constructs a `UART(...)`, and a program that does neither used to halt
with nothing on the wire (PyMCU#340). When nothing in the program owns the UART, this path
turns the transmitter on itself, at the `[tool.pymcu] stdout_baud` rate (115200 by default),
8N1. About 30 bytes of flash, and only in a program that can raise and never prints; a program
that does print keeps the image it had, byte for byte, because the initialisation is not
emitted at all. A program that owns the UART at its own rate is never reprogrammed under a
live stream: the path checks TXEN0 first and writes only when it is already set.

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
| `*args` / `**kwargs` over a run-time call | The forms themselves are supported as COMPILE-TIME sequences and mappings (#368): the callee is specialised per call site, where the extra arguments are written out, so `f(**kwargs)` and `super().__init__(self, **kwargs)` splice the known keys, and `kwargs["k"]`, `.get`, `in`, `len` and `.items()` fold or unroll. What is refused is a `**` built from a run-time mapping, and a key no callee accepts, which is named | Write the keys at the call, or declare the parameter |
| `functools.partial` | Runtime partial object | Wrapper `@inline` function |
| A function value whose target varies at run time | The address must be known at compile time | Pick with `match / case`, or index a `Callable[N]` table of known functions |
| Recursion of any depth, direct or mutual | No per-call frame: PyMCU uses a static stack layout | Iterative equivalent |

**Supported:** `@inline` functions expand at call sites — zero call overhead, zero stack.

A **free function that takes a class instance** (`def blink_twice(led: Pin)`) is supported and
expands at the call site, the way an explicit `@inline` of the same shape does: the instance
fields live in the caller's frame, so there is no subroutine to call. The parameter must be
annotated with the class name; an unannotated one is not an instance parameter.
Non-`@inline` functions use a conventional call/ret ABI, but they may **not** recurse: the
compiler assigns every function a fixed stack slot, so a recursive call cycle is refused at
build time (`RecursionError: recursive call cycle: f -> g -> f`), whatever the depth would
have been. Direct and mutual recursion are both detected.
`lambda x: expr` (no closure capture) is inlined
at the call site. `nonlocal` is supported inside nested `@inline` functions.

**A function that promises a value must produce one wherever its result is read.** There is
no `None` here: the result of an `@inline` is a temporary the expansion writes its `return`
into, and the result of a subroutine is a register. A body that reaches its end without
returning leaves both untouched, so a caller that reads the result reads whatever the register
or stack slot happened to hold. That is refused at the call, naming the callee:

```python
@inline
def bucket(freq: uint16) -> uint8:
    if freq > 488:
        return 3          # and nothing for the other path

p: uint8 = bucket(f)      # refused: a path through bucket returns nothing
bucket(f)                 # fine: nothing reads the result
```

A `match` needs a `case _:` arm and an `if` needs an `else:` for the compiler to see that
every path leaves; `raise` counts as leaving, and so does a `while True:` with no `break`.
Calling the function as a statement is always allowed, which is how an accessor whose
one-argument path returns nothing is used.

**Function references are supported.** A function assigned to a `Callable`-annotated name
captures its address and calling through it emits an indirect call (`ICALL` on AVR);
`funcref(fn)` is the explicit spelling, and `Callable[N]` builds a dispatch table you can
index at run time. A bare `cb = my_handler` works too. The one limit is that each target
must be a named function known at compile time -- rebinding the name inside a run-time
branch is refused, naming the branch.

---

## Classes and inheritance

| Feature | Why it fails | Alternative |
|---|---|---|
| Multiple inheritance / MRO | C3 linearization is a runtime concept | Single-level inheritance only |
| Runtime polymorphism (vtable dispatch) | Requires vtable + heap class objects | Compile-time `match / case` dispatch |
| `isinstance()` / `type()` | No type tags at runtime | `isinstance(x, T)` on a ZCA instance folds (#424); `isinstance(x, (tuple, list))` folds from the receiver's known shape (#423). `type()` is still refused |
| `__repr__`, `__str__` | No runtime string formatting | `uart.println()` with explicit fields |
| `dataclass` | Metaclass + runtime heap | Manual `@inline` class |
| `namedtuple` **defaults / rename / module** | Extra factory kwargs | `Name = namedtuple("Name", ("a", "b"))` -- two positional arguments. The assignment is a ZCA class |
| `namedtuple` index `p[0]` | Not a tuple subclass | Field access `p.x`; `__match_args__` is set so a class pattern binds in field order |

**Supported:** ZCA `@inline` classes (zero SRAM), `@property` / `@name.setter`,
single-level class inheritance with `super()`, `with obj:` context managers
(`__enter__`/`__exit__`), operator dunder methods (`__add__`, `__sub__`,
`__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, all comparison / bitwise
dunders). A class-typed field dispatches correctly through a **value-returning** method too
(`self.pin.read()` on a nested ZCA field), which is what the compat layers are built on —
`machine.Pin` wrapping the HAL `Pin` is exactly this shape.

A class declared inside another class is constructible, and its constants are readable
through both names: `Outer.Inner.A`, and `mod.Outer.Inner.A` through the module that
declares it. That is how CircuitPython spells the UART parity, `busio.UART.Parity.ODD`.

A call argument that holds a compile-time constant is passed as that constant, so a callee
that dispatches on it takes the same path whether the caller wrote the value at the call or
put it in a local first. The value has to be one the compiler can still see: a name a branch
or a loop can change is not one, and neither is anything read from a register.

An unannotated field takes its width from the widest value the constructor assigns — a
conversion call says its own type, a literal the narrowest type that holds it, an arithmetic
expression its widest operand. An explicit `self.x: T = ...` still wins.

A field's layout is derived from every `self.x = ...` in the class body, not from `__init__`
alone — a property setter (`@x.setter`) or a plain method `__init__` calls directly at the top
level of its own body also introduces a field, exactly as `__init__` itself does (PyMCU#441).
A method reachable only from outside construction does not: a name novel to such a method is
refused as a typo rather than silently becoming a field of its own, since nothing here can
tell the two apart the way `__init__` and a setter can be told apart from an arbitrary helper.

A field's type is fixed at the first site that writes it (scalar widening across sites stays
allowed, as it already was for multiple writes within `__init__`); a **later** write of a
categorically different kind (numeric vs. `str` vs. anything else) is a located compile error.
This is a PyMCU design choice, not an attempt to track what CPython, MicroPython or
CircuitPython actually do — measured (PyMCU#441): all three let a field change type freely
across writes, with no error or warning at all.

**Divergence** (PyMCU#441): a field read somewhere and never written anywhere reachable in the
class is refused at compile time, worded like the `AttributeError` every one of CPython,
MicroPython and CircuitPython would raise for the same program at run time — that refusal is
sound, since no run of the program could ever supply a value. What PyMCU cannot decide
statically is *when*, in one instance's actual execution, a write reaches a field relative to
a read of it: the three interpreters resolve attribute existence dynamically, per instance, by
execution order, so a write that happens to run before a given read makes that read succeed
even when it is not the field's "defining" site in the class body PyMCU scans. A read that (in
execution order) precedes every write reachable from it gets PyMCU's zero-initialized default
instead of the `AttributeError` the interpreters would raise at that point. `tests/oracle/probes/`
has a probe of this shape marked `# expect: divergence`, citing this paragraph.

---

## Type system limitations

| Feature | Why it fails | Alternative |
|---|---|---|
| `complex` numbers | Requires float | Not available |
| `Decimal` | Requires heap | Not available |
| `None` assigned to a scalar (`int` / `uintN`) | `None` is a real null literal, not the integer `-1` | Use a sentinel value (e.g. `0xFF`), or keep `None` for reference / optional-typed values where `is None` / `== None` checks work |
| `Union` of two real types | Runtime type tag required | Separate functions per type |
| `TypeVar` / `Generic` | Runtime generics | Separate `@inline` functions per type |

**`None` is a compile-time value.** It travels: passing it as an argument, assigning it
through a property setter, binding it to a name, or storing it in a field binds that name as
`None`, so `p is None` folds and a `match p:` is decided at compile time. `None` matches
`case None` and the wildcard and nothing else, and only the arm it selects is lowered, so a
refusal written in an arm the program never takes never fires. This is what makes the
CircuitPython spelling `pin.pull = None` mean "no pull".

**`Optional[X]` is read as `X`,** and so are `X | None` and `Union[X, None]`, in every
annotation position. The refusal they used to get was about storage, and storage is not the
question: None-ness is the compile-time property above, so the annotation needs `X`'s width
and nothing else. A field keeps the knowledge PER INSTANCE, so two objects of one class, one
constructed with the optional argument and one without, get different code from the same
method:

```python
class Dev:
    @inline
    def __init__(self, pin: Optional[uint8] = None):
        self._pin = pin

    @inline
    def go(self) -> uint8:
        if self._pin is not None:
            return self._pin
        return 99

d = Dev()       # d.go() is the constant 99, and d._pin has no storage
e = Dev(7)      # e.go() is the constant 7, with no test at run time
```

Neither branch that cannot run is lowered, so this costs nothing: the 321-fixture corpus is
byte-identical across the change.

**Where the knowledge runs out is a `return`.** The caller asked for a number and the path
answers `None`, which has no width, so that return is refused in one sentence at the line it
is written on. A `return None` on a path the caller cannot reach is not refused, because the
guard that excludes it folds first.

**A `Union` of two REAL types on a PARAMETER** of an `@inline`-expanded function or method
(a constructor included -- every ZCA instance is built at its own call site) reads the same
way `Optional[X]` does, one step further: not "the width both members share" -- there is
none -- but "the type of the argument at THIS call site, which must be one of the members",
exactly how an `@inline` overload already dispatches on an argument's type. A field assigned
from such a parameter takes the site's type the same way any unannotated field does. A
`List[X]`/`Tuple[X, ...]` member matches a fixed array/list literal argument (there is no
run-time `List`/`Tuple` object here); a `Callable[...]` member matches a plain function
reference. A call whose argument matches none of the members is refused, naming them. A
`Union` on a REAL SUBROUTINE's parameter, or on anything that is not a parameter (a field, a
local, a return type), keeps its refusal: there both members need storage and disagree about
how much, and a real subroutine has one ABI for every caller with no call site to resolve it
at.

**Note on `float`:** Soft-float (IEEE 754 single-precision) is supported on AVR via a
pure-assembly helper library. Expect ~200-400 cycles per operation. Subnormals are treated as
zero; NaN and Inf propagate correctly. `uint32(x * 100.0 + 0.5)` and the other float→int
casts truncate toward zero on the real value, not on its raw bit pattern.
ARM and PIC18 have `float` too (RP2040 through the bootrom fast-float library, RP2350
through the M33 FPU). **PIC16 and RISC-V have no floating point at all** -- even a bare
`x: float = 1.5` fails there, today with an unlocated backend message rather than a proper
diagnostic.

**Note on `const[T]`:** a `const[T]` parameter accepts compile-time **float** constants as
well as integers and strings, so `Timer(freq=2.5)` binds. What it does not accept is a value
that varies at runtime: passing one is a located `CompileError` naming the parameter, rather
than a silent fold of whatever the variable happened to hold. `Pin(n)` where `n` is a runtime
variable is the case you are most likely to hit — a pin identity has to be known at compile
time for the GPIO access to stay zero-cost.

**A local holding a value the compiler can see IS a compile-time value.** `x: uint8 = 5` then
`x + 1` is the constant 6, not an addition, and the same goes for a chain of them: a HAL helper
written through wide locals, which is how it has to be written for the arithmetic not to
truncate, folds exactly as the expression form does. What follows from that is worth knowing
before it surprises you:

* `assert x == 3` on such a local is decided at compile time, and a false one is a
  `CompileError` rather than a stripped statement.
* a `const` parameter accepts it, and a `match` on it picks its arm at compile time.
* a diagnostic about "a run-time value" will not be about it.

The value is forgotten at every write to the name, at every name a loop body can assign, and
where the arms of an `if` chain disagree, so a name a loop mutates is a run-time value again.
Reading a register (`GPIOR0.value`) is the way to say "the compiler cannot know this".

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
| A tuple **literal** passed as an argument or stored in a field | A tuple is a compile-time construct here | Separate variables, or a fixed-size array |
| Dict comprehension | Heap allocation | Not available |
| Set comprehension | Heap allocation | Not available |
| Generator expressions | Coroutine frame requires heap | A `yield` generator function (supported — see Async and concurrency) |
| `map()` / `filter()` with runtime iterables | Lazy iterator requires heap | Explicit `for` loop |

**Supported:** `for i in range(N)` (runtime or constant N), `for x in array`,
`for x in [...]`, `for x in (...)`, `for i, x in enumerate(iterable)`, `for x, y in zip(list1, list2)`,
`for x in reversed([...])`, `for x in reversed(range(...))`, `x in range(...)`,
list comprehensions with compile-time constant bounds (`range(start, stop, step)` honours
the step), nested list comprehensions, `if`-filtered list comprehensions (constant condition),
`for pin in [DigitalInOut(p) for p in (...)]` and
`for bit, pin in enumerate([DigitalInOut(p) for p in (...)])` (CT unroll of ZCA instance arrays),
and `for pin in (reset_dio, enable_dio, ...)` over already-constructed instances,
and `for x in t` where `t` is a bound tuple-return result.

A `range()` bound is folded before the loop is lowered, whatever shape it is written in: a
literal, a name, or an expression over either. A count of at most eight unrolls, an empty
range emits nothing, and anything the program really decides at run time stays a loop.

A `for` over a short constant list unrolls, and the loop variable is a compile-time constant
in each iteration, so a `const` parameter receiving it resolves as it would from a literal.
The elements may be numbers or STRINGS, which is what a row of board pins is:
`for pin in (board.D2, board.D3, board.D4)` works, and so does the pair form
`for pin, name in [(board.D2, "D2"), (board.D3, "D3")]`. A tuple or list of
already-constructed instances unrolls the same way: `for pin in (reset_dio, enable_dio, ...)`.

A comprehension of class INSTANCES is not supported outside the forms above: PyMCU lays an
instance out at compile time and it has no array slot to live in. Write the list as a
literal of constructions (`[A(x), A(y)]`), or build each one by name.

**A driver that takes a list.** A class is handed several pins, several devices or a table
of numbers the way every embedded library does it, and keeps the list in a field:

```python
class LedBar:
    def __init__(self, pins):
        self._pins = pins

    def all_on(self):
        for p in self._pins:
            p.value(1)

    def one(self, i: uint8):
        self._pins[i].value(1)

bar = LedBar([Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT), Pin("PD7", Pin.OUT)])
```

The list is a compile-time sequence: the elements are built once at the call site, and the
parameter and the field are other names for them, never copies. So `self._pins[0]`,
`for p in self._pins`, `len(self._pins)` and a method call through a run-time index all
work, and so does the same list written into a name first (`pins = [...]`, `LedBar(pins)`),
a list given to a method rather than to the constructor, and a list of instances that each
hold a pin. There is no length limit on the sequence itself.

Two things it is not. A run-time subscript that takes the ELEMENT
(`p = self._pins[i]`) is refused: the instances are flattened at compile time and have no
slot to select, so walk them with `for` or index with a constant. And a run-time subscript
that CALLS a method is lowered as one comparison and one expansion per element, so past
eight it is refused as more code than it is worth.

A list of NUMBERS in a field works the same way for a constant subscript, `for` and
`len()`. A `bytearray` or a fixed array handed to a driver keeps its storage, so
`self._data[i] = v` writes the caller's buffer.

**A method is not a field.** `Pin.value` is an overloaded method (`value()` reads,
`value(x)` writes), so `p.value = 1` is an assignment to a name the class does not have as
a field. It is refused where it is written, and told to call `p.value(1)` instead. The
CircuitPython `digitalio.DigitalInOut.value` IS a property and takes the assignment.

**A driver library, unmodified.** The shapes a MicroPython or CircuitPython driver is
written in now compile as their authors wrote them:

```python
class SevenSeg:
    def __init__(self, pins, common_anode=False, dp_pin=None):
        self.segments = [Pin(p, Pin.OUT) for p in pins]     # a comprehension of instances
        self.dp = Pin(dp_pin, Pin.OUT) if dp_pin else None  # the dead branch is not lowered
        self.digits = {0: [1,1,1,1,1,1,0], ...}             # a dict of rows, in a field

    def show(self, num):
        pattern = self.digits[num]                          # a run-time key picks a row
        for pin, seg_on in zip(self.segments, pattern):     # zip over a field and a row
            pin.value(seg_on ^ self.common_anode)
```

A comprehension of instances is the literal of constructions written once instead of N
times, so it needs a compile-time iterable: a constant list, a name or parameter bound to
one, or `range(N)`. A comprehension whose length is decided at run time is still refused.

A dict of rows must be a rectangle: same-length rows of constants, keyed by distinct
constants. Ragged rows have no table and are refused, saying so.

**Character keys.** A ONE-CHARACTER string literal is its character code, so
`{"0": [...], "A": [...], "-": [...]}` is a rectangle like any other and a byte read at run
time indexes it. A MULTI-character key is an interned id instead, which no run-time value
equals, so a table keyed by one can only be looked up with a constant. That is the rule for
every dict, not only a table of rows.

**A lookup table written as a plain list.** `DIGITS = [0x3F, 0x06, ...]` read as
`DIGITS[digit]` with a run-time digit is the shape of every 7-segment table, font and gamma
curve. The values are constants and nothing writes them, so the table is placed in flash,
and only when a run-time subscript actually needs it: a table that is only ever indexed
with a constant emits nothing at all, as before. The same holds for such a list reached
through a parameter or held in a `self` field. What is refused is a table the program
STORES into: flash cannot be written, so that one is told to declare its storage
(`T: uint8[10] = [...]`).

**A sequence bound to a name.** `DUTIES = [256, 383, ...]` and `DUTIES = (256, 383, ...)`
are the same thing to iterate over, at any length: up to eight constant elements the `for`
unrolls against the literal, and past that the name gets a fixed array the loop walks.
Without an annotation the element width comes from the widest element, so a 16-bit table
stays 16-bit. Being the same storage, a write through the name (`DUTIES[0] = 1`) is not
refused on the tuple the way CPython refuses it.

**The range counter.** The loop variable of a `range()` loop is as wide as its bounds need,
with no annotation: constants exactly (`range(300)` is a 16-bit loop, `range(200, -1, -1)` a
signed one), variables by their declared type (`n: uint16` gives a 16-bit counter), and an
`int8` start with a `uint8` stop gives `int16`. A step other than 1 can stop one step past
`stop`, and the counter is sized for that too. A range that fits a byte stays the 8-bit loop it
always was. A type declared on the loop variable before the loop is used as written, and is a
`CompileError` when constant bounds do not fit it. A signed runtime step picks its direction at
run time; an unsigned one counts up.

An unannotated accumulator at module level is typed the way a local is, from the promoted
width of what feeds it (`n = 0` then `n = n + 1` over `range(300)` is a 16-bit counter); a
written annotation keeps its width and wraps at it. A comparison is decided by the values on
both sides: `count >= 404` with `count: uint8` is False, and `x < n` with `n: uint16` compares
all sixteen bits.

After the loop the variable holds the last value visited, as in Python, whether the loop
unrolled or ran as a counter. The one difference: a range that runs zero times leaves the
variable at `start` (Python leaves it unbound). `enumerate(range(...))` with runtime bounds
keeps a runtime index alongside the counter. `reversed(range(...))` with runtime bounds needs
a step of 1 or -1; with constant bounds any step works.

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

A routine sits at ONE interrupt vector. Registering the same handler at a second vector is
refused where it is written: the table has one entry per routine, and the earlier vector
would be left on the bad-interrupt handler. Two pins handled by the same code need a second
function that calls the shared body — `def on_int1(): step()` — registered at the second
vector.

:::{admonition} Timer0 and millis / ticks_ms
:class: warning

`millis_init()` (auto-injected when `ticks_ms()` / `monotonic()` or, on ATmega, an
`async def` is detected) runs **Timer0** at prescaler 64 and counts its overflows. A PWM
on PD5/PD6 (Arduino D5/D6) at the default frequency shares the timer without harm; any
other frequency there would reprogram the prescaler under the clock (measured: 5000 Hz on
D6 made `monotonic()` run 8.44 times too fast) and is refused at compile time, naming
D3/D11 and D9/D10 as the pins to use. CTC or other direct uses of Timer0 in such a
program are still yours to avoid.

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
`from foo import *`, `from package import submodule`, relative imports
(`from .util import half`, `from . import util`), multi-module projects, `pymcu` stdlib,
`pymcu-circuitpython` and `pymcu-micropython` compat packages.

`from <package> import <submodule>` binds the submodule under its own name, as CPython does
when the package's `__init__` has no such attribute: `from adafruit_motor import servo`, then
`servo.Servo(pwm)`. Rebinding that name to an instance (`servo = servo.Servo(pwm)`) is the
Adafruit guide spelling: later reads and calls see the instance, not the module (#467).
An alias is kept (`from adafruit_motor import servo as s`).

An import alias belongs to the file that writes it. Two modules that alias different things
to the same name each keep their own, the way Python scopes them.

`from foo import *` binds the public top-level names of `foo`: its functions, classes and
module-level variables, minus the ones whose name starts with `_`, which are private and
which a star never binds in CPython either. A module that declares `__all__` gets exactly
that list instead. A name `foo` re-exports (one it imported itself) resolves through the
star as well.

`import os` / `from os import uname` resolve to `pymcu/os.py`, the same stdlib-alias
fallback `import time` already uses. `uname()`, `os.name` and `os.sep` are compile-time
facts of `__CHIP__`. Names that need a filesystem (`listdir`, `getenv`, `stat`) are not
defined on that module, and `import uos` points at `import os`.

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
| `range(n)` | ✅ Supported | For-loop bounds, runtime or constant; the counter is sized from the bounds. Also `x in range(...)`, `reversed(range(...))`, `enumerate(range(...))`. Not a value: `r = range(4)` is a `CompileError` |
| `len(arr)` / `len(b"...")` | ✅ Supported | Compile-time constant fold |
| `abs(x)` | ✅ Supported | Intrinsic |
| `min(a, b)` / `max(a, b)` | ✅ Supported | Intrinsic. Also over a fixed-size array, and with `key=f`: the key is called once per operand and the winner is the original value, not its key |
| `sum(iterable)` | ✅ Supported | Compile-time fold or unrolled additions |
| `enumerate(iterable)` | ✅ Supported | Compile-time index counter over constant sequences, `range()`, and fixed-size arrays -- including a buffer reached through inline parameter bindings or a `bytes([expr])` argument whose elements are run-time |
| `zip(a, b)` | ✅ Supported | Compile-time unroll over constant lists |
| `reversed(iterable)` | ✅ Supported | Compile-time reverse unroll |
| `any(iterable)` / `all(iterable)` | ✅ Supported | Compile-time fold |
| `divmod(a, b)` | ✅ Supported | Compile-time or runtime |
| `pow(x, n)` / `x ** n` / `math.pow(x, n)` | ✅ Supported | Compile-time integer fold; runtime integer unroll; runtime float via `__pymcu_powf` (#463) |
| `hex(n)` / `bin(n)` | ✅ Supported | Compile-time only |
| `str(n)` | ✅ Supported | Compile-time only |
| `ord('A')` / `chr(n)` | ✅ Supported | Compile-time constant only |
| `int.from_bytes(b, e)` | ✅ Supported | Compile-time fold or runtime |
| `memoryview(buf)` | ✅ Supported | Compile-time alias of a fixed-size buffer (bytearray or fixed array): `memoryview(buf)[k]` indexes it, and `memoryview(buf)[a:]` inside `struct.unpack`/`unpack_from` adds its start to the read offset. The name is a CPython builtin type this compiler stores, so `-> memoryview` is the same view the call already wraps. No run-time buffer protocol |
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
---

## What stops each Adafruit CircuitPython library

Measured on 2026-09-17 against an Arduino Uno (atmega328p), with each library's file
**byte-identical to its repository** and a `main()` written after the library's own example
that constructs the object and calls its methods. The harness is 37 libraries (the original
twenty plus I2C sensors and expanders that sit next to them on Adafruit's list).

**Sixteen of the thirty-seven build unmodified**: `adafruit_hcsr04` (3 430 bytes),
`adafruit_motor`'s servo (2 332 bytes), `adafruit_pcf8574` (1 442 bytes),
`adafruit_bus_device` (800 bytes; its own example uses a `bytearray([...])` inline
argument and a generator expression in `join`, which need the supported spellings),
`adafruit_mcp3xxx` (3 094 bytes), `adafruit_74hc595` (402 bytes),
`adafruit_ahtx0` (7 108 bytes), `adafruit_mcp9808` (4 646 bytes),
`adafruit_lis3dh` (2 522 bytes), `adafruit_tsl2591` (5 814 bytes),
`adafruit_mlx90614` (3 846 bytes), `adafruit_bmp280` (25 006 bytes),
`adafruit_tcs34725` (28 414 bytes), `adafruit_ina219` (7 294 bytes),
`adafruit_aw9523` (2 294 bytes) and `adafruit_veml7700` (10 614 bytes).

| Library | Stops at | What the compiler says |
|---|---|---|
| `adafruit_ahtx0` | **builds unmodified, 7 108 bytes** | |
| `adafruit_ads1x15` | generator expression (`next(key for key, value in ...)`) | `Expected ')'` |
| `adafruit_aw9523` | **builds unmodified, 2 294 bytes** | (moved off name `adafruit_aw9523_AW9523`: `type(inst)` in the descriptor rewrite is the source class name) |
| `adafruit_bme280` | `_bus_implementation.read_register` | call to undefined function |
| `adafruit_bmp280` | **builds unmodified, 25 006 bytes** | (moved off `list(struct.unpack(...))`: an unpack result is a compile-time sequence, and a function returning its local buffer binds the caller's name to that slot) |
| `adafruit_bus_device` | **builds unmodified, 800 bytes** | the library itself compiles; its own example needs the bound-name `bytearray` and no generator expression in `join` |
| `adafruit_character_lcd` | `Pin.high()` runtime bit index | `__init__` is no longer a shared subroutine and a reduced `Lcd(mcp.get_pin())` fixture keeps the expander class; the unmodified I2C backpack still reaches HAL `self._port[self._bit] = 1` |
| `adafruit_debouncer` | `Debouncer(pin)` | `'io_or_predicate' is declared Union[ROValueIO, Callable[[], bool]]`, and this argument's type matches none of those members |
| `adafruit_dht` | `def temperature(...) -> Union[int, float, None]` | a union of two REAL types; `uname()` is a compile-time view of `__CHIP__` (#466) so the CircuitPython-vs-Blinka test already took the CircuitPython arm |
| `adafruit_dps310` | (moved off `coeffs = [None] * 18`) | a repeated list of None is a fixed SRAM array; next construct after that is measured after this landing |
| `adafruit_ds18x20` | `import onewireio` | module not found |
| `adafruit_ds3231` | `from time import struct_time` | `pymcu.time` defines the nine-field stub; the CircuitPython overlay's advertised names are still only `monotonic` / `monotonic_ns` / `sleep` |
| `adafruit_74hc595` | **builds unmodified, 402 bytes** | (moved off `bytearray(self._number_of_shift_registers)` and `DigitalInOut(pin, self)`: a compile-time field is a buffer size, and a class defined in the module shadows the entry file's `from digitalio import DigitalInOut`) |
| `adafruit_hcsr04` | **builds unmodified, 3 430 bytes** | |
| `adafruit_ht16k33` (matrix) | `bytearray((self._buffer_size) * len(self.i2c_device))` | could not determine buffer size from initializer |
| `adafruit_ht16k33` (segments) | `def print(self, value: Union[str, float], ...)` | a union of two REAL types; a call in a raise message is a deferred print (#435) |
| `adafruit_ina219` | **builds unmodified, 7 294 bytes** | (moved off `self.raw_bus_voltage`: `type(self)` in the descriptor rewrite is `INA219`, not the mangled `adafruit_ina219_INA219`) |
| `adafruit_irremote` | `yield` in `NonblockingGenericDecode.read` | a generator has to be a module-level function today |
| `adafruit_lis3dh` | **builds unmodified, 2 522 bytes** | |
| `adafruit_mcp230xx` | `Pin.high()` runtime bit index | same as `adafruit_character_lcd` |
| `adafruit_mcp3xxx` | **builds unmodified, 3 094 bytes** | (moved off `with ... as`: the bound name now takes `__enter__`'s returned instance class) |
| `adafruit_mcp9808` | **builds unmodified, 4 646 bytes** | |
| `adafruit_mlx90614` | **builds unmodified, 3 846 bytes** | |
| `neopixel` | `all(... for component in val)` in `adafruit_pixelbuf` | generator expression; `import adafruit_pixelbuf` itself is present |
| `adafruit_pca9685` | `self._channels[index] = PWMChannel(...)` | (moved off `[None] * len(self)`: a repeated list of None is a fixed SRAM array of integer slots.) storing a ZCA instance into that numeric cache is a later gap |
| `adafruit_pcf8523` | `from time import struct_time` | same as `adafruit_ds3231` |
| `adafruit_pcf8574` | **builds unmodified, 1 442 bytes** | (moved off `-> Pull.UP`; the `pull` property compiles) |
| `adafruit_seesaw` | f-string raise with `self.chip_id` | a raise message must be adjacent string literals or a module-level string constant |
| `adafruit_sht31d` | `word[i*2], crc[i*2], ... = struct.unpack(...)` | Expected newline or end of block (multi-target unpack from a call) |
| `adafruit_sht4x` | `@classmethod` | no runtime class object; write a module-level factory |
| `adafruit_si7021` | `obj: "adafruit_si7021.SI7021"` | string (forward reference) type annotations are not supported |
| `adafruit_ssd1306` | `fill = (color >> 16) & 255, ...` in `adafruit_framebuf` | Expected newline or end of block (tuple assignment) |
| `adafruit_tcs34725` | **builds unmodified, 28 414 bytes** | (moved off run-time `pow` to `__pymcu_powf`; tuple-valued property reads bind a compile-time sequence) |
| `adafruit_tmp117` | `@classmethod` | same as `adafruit_sht4x` |
| `adafruit_tsl2591` | **builds unmodified, 5 814 bytes** | |
| `adafruit_veml7700` | **builds unmodified, 10 614 bytes** | (moved off `self.gain_values[gain]`: a class-body dict is a compile-time lookup table, including mixed int/float values) |
| `adafruit_motor` (servo) | **builds unmodified, 2 332 bytes** | (moved off `self._min_duty`; the whole four-module package compiles) |

### Which of these are limits and which are gaps

**Limits of the no-heap, fixed-width model.** A piece in a raise message whose type has
no static width is refused by name. `array` is dynamic storage. Each says so in one
sentence at the line it is written on.

Two entries left this list rather than staying on it. `**kwargs` was read as needing a
run-time dictionary, which is true of CPython and false here: the callee is specialised per
call site and the extra keyword arguments are literals there, so it is a compile-time mapping
(#368). `except X as e` was read as needing an exception object, and it does -- a bounded one,
which one live exception at a time makes static rather than allocated (#369). Both were
restrictions of the lowering, not of the model, and all three libraries that stopped on
`**kwargs` now stop somewhere else entirely.

**`adafruit_ssd1306` now reaches `adafruit_framebuf`** (the module is present in the
harness); it stops on a tuple assignment. `onewireio` is still missing for
`adafruit_ds18x20`. `neopixel` moved off a call inside a raise message (#435) onto
a generator expression in `adafruit_pixelbuf`.

**The remaining refusals are scattered, one construct each.** `enumerate()` over a
buffer that reaches `busio.I2C.writeto` through inline bindings now compiles -- the
aliased class-attribute array resolves to its module-init storage, and a
`bytes([expr])` argument whose elements are run-time materializes a hidden buffer.
A filled `bytearray` return is expanded at the call site (#464), and
`struct.unpack` results bind as typed sequences, so `adafruit_bmp280`
builds unmodified. A runtime float `pow(x, 2.5)` lowers to `__pymcu_powf`
(#463) and a tuple-returning property binds a compile-time sequence, so
`adafruit_tcs34725` builds unmodified.
A `Protocol` member of a constructor `Union` is structural (#465), so
`adafruit_debouncer` moved off the annotation; this simpletest's `Debouncer(pin)`
still refuses the `DigitalInOut` as matching none of the members.
A field whose first store is a string literal or `bytearray(...)` is that
kind, so `adafruit_character_lcd` (`_message`) and `adafruit_74hc595` (`_gpio`)
move off the numeric-vs-str / numeric-vs-buffer contradiction.
`pin.direction = Direction.OUTPUT` through a for-unrolled instance is the
`@property` setter, so `adafruit_character_lcd` moves off that assignment.
`bytearray(self._number_of_shift_registers)` after the field holds a
compile-time integer is a fixed buffer, and `DigitalInOut(pin, self)`
inside that module is the file's own two-argument class even when the
entry file imported `digitalio.DigitalInOut`, so `adafruit_74hc595`
builds unmodified. `__init__` is expanded at each construction, so an MCP
`get_pin()` passed into `Character_LCD.__init__` keeps the expander class
instead of the `digitalio.DigitalInOut` annotation. A rebound import alias
(`servo = servo.Servo(pwm)`) sees the instance on later reads (#467).
A `try`-guarded
`from typing import Tuple` that shares its `try` with a failing sibling import used to lose
the resolved names entirely; with the fold fixed, `adafruit_ina219` and `adafruit_veml7700`
move inside `adafruit_register` to `RWBits.__get__`/`__set__`, where `obj` is the owning
instance even though it is annotated `I2CDeviceDriver` (#419); `value <<= self.lowest_bit`
keeps `value` as a local so `reg |= value` is the shifted bits. A class-body
`_fit(n)` keeps the grown `_BUFFER` rather than letting the replay of
`bytearray(1)` shrink it, so `_BUFFER[i]` in `RWBits` is in range. `value: Any`
on a descriptor `__set__` is the written value, not a missing width. `type(inst)` in the
descriptor rewrite is the source class name, so `self.raw_bus_voltage` inside a method of
an imported class is not "name 'adafruit_ina219_INA219' is not defined"; `adafruit_ina219`
and `adafruit_aw9523` build unmodified. A class-body dict is the same lookup table as a
module-level one, so `self.gain_values[gain]` is a fold or a compare chain (mixed
int/float values are a float); `adafruit_veml7700` builds unmodified. A constant tuple
assigned to a field is the same array as a list (`self.scale = (524288, ...)`), so
`adafruit_dps310` moves off that onto `coeffs = [None] * 18`. A `str` parameter that
received a compile-time string keeps the text at any length, so
`struct.calcsize(struct_format)` inside an inlined descriptor constructor folds
(`StructArray(0x06, "<HH", 16)`). `[None] * n` is a fixed SRAM array, so
`coeffs = [None] * 18` and `self._channels = [None] * len(self)` index; None is a
0 slot. `adafruit_pca9685` moves off that onto storing a `PWMChannel` into the
integer cache. The inner Adafruit TYPE_CHECKING guard
(`except NotImplementedError` around `from pwmio import PWMOut`) keeps the
resolved name and does not load the stub package (#480, #481). `raise ... from ...`
(#434) and `from __future__ import annotations` (#452) no longer stop `adafruit_irremote`;
`namedtuple` is a compile-time ZCA class factory, so it moves off
`from collections import namedtuple` onto `yield` in a method. `isinstance(address, (tuple, list))`
folds from the argument's shape (#423), so `adafruit_ht16k33` matrix moves off that onto
`bytearray((self._buffer_size) * len(self.i2c_device))`. `os.uname()`
is a compile-time view of `__CHIP__` (#466), so `adafruit_dht` moves off `from os import
uname` onto `Union[int, float, None]` on `temperature`. `time.struct_time` is a
nine-field stub, so the RTC drivers move off that import.

**Five more I2C sensors build unmodified** on the expanded list: `adafruit_ahtx0`,
`adafruit_mcp9808`, `adafruit_lis3dh`, `adafruit_tsl2591`, `adafruit_mlx90614`.
`adafruit_ina219` (7 294 bytes) and the `adafruit_aw9523` expander (2 294 bytes) join
them once `type(self)` in a descriptor rewrite is the source class name.
`adafruit_veml7700` (10 614 bytes) joins once a class-body dict through `self` is a
lookup table.

**A union of two REAL types is what the union refusal is now about.** `Optional[X]`,
`X | None` and `Union[X, None]` are read as `X`: see "None is a compile-time value" above.
That moved nine of the original twenty off the annotation they used to stop on, and cost nothing (the
321-fixture corpus is byte-identical).

**What moved on 2026-09-14.** `WriteableBuffer` and `ReadableBuffer` are read as the byte
buffer they name (#356), so `adafruit_bus_device` and the three libraries behind it reach the
union instead of an unknown type. `Tuple[...]`, and a `...` or a `Literal[...]` inside a tuple
annotation, are read (#357): `adafruit_tcs34725` moves off the annotation, `adafruit_ds18x20`
reaches its missing module, and `adafruit_ht16k33` segments moves from the annotation on line
181 to the call in a raise message on line 210. A two-index subscript binds its pair at compile
time (#352), so `adafruit_ht16k33` matrix compiles `m[x, y] = 1` as written. And `Optional[X]`
is read as `X`, which moved nine. Nothing now stops on an annotation naming a type the compiler
has, or on a construct whose message points at a bracket.

**A two-index subscript** (`matrix[x, y]`) is the same no-runtime-tuple limit reached through
a subscript: the pair becomes one tuple before `__getitem__` sees it. Whether the compiler
should bind that pair at compile time, so the `x, y = key` upstream writes unpacks the way
`a, b = f()` already does, is open.

Every one of the thirty-seven now names its construct at the line it is written on. None is reported
as a missing bracket, and none names anything internal to the compiler. That is the property to
check when one of these messages changes.
