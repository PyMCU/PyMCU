# Roadmap

This page tracks which language and HAL features have been implemented, and what is planned next.

---

## Implemented

### Language

| Feature | Notes |
|---|---|
| `if / elif / else` | Compile-time DCE on `__CHIP__` branches; under `stdlib = [...]` also `sys.implementation` / `sys.platform` / `uname()` guards (RFC 0007, PyMCU#266) |
| `while` + `break` / `continue` | |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)`. The counter is as wide as the bounds need: 8-bit for `range(n)` with `n: uint8`, 16-bit for `range(300)`, signed for `range(200, -1, -1)`; a declared type on the loop variable is used as written. A constant range of at most 8 steps unrolls when its body is cheap; an expensive body -- a nested loop or a call -- lowers to a counter loop instead. A loop variable that must stay a compile-time constant (one fed to a `const` parameter, say `Pin(n, ...)`) unrolls regardless of body cost. After the loop the variable holds the last value visited, as in Python |
| `for x in array` / `for x in [1, 2, 3]` | Fixed-size array or constant list literal |
| `for x in named` where `named = [...]` / `(...)` | List and tuple alike, at any length: up to 8 constant elements the loop unrolls against the literal when the body is cheap, past that -- or with an expensive body at any length -- the elements go into a fixed array read from flash by a counter loop. The element width comes from the widest element |
| `for x in ["PD2", "PD3"]` / `for x in (board.D2, board.D3)` | A constant list of STRINGS unrolls too, and the loop variable binds as a string constant, so a `const` parameter receiving it resolves as it would from a literal. Also the pair form, `for pin, name in [(board.D2, "D2"), ...]` |
| `for i, x in enumerate(iterable)` | Compile-time index counter; `enumerate(range(...))` with runtime bounds keeps a runtime index. `enumerate(s.split(sep))` on compile-time strings unrolls the split chunks, and `enumerate(s)` over one compile-time string iterates its characters: eight or fewer unroll to compile-time pairs, a longer string runs a counter loop over its flash copy with the character a runtime `uint8` `ord()` accepts (adafruit_framebuf `text()`) |
| `for chunk in s.split(sep)` | Compile-time unroll over a string whose text is known -- literal, module constant or parameter bound to one; `maxsplit` as a compile-time int. A `split()` call as a value is refused: there is no list to hand back |
| `for x, y in zip(a, b)` | Compile-time unroll over paired lists |
| `reversed(iterable)` | Compile-time reverse unroll; `reversed(range(...))` is the descending range |
| `match / case` | Literal, wildcard, OR (`\|`), guard `if cond`, sequence, capture, dotted-name patterns; DCE on `__CHIP__`. A capture binds the name the way CPython binds it at that point: the module global at module level, a function-scoped local inside a function, and one slot per expansion inside an `@inline` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return (a tuple-returning function force-inlines; annotated `-> (T1, T2)`, `-> tuple[T1, T2]` or `-> Tuple[T1, T2]`). A `@property` that returns a tuple is the same `f()[k]` site (`self.measurements[0]`). `return struct.unpack_from(fmt, buf, off)` delivers the format's fields as the result tuple, no annotation needed. `return (a, b) if c else (a, b, d)` is a tuple return too when `c` folds at compile time, and `obj[i] = f()` binds those slots as the sequence `__setitem__` unpacks (adafruit_neopixel `wheel`). A `return None` or bare `return` on a reached path of a `-> X` function is refused -- None has no width. `-> Optional[X]` (also `-> X | None`, `-> Union[X, None]`) on a real subroutine is the runtime-tagged return of RFC 0009 phase 1: payload in the ordinary return registers, a one-byte member tag in the next (R25/R22/R20 by payload width), the tag byte stored beside a local that binds the result, `is None`/`is not None`/`if r`/`r or default` reading it and narrowing -- and zero tag cost when the None-ness folds at compile time. `-> Union[A, B, ...]` (also `A | B`, up to four members, `None` anywhere in the list) extends the same tag to a member index: the payload is the widest member's storage, `isinstance(r, T)` and `match`/`case T():` compare and dispatch on the tag, a narrowed read takes the member's width, and a union provably decidable at compile time emits exactly the code it had before. A field declared or inferred union-typed stores a flattened payload plus a sibling tag byte. Union parameters on real subroutines stage the member tag in the argument run immediately after the payload; union fields store a flattened payload plus a sibling tag byte, and a local merged `None` under a runtime condition carries one too. An unnarrowed tagged value dispatches on its tag where CPython would fault on None — arithmetic, an ordering comparison, unary `-`/`~`, `len()`, a subscript, an argument to a non-Optional parameter — each live member running the op at its own width and the None leaf raising TypeError at run time with CPython's wording, while `print(r)` and `f"{r}"` emit the member's own text or `None` exactly as CPython prints it. Buffer parameters may be annotated `bytearray`, `WriteableBuffer` or `ReadableBuffer` |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level statements |
| Module-level `main()` (bare, or under `if __name__ == "__main__":`) | Says where the entry point's body runs: what is written after the call runs after the body. A second call, and an early `return` with module-level code after the call, are refused |
| `class` | ZCA `@inline` flattening, constructors, `@property` / `@name.setter`; a class attribute whose class defines `__get__`/`__set__` is a descriptor, and `obj` is the owning instance even when annotated with a typing-only name (#360, #419); `type(inst)` in that rewrite is the source class name, including for an imported class; `value: Any` on `__set__` is the written value, not a missing width; `value <<= n` keeps `value` so a later read of it is the shifted bits; `obj.attr = (a, b)` on a descriptor member calls `__set__` with the tuple as `value`, and `obj.attr[k]` on a tuple-returning `__get__` is `f()[k]`; the descriptor protocol also reaches a class attribute literally named `value` (`obj.value` on a data descriptor -- adafruit_register, digitalio -- used to fall to the `.value` MMIO/collapsed-scalar shortcut instead, silently, since that shortcut ran first and unconditionally). Not supported: reading or writing a descriptor whose class defines `__set_name__` (CPython calls it once at class creation; nothing runs user code there, so it is refused rather than answering with never-initialised storage); reading a descriptor through the class itself, `Box.value` (CPython calls `__get__(attr, None, Box)`; there is no instance to evaluate as `obj`, refused by name); writing to a non-data descriptor (a class whose class defines `__get__` but not `__set__`) -- CPython would create a per-instance override that shadows the class attribute, which PyMCU's compile-time instance layout has no storage for, refused by name |
| Nested `class` | Constructible, and its constants readable through both names: `Outer.Inner.A`, and `mod.Outer.Inner.A` through the declaring module (`busio.UART.Parity.ODD`) |
| Single-level class inheritance | ZCA base + derived; `super()` calls. `class C(mod.Base)` after `import pkg as mod` resolves `mod.Base` to the defining module, so `super().__init__` expands the imported constructor. A field assigned inside that base `__init__` `if` (`self.format = Fmt()`) is still a constructor field (adafruit_ssd1306 / `framebuf.FrameBuffer`) |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| `with obj:` / `with a as x, b as y:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `global` / `nonlocal` | Cross-function variable access; `nonlocal` in `@inline` |
| `try / except / else / finally`, `raise`, bare `raise` | AVR + ARM (RP2040/RP2350); zero-cost T-flag propagation (AVR: `SET`/`CLT`/`BRTS`; ARM: an internal flag+code global pair — no `setjmp`/`longjmp` on either); errors propagate across calls to any depth and are caught at the call site; `finally` runs on every exit path (caught, propagated, `return`/`break`/`continue`); unhandled raise prints `"E:TypeName\r\n"` to UART0 then halts, or `"E:TypeName: message\r\n"` when the raise carried one; `except E as e` binds a bounded object -- `print(e)`, `str(e)`, `e.args[0]` read the raise's message (a string literal, or a deferred print of an f-string / concatenation / call, #435) and `isinstance(e, X)` compares the code; an integer raise argument (`raise OSError(110)`, `errno.ETIMEDOUT`, a register read) is stored as the exception's argument and `e.errno`/`e.args[0]`/`e.args`/`len(e.args)` read it, `print(e)` rendering MicroPython's `[Errno n] NAME` -- `e.errno` is refused on a handler that can catch non-OSError types or an integerless raise, at the cost of one module word and one store per raise, emitted only when some handler in the program binds a name; `raise X(...) from Y` compiles as `raise X(...)` (#434) |
| `except` type spellings | A bare name, a tuple of names (`except (A, B):`, #346 — one comparison per alternative into the one handler body), or a module-qualified name (`except adafruit_irremote.IRNECRepeatException:`) in either position; the qualifier resolves through the import, `import mod as x` included, to the code the class was scanned under |
| Integer arithmetic promotion | `+`/`-`/`*`/`<<` promote to the next wider type (`uint8 255 + 45 == 300`); the annotation is a storage width; `uint8(a + b)` is the fixed-width escape hatch; out-of-range literals / folded constants are `CompileError` |
| True division `/` vs `//` | `/` yields `float` (soft-float, warns on integer operands); `//` / `%` are integer floor div / mod; runtime divide-by-zero raises `ZeroDivisionError` |
| f-strings (streamed) | `print(f"...")`, `uart.write_str/println(f"...")`, `lcd.print_str(f"...")` with runtime interpolations and format specs (`{x:02x}`, `{x:08b}`, `{x:04d}`, `{t:.1f}`, `{v:6.3f}`, …); lowered to direct writes, no heap. Unformatted `float` interpolations print MicroPython's real float32 print algorithm (6 to 9 significant digits, round-trip decided); float specs print N decimals with width/zero padding, rounding half-to-even on the exact float32 expansion like CPython |
| `print()` of a buffer | `print(bytearray)`, `print(arr[a:b])` and `print(obj[a:b])` (via `__getitem__`/`__len__`) emit the CPython repr — `bytearray(b'\xcc\x10\xca\xfe')`; the length must be compile-time |
| `print(float)` / `str`/`repr`/unformatted `f"{x}"` | MicroPython's real float32 print algorithm (6 to 9 significant digits, round-trip decided): `0.1`, `3.1415928`, `1e+20` |
| Functions with > 5 arguments | Overflow arguments passed via a fixed SRAM spill region |
| `in` / `not in` | Compile-time fold on constant list; runtime equality chain. A call that returns an instance with `__contains__` dispatches the dunder (`"Linux" not in uname()`, #466); two compile-time strings are substring membership (`"RP2350" in uname().machine`). A name bound to a compile-time string tests a set/list of compile-time strings by text (`ORDER in {RGB, GRB}`, adafruit_neopixel `wheel`) |
| `seq.index(x)` | On a name bound to a compile-time tuple/list: folds when `x` folds, else a compare chain over the elements that raises `ValueError` on a miss (adafruit_tcs34725 `_GAINS.index(val)`) |
| `isinstance(x, T)` | Folds at compile time: a ZCA instance against a class or subclass (#424), or a value against the builtins `tuple`/`list`/`int` from its known shape (#423) -- through an inline-parameter alias, a keyword argument, or a module-level string's own text (adafruit_neopixel `pixel_order`); `isinstance(x, slice)` is always False -- nothing is a runtime slice (adafruit_pixelbuf `__setitem__`); a `None`-bound name answers False to every builtin |
| `is` / `is not` | Maps to `==` / `!=` |
| `divmod(a, b)` | Returns `(quotient, remainder)`: unpacked into two targets (`q, r = divmod(a, b)`), bound to one name (`v = divmod(a, b)`), or printed directly (`print(divmod(a, b))`); a runtime-zero divisor raises `ZeroDivisionError` |
| `bitcast(T, v)` | Reinterpret raw bytes as `T`; float↔uint32; compile-time folding |
| `hex(n)` / `bin(n)` / `oct(n)` | Compile-time constant interns the flash string (`hex(255)` -> `"0xff"`); a runtime value builds the same spelling into a buffer through `pymcu.strfmt`, streamed by `print()` or bound by `s = hex(n)`. Negative values spell the sign before the base prefix, like CPython |
| `round(x)` | Compile-time: folds a compile-time `float`/int half-to-even like CPython |
| `round(x, n)` | `n` a compile-time constant. An int `x` folds at compile time, CPython's own int semantics (`n >= 0` unchanged, `n < 0` rounds to a multiple of `10 ** -n`). A float `x` (constant or run-time) forwards to `pymcu.round2`, half-to-even on the exact decimal expansion of the float32 value -- the same algorithm the f-string float format spec uses |
| `sum(iterable)` / `any(iterable)` / `all(iterable)` | Compile-time fold or unrolled chain |
| `all/any/sum/min/max(x ... for x in it)` | A generator expression as the DIRECT argument of a reduction unrolls at compile time over a known-length iterable (tuple/list literal, const sequence, `range` of constants, compile-time string, fixed-size array). `all`/`any` short-circuit like CPython; `sum` honours `start`; a `for ... if` clause filters (adafruit_pixelbuf). Elsewhere a generator expression is refused, naming the five reductions |
| Compile-time string methods | On a name bound to ONE text (literal, module constant, parameter receiving one -- through `super().__init__` and nested `@inline` calls, `Union[str, ...]` parameters included): `len(s)`, `s[i]`, `needle in s`, `s == "lit"`, `s[a:b]` slices, `str(x)` of a constant, `s.strip()`/`lstrip()`/`rstrip()`, `s.index()`/`s.find()` (miss: catchable `ValueError` / -1), `s.startswith()`/`s.endswith()`, `s.count()`, `s.replace()`, `s.upper()`, `s.lower()` all fold |
| `str(n)` compile-time | `str(42)` → `"42"` string constant |
| A `str` decided at run time | A name the branches of an `if`/`else`, a loop body, a `global` rebind or a CONDITIONAL EXPRESSION bind to different texts holds the interned id in one 16-bit slot; `print` / `uart.write_str` / `println` and `==` / `!=` against a literal dispatch on it, and the texts stay in flash. A conditional expression written straight into a write (`print("mono" if k == 0 else "none")`) lowers as the condition plus a literal write per arm and needs no slot; a condition that folds picks its arm at compile time (#378) |
| `pow(x, n)` / `x ** n` / `math.pow(x, n)` | Compile-time integer fold; runtime integer unroll; runtime float (including a negative integer exponent on a float base) via `__pymcu_powf` (#463). `0.0 ** negative` / `pow(0.0, negative)` raise `ZeroDivisionError`, matching CPython's `**`/`pow()` exactly; `math.pow(0.0, negative)` raises `ValueError`, matching CPython's own `math.pow` domain for the same value |
| `math.sqrt/exp/log/radians(x)` | Software float, run-time argument. `sqrt` is Newton-Raphson after a scale reduction; `log` and `exp` are the two halves of `__pymcu_powf`'s series; `radians` is a scaling multiply that folds for a constant angle. Each body lowers LAZILY, so `import math` with no call costs 0 bytes and a program carries only what it calls. Only these four: each is here because a measured library stops without it (max31865, thermistor, sgp30, mpu6050/lsm6ds). `math.pi` / `math.e` are not defined: a module-level float constant in an imported module is storage nothing initialises |
| `float('inf')` / `float('nan')` | Compile-time constant (case-insensitive, optional sign: "inf", "infinity", "nan"). The target already printed these correctly (`_f32_repr` reads the exponent/mantissa bit pattern); this closed the parsing gap |
| `math.isnan(x)` / `isinf(x)` / `isfinite(x)` | @inline, three bit operations apiece over the same exponent/mantissa split. NaN comparisons (`==` `!=` `<` `<=` `>` `>=`, both as a value and in `if`/`while`) match CPython: every one is False except `!=` |
| Forward-reference annotation `"Name"` | A type named as a string literal (PEP 484), the spelling every Adafruit driver uses for its own `__enter__` return. The quotes come off and the name inside is resolved and checked like any other, in `AnnotationText` so both front ends read it the same way |
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for`, array init, `len()` |
| `bytearray` | Mutable SRAM buffer. A function that fills one and `return`s it is expanded at the call site so the caller indexes the same storage (#464). `memoryview` is a CPython builtin type this compiler stores, so `-> memoryview` is the same view `memoryview()` already wraps. Replaying `name = bytearray(n)` does not undo a `.extend()` that already grew it, so a class-body `_fit(2)` keeps a 3-byte `_BUFFER` |
| `bytes([...])` / `bytes(N)` as a call argument | Written inline at a call site: unrolls into an `@inline` callee's unannotated buffer parameter the same way a list literal does, or lays out a hidden fixed buffer for a `bytearray`/`bytes`-annotated parameter of a real function. `bytes(n)` with a run-time `n` is refused (`bytearray(n)` takes one) |
| `Union[A, B]` on an `@inline`/constructor parameter | Read as the argument's type AT THAT CALL SITE, which must be one of the members -- the same way an `@inline` overload dispatches. A field assigned from it takes the site's type. `List[X]`/`Tuple[X, ...]` matches a fixed array/list literal; `Callable[...]` matches a function reference. A `Protocol` member is structural (#465): a class that has the protocol's members matches even when it is not named as the protocol. A non-matching argument is refused, naming the members. A real subroutine's parameter, or any non-parameter position, keeps the union refusal |
| Annotation alias `Name = Union[...]` | `ColorUnion = Union[int, uint8]` binds the alias at compile time -- including inside a discarded `if TYPE_CHECKING:` / compat-layer guard -- and `x: ColorUnion` resolves it to the same members as the spelled-out union (adafruit_pixelbuf) |
| `input(prompt?, maxlen?)` | `line: bytearray = input("prompt")` — reads newline-terminated line from UART; auto-injects UART init preamble |
| `open(name, mode)` | RFC 0008 romfs — resolves at compile time to a handle over a flash blob (no filesystem on the chip). `name`/`mode` are compile-time strings; read modes only. `read(n)` (compile-time `n`, yields a flash view fusing with `[i]`/`len()`/`struct.unpack`), `readinto`, `readline(max)`, `seek`, `tell`, `close`, `with`. Driver embeds via `[tool.pymcu] files = [...]` or auto-embeds a literal-opened file in the sources |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold or runtime |
| Raw strings `r"\n"` | No escape processing |
| Extended unpacking `first, *rest = tup` | Compile-time tuples only (PEP 3132) |
| Nested list comprehensions | **Neither half of this row is true, measured 2026-09-26.** A comprehension inside a comprehension (`[[f(j) for j in ...] for i in ...]`) is refused. Two `for` clauses in one comprehension (`[a * b for a in X for b in Y]`) compiles and writes zeros, silently: probe `063` in the pymcu-avr corpus holds it against CPython as [#394](https://github.com/PyMCU/PyMCU/issues/394). An `if` filter is refused by name (`a list comprehension with a filter (if) is not supported`). What does work is the single-clause form filling a fixed array whose length is a compile-time constant |
| `for v in [Cls(p) for p in (...)]` | CT unroll of ZCA instance arrays from list comprehensions; plain for-in and enumerate both supported. An element built by a factory call keeps its returned class (`[pcf.get_pin(i) for i in range(8)]`, adafruit_pcf8574) |
| A list given to a class (`Bar([Pin(a), Pin(b)])`, `Bar(pins)`) | Compile-time sequence bound to the parameter and to the `self` field: constant subscript, `for`, `len()`, and a run-time subscript that calls a method (up to 8 elements, lowered as a selection) |
| A list of numbers or a `bytearray` given to a class | The field is another name for the values or the buffer: constant subscript and `for` on the values, run-time indexed load and store on the buffer |
| `str.join` | `sep.join([...])` folds compile-time strings, in expression position too (`print(sep.join([a, b]))`); `''.join([chr(b) for b in buf])` lowers to a runtime string (the MicroPython/CircuitPython bytes-to-string idiom); `sep.join(f"{x:02x}" for x in buf)` -- a generator/comprehension over a compile-time sequence producing f-strings, compile-time strings or `chr(b)` -- streams in `print`/`uart.write_str`/`println` and materializes into a fixed buffer (a runtime string, like an f-string-as-value) elsewhere |
| Slice indexing `arr[1:3]`, `arr[::2]` | READ needs compile-time constant bounds (folding through function-local constants too) and yields a fixed-size array; a slice of SRAM-backed storage marshals to a `bytearray` parameter by base address. Equal-length slice ASSIGNMENT (`arr[a:b] = src`) with list/`bytes`/array/slice sources, incl. overlapping same-array copies (snapshot semantics), through `__setitem__` objects (`nvm[0:4] = b'...'`), a module `bytearray`, an instance-member buffer (`self.buf`), and a run-time start whose length is compile-time (`buf[i:i+n] = bytes(fill)`). ITERATION accepts runtime bounds (`for b in buf[0:n]`); a runtime `step` is a diagnostic |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, comparisons, bitwise. Dispatched wherever the operator is written: as a condition, as a value, at module level, and on a class-typed field receiver |
| Comparing two instances with no comparison dunder | `==` / `!=` / `is` / `is not` fall back to identity, as CPython does; an ordering is refused, and so are `max()` / `min()` over instances |
| `__new__`, `__init_subclass__`, `__del__` | The two construction hooks are refused where they are written; a destructor compiles, never runs, and is warned about |
| `@extern("symbol")` | External C/C++ symbol interop with AVR ABI |
| `__name__` / `if __name__ == "__main__":` | Compile-time guard; body promoted in main, eliminated in libs |
| Triple-quoted strings `"""..."""` / `'''...'''` | Multiline string literals; leading newline after opening quote stripped; useful for multiline `asm()` |
| `list[T]` heap-allocated list | `x: list[uint8] = list()` / `list(N)` / `[a, b, c]`; `append()`, `len()`, `x[i]`, `for v in x:`; bounded bump allocator + GC; suitable for ATmega328P (2 KB SRAM) and larger. A `list[T]` parameter or return also works on a real (non-`@inline`) function, expanded at each call site |
| `import os` / `os.uname()` | Compile-time five-field record of `__CHIP__` (`sysname` `"PyMCU"`, `machine` the chip, with an `RP2040`/`RP2350` token on those parts). `"Linux" not in uname()` and `"RP2350" in uname().machine` fold. `os.name` is `"posix"`, `os.sep` is `"/"`. `stat(name)` / `listdir(dir)` answer from the embedded-file table (RFC 0008) — `stat` returns the ten-field tuple (size at index 6), `listdir` unrolls the names under the prefix, sorted; `getenv` is not defined (#466) |
| Unannotated field first store | A string literal or `bytearray(...)` / `bytes(...)` is that kind, not uint8. `self._message = ""` then a `str` setter and `self._gpio = bytearray(n)` then a buffer setter are the same field; an int then a str is still refused |
| Constant tuple field | `self.scale = (524288, ...)` is the same fixed array as `self.buf = [0, 0, 0]`. Counted as a scalar the class became one-field and `self.scale[n]` was a bit index (adafruit_dps310) |
| `str` parameter text | A compile-time string of any length bound to a `str` parameter keeps its text, so `struct.calcsize(fmt)` folds (`StructArray(0x06, "<HH", 16)` in adafruit_pca9685) |
| `[None] * n` | A repeated list of None (or a constant) is a fixed SRAM array. `coeffs = [None] * 18` and `self.ch = [None] * len(self)` are indexable; None is a 0 slot (adafruit_dps310, adafruit_pca9685) |
| 2-D grid `[[v] * W for _ in range(H)]` | One flat `T[W*H]` array -- `g[y][x]` is `g[y*W+x]` with the same index code the hand-flattened spelling emits. Also `[bytearray(W) for _ in range(H)]` and `self.g = <same>` in `__init__`; both dimensions fold like a fixed array's size (literal, `const` name, module constant, or a constructor argument literal at every call site). `len(g)` is H, `len(g[y])` is W, `for row in g` is a row-index loop, `for x in g[y]` iterates a row, `r = g[y]` binds a row view usable only for `r[x]`/`len(r)`/`for x in r` in the same block. A row is a view, not a value: passing, returning, storing, comparing, `in`, slicing or appending it, rebinding `g[y]`, and `g[a:b]` are all refused; `[[0]*W] * H` is refused because CPython's spelling aliases one row |
| `x = a, b, c` | An unparenthesized comma RHS is a tuple, the same wrap `return a, b` already had. `fill = (color >> 16) & 255, (color >> 8) & 255, color & 255` (adafruit_framebuf) |
| `return a, (b, c, d), e` | A tuple-return element that is itself a fixed literal sequence reaches the caller as a compile-time sequence, not a runtime tuple: `bpp, byteorder_tuple, has_white, dotstar_mode = self.parse_byteorder(...)` unpacks `(r, g, b)` so `byteorder_tuple[i]` folds and `if dotstar_mode:` drops its branch (adafruit_pixelbuf) |
| `buf[i:i+n] = bytes(fill)` | Equal-length slice assign onto a `bytearray` (and onto `self.buf`), with a run-time start whose length is compile-time (`i:i+3`) and `bytes(named_seq)` as the source (adafruit_framebuf RGB888 fill) |
| `"mod.Cls"` annotation | A quoted dotted class is the same type as unquoted `mod.Cls`. `"Vec"` already was the bare name (#261); `"adafruit_si7021.SI7021"` is the dotted spelling (adafruit_si7021) |
| `word[i], crc[i] = unpack(...)` | An IndexExpr unpack binds the RHS to a name then stores `t[k]`. A `struct.unpack` buffer slice may start at a run-time offset (`data[i*6:(i*6)+6]`) (adafruit_sht31d) |
| `@classmethod` | Compile-time class-namespace population: `cls` is the receiver class. `setattr(cls, name, value)`, `cls.attr = {}` and `cls.attr[k] = v` fill that class; `return cls()` constructs it (adafruit_sht4x / tmp117 `CV.add_values`) |
| `self.prop[k]` on a tuple `@property` | A getter that returns a tuple is `f()[k]`. `return self.measurements[0]` from `temperature` is the first slot (adafruit_sht4x) |
| `self.buf[a:b]` | A field bytearray slices the same way a named `buf[a:b]` does. `temp_data = self._buffer[0:2]` (adafruit_sht4x) |
| `class C(mod.Base)` + `super()` | An imported dotted base unwraps the module alias. `import adafruit_framebuf as framebuf` then `class _SSD1306(framebuf.FrameBuffer)` expands `super().__init__` (adafruit_ssd1306) |
| `self.x = ...` inside a base `__init__` `if` | A super-expanded base constructor is still `__init__`. `self.format = MVLSBFormat()` in `FrameBuffer.__init__` is a constructor field, not a missing field of the subclass (adafruit_ssd1306) |
| `super().__init__(reset=None)` | A None argument through super() is still None. `if self.reset_pin:` folds and the guarded DigitalInOut use is not lowered (adafruit_ssd1306) |
| `for x in (NAME, a if c else b, self.n - 1)` | A for-in tuple element folds like any other constant: a `const` name, a field ternary, a comparison ternary, or field arithmetic (adafruit_ssd1306 `init_display`) |
| Parameter shadows `import ... as` | A parameter of the same name as an import alias is the parameter. `def set_pixel(framebuf, ...): framebuf.stride` after `import adafruit_framebuf as framebuf` is the instance field. A class-body function with no `self` that reads a parameter field expands at the call site so that class is visible (adafruit_ssd1306 `MVLSBFormat.set_pixel`) |
| Field array list comprehension | `obj.buf = [x for i in range(len(obj.buf))]` fills the field array already bound. A list comprehension as a value is still refused (adafruit GS2HMSBFormat.fill) |
| `if buf_format == MVLSB` in `__init__` | When the constructor is expanded with a bound constant, only that format class is assigned. A local that holds that constant (`_FRAMEBUF_FORMAT = MVLSB`) forwarded through `super().__init__` is still a constant, so the I2C subclass keeps `MVLSBFormat`. A no-self method that forwards the buffer to a sibling (`GS2HMSBFormat.rect` -> `set_pixel(framebuf, ...)`) expands at the call site so an unused format class is not compiled (adafruit_framebuf) |
| `memoryview(buf)[k:]` as a value | A writable window of `buf` (offset + shorter `len`), not a copy. `super().__init__(memoryview(self.buffer)[1:])` then a second hop `super().__init__(buffer, ...)` keeps the window, so `len(framebuf.buf)` / `framebuf.buf[i] = fill` writes `buffer[1:]` and leaves the I2C command byte. A plain `buf[a:b]` is still a copy (adafruit_ssd1306 / sht4x) |
| TYPE_CHECKING inner `except NotImplementedError` | The try body's import stays in scope. `from pwmio import PWMOut` is not dropped, and the stub handler is not loaded (#480, #481) |
| `for p in (inst, inst)` | A tuple or list of already-constructed ZCA instances unrolls the same way `for p in self._pins` does. `pin.direction = OUTPUT` through the loop variable is the `@property` setter (adafruit_character_lcd) |
| `bytearray(self.field)` | A field that holds a compile-time integer is a compile-time size (adafruit_74hc595's `self._gpio = bytearray(self._number_of_shift_registers)`) |
| Local class vs imported name | A class defined in a module shadows an import of the same name from another module. `DigitalInOut(pin, self)` in adafruit_74hc595 is that file's two-argument class, even when main imported `digitalio.DigitalInOut` |
| Constructor not outlined | `__init__` is expanded at each construction. A class-typed parameter is the argument's class, not the annotation (`Lcd(mcp.get_pin(1), ...)` annotated `digitalio.DigitalInOut` is still the expander pin). A plain function whose body returns a construction (`def I2C(): return _board_i2c(SCL, SDA)`, the generated `board.py`) expands the same way a declared `-> busio.I2C` factory does, so `i2c = board.I2C()` then `b.try_lock()` on `b: busio.I2C` resolves to the class, not the receiver's name |
| Rebound module alias | `from adafruit_motor import servo` then `servo = servo.Servo(pwm)` rebinds the name; later reads and calls see the instance, not the module (#467) |
| `time.struct_time` | Stdlib stub with the nine CPython field names, so Adafruit RTC `from time import struct_time` in a typing try does not fail |
| `collections.namedtuple` | Compile-time class factory: `Name = namedtuple("Name", ("a", "b"))` becomes a ZCA class with those fields, `__len__` and `__match_args__`. The bound name is the class (adafruit_irremote's `IRMessage`) |
| Arena allocator for runtime-sized `bytearray(n)` | Allocates from a static arena (no `free()`) wherever the compiler can prove the statement runs at most once (a module-level statement not in a loop, or an `@inline __init__` reached only through inlining from one); refused elsewhere, naming the reason. `x[i]`, `x[i] = v`, `x[i] OP= v`, `x[-k]`, `len(x)`, both as a local and as an `@inline __init__`'s field, through further `@inline` method calls; `for b in x` iterates the bytes when the buffer reaches the loop through an `@inline` parameter binding or a field (`self._post_brightness_buffer` into `neopixel_write`); new `MemoryError` on overflow; `pymcu build` reports the reservation. AVR only. See `docs/rfcs/0004-arena-allocator.md` |
| Closed `dict` / `set` literals | `d = {0: 10, "mid": 2}` / `OK = {1, 3, 5}` bind compile-time lookup tables with no storage: `d[const]` folds, `d[runtime]` compare-chains and raises `KeyError`, `x in d` and `len(d)` fold. A class-body dict (`self.gain_values[gain]`) is the same table, including mixed int/float values. Read-only |
| `pymcu.collections.FixedDict` | Mutable fixed-capacity integer dict — open addressing over per-instance fixed arrays, no heap and no GC |
| f-string as a **value** | `s = f"t={t} C"` builds into a compiler-managed fixed `bytearray`; `len(s)`, `s[i]` (the one-character string at that position, so `print(s[i])` writes the character), `print(s)`, buffer reuse on re-assignment. `s = f"{s}..."` (self-interpolation) works too, via a private snapshot buffer. Float format specs (`{v:.2f}`) work here too, and an int spec (`{v:X}`, `{v:04d}`, `{v:b}`, `{v:o}`) folds to its text wherever the whole f-string is a compile-time constant |
| `async def` / `await`, generators (`yield`) | Lowered to a zero-cost state-machine class with `poll()`; `await asyncio.sleep/sleep_ms` anywhere in the body; executors `asyncio.run` / `asyncio.gather`; `for x in gen(...)` desugars to a poll loop. `yield from` delegates, and a generator method works through a bound receiver |
| Type inference for unannotated `def` params/returns | Outlined functions join the call-site evidence the front end can type, defaults and return expressions (safe integer widening); class methods join the return side, so `self.x = self._m()` reads `_m`'s inferred return type as field-width evidence (#489). That evidence alone still left `uint8` wherever an argument or a return could not be typed before lowering (`f(GPIOR0.value + 900)` printed 132), so every store into an unannotated parameter, return, local, field or global is now checked against the width chosen for it, and one that does not fit compiles the program again with that slot widened -- signed when the value can be negative. An unannotated `@inline` parameter takes its run-time argument's width, and an annotated one holds the argument converted to its declared width, as a real subroutine's does (`@inline def f(n: uint8)` called with 300 read 300). What it does not cover is under "Unannotated widths" in limitations.md |
| Value-returning methods on nested ZCA fields | `self.pin.read()` on a class-typed field dispatches through facade re-exports and single-level inheritance — the shape the compat layers are built on |
| `def f(): return C(...)` returning a multi-field ZCA | A plain function whose return is a ZCA construction force-inlines at the call site, the same rule as a ZCA-typed parameter: `board.I2C()` -> `_board_i2c(SCL, SDA)` keeps every field store the constructor emits (adafruit_ssd1306). A single-field class still returns its register-packed handle; a declared `-> C` still lowers sret |
| `f"..." "literal"` implicit concatenation | An f-string adjacent to plain literals folds into one `JoinedStr`, the same merge CPython's parser makes — the spelling of `adafruit_seesaw`'s chip-id raise; as a deferred raise message the merged parts replay like `print` |
| `buf += src` on a fixed `bytearray` | In-place concat: the buffer's compile-time size grows by the source's length and the source bytes store into the new tail (`full_buffer += buf` in `adafruit_seesaw.write`). The `+=` must sit in the same run-time branch context as the buffer's declaration; anything else is refused, naming why |
| `buf[a:] = src` open-ended slice assign | An open-ended slice takes its length from the source's compile-time length; the start may be run-time (`cmd[offset:] = struct.pack(">I", pins)` in `adafruit_seesaw`) |
| `struct.pack(fmt, v...)` as a value | `name = struct.pack(fmt, v...)` binds a fixed `bytearray` of `calcsize` bytes — `pack_into`'s writes onto a fresh name — and works as a slice-assign source. `struct` covers the 4-byte codes `I`/`i`/`L`/`l` under `<`/`>`/`!` |
| `try/except ImportError` in a function-scope-imported module | A module discovered through an import inside a function body gets the same optional-import marking the dependency graph applies at top level, so `adafruit_seesaw`'s `micropython.const` pinmap idiom folds instead of failing on the stub handler |

### MCU extensions

| Feature | Notes |
|---|---|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Annotation for variables; an unannotated `def` param/return takes the width of what its calls and returns store into it (call-site inference, then a recompile for any store that does not fit) |
| `int` (built-in) | Maps to `int16`; no import required |
| `ptr[T]` / `ptr(addr)` | Memory-mapped I/O. The declaration works at module level, inside a class body, and on an instance field (`self.reg: ptr[uint8] = TCCR1B`). A class body is what a grouped peripheral is (RFC 0012): `class Timer1: TCCR1A: ptr[uint8] = ptr(TCCR1A)` and then `Timer1.TCCR1A.value = 0x82`, `Timer1.TCCR1B[Timer1.CS10] = 1`. The class has no runtime existence and the grouped spelling compiles to the same bytes as the loose one. The element width travels with the address through fields, parameters and return types, so `.value` is always an access of the declared width. An address the compiler cannot resolve while compiling is a located error, and so is `ptr()` of an array |
| `const[T]` / `const[uint8[N]]` | Compile-time constants, integer / string / **float** (`Timer(freq=2.5)`); flash-resident arrays via `LPM Z`. A runtime-varying argument is a located `CompileError`, not a silent fold |
| `asm("instr")` | Inline assembly with register constraints `%N` |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic busy-wait |
| `millis()` / `micros()` | Timer0 overflow; atomic 32-bit read under CLI/SEI. `millis()` carries the Arduino-style fractional correction (an overflow is 1024 µs, not 1000 µs); `micros()` is monotonic across an overflow |
| `@inline` | Zero-cost expansion |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion. A tuple-returning getter indexes like `f()[k]` (`self.measurements[0]`, adafruit_sht4x) |
| `__CHIP__` | Conditional compilation by chip name / architecture |
| `sys.implementation` / `sys.platform` / `os.uname()` in `if` / `match` | When the project declares `stdlib = ["circuitpython"]` or `["micropython"]`, these fold to the answers a real board of that layer reports (RFC 0007, PyMCU#266) |
| `__FREQ__` | Compile-time clock frequency in Hz |
| `[tool.pymcu.ffi]` build config | C/C++ interop: `sources`, `include_dirs`, `cflags` |
| `float` (soft-float) | IEEE 754 single-precision; AVR (`__fp_*` intrinsics) and RP2040 (bootrom fast-float library via `__aeabi_f*` shims); annotation `x: float = 3.14`; float↔int conversions truncate toward zero. RP2350 pending (M33 FPU) |
| `@naked` | No compiler prolog/epilog; registers hold raw calling-convention values at function entry; required for precise `uint16` register manipulation |
| `@classmethod` | Compile-time class-namespace population: `cls` is the receiver class. `Class.method(args)` expands with `setattr(cls, name, value)`, `cls.attr = {}` and `cls.attr[k] = v` filling that class (adafruit_sht4x / tmp117 `CV.add_values`). `return cls()` constructs the receiver class. `cls` is not a runtime object |
| `@staticmethod` | Accepted and ignored: what makes a method callable through the class is having no `self` parameter. `def f(x)` in a class body compiles as `Class_f` and is reached by `A.f(x)` or by `obj.f(x)`, neither of which consumes the argument. A method that DOES take `self` cannot be called as `A.f(x)` and is refused where it is written, not at the linker (PyMCU#201) |
| `CompileError` intrinsic | `raise CompileError("msg")` aborts compilation with a `CompileError:` diagnostic; never generates `RaiseExn` IR; used in all HAL modules for unsupported arch/chip guards; cannot be caught by `try/except` |

### HAL (ATmega328P)

| Module | Coverage |
|---|---|
| `pymcu.hal.gpio` | `Pin` — `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART`: `write` (`uint8` or `bytearray`, so a bytes literal or fixed buffer sends one byte at a time, like the MicroPython compat layer's `machine.UART.write` already did) `/read/read_line/write_str/println/print_byte/available` + RX interrupt |
| `pymcu.hal.adc` | `AnalogPin` — poll + interrupt; channels `"PC0"`–`"PC5"`, `"TEMP"` (internal sensor), `"VBG"`, `"ADC8"` |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — Timer0/1/2 unified; CTC mode |
| `pymcu.hal.pwm` | `PWM` — `start/stop/set_duty/set_freq`; multi-channel (two channels of the same timer coexist — the COM bits are OR-ed). `set_freq` picks the **nearest** reachable prescaler bucket |
| `pymcu.hal.spi` | `SPI` + `SoftSPI` |
| `pymcu.hal.i2c` | `I2C` + `SoftI2C`; `write_to` / `read_from` / `write_bytes` / `writeto_mem` / `readfrom_mem`; internal pull-ups on SDA/SCL on by default, as Arduino (`pullups=False` opts out) |
| `pymcu.hal.eeprom` | `EEPROM` — `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` — `enable/disable/feed` |
| `pymcu.hal.power` | `sleep_idle` / `sleep_adc_noise` / `sleep_power_down` / `sleep_power_save` / `sleep_standby` / `sleep_extended_standby` |

### Drivers

| Module | Device |
|---|---|
| `pymcu.drivers.dht11` | DHT11 temperature + humidity |
| `pymcu.drivers.ds18b20` | DS18B20 1-Wire precision temperature (12-bit) |
| `pymcu.drivers.lcd` | HD44780 LCD (4-bit parallel) — class `LCD` |
| `pymcu.drivers.ssd1306` | SSD1306 OLED (I2C, 128×64) |
| `pymcu.drivers.max7219` | MAX7219 8×8 LED matrix (SPI) |
| `pymcu.drivers.bmp280` | BMP280 barometer (I2C) |
| `pymcu.drivers.neopixel` | WS2812 NeoPixel |

There is no LM35 driver in the core stdlib: an LM35 is a plain analog sensor, so it is
read directly with `AnalogPin`. The `pymcu-micropython` compat package does ship an
`lm35` module.

### Compatibility layers

| Package | Activation | Coverage |
|---------|-----------|----------|
| `pymcu-micropython` | `stdlib = ["micropython"]` | `machine` (Pin, UART, ADC — pin or channel number, PWM with `freq()`/`duty_u16()` getters, SPI, I2C, `SoftI2C`, `Timer(id, period, callback)`), `utime`, `micropython` |
| `pymcu-circuitpython` | `stdlib = ["circuitpython"]` | `board`, `digitalio`, `analogio`, `busio` (SPI + I2C), `pwmio`, `time`, `supervisor`, `alarm`, `microcontroller` (`cpu`, `nvm`, `watchdog`, `reset_reason`) |

### Boards

| Module | Pins |
|---|---|
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |
| `pymcu.boards.arduino_mega` | `D0`–`D53`, `A0`–`A15`, `LED_BUILTIN` |
| `pymcu.boards.arduino_leonardo` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |

The pin-constant module is named after the Leonardo, but the ATmega32U4 board key
accepted by the CLI (`pymcu build --board`) is `arduino_micro`.

---

## RP2040 (alpha)

The **RP2040** (Raspberry Pi Pico, ARM Cortex-M0+) backend is implemented in **alpha**.

The reason is philosophical: the RP2040 is the most popular MicroPython target today.
PyMCU's promise is *prototype fast in MicroPython, bring to the metal with PyMCU* — the
same source file that runs on a Pico under MicroPython should compile to bare-metal
firmware with zero runtime when you are ready to ship. RP2040 closes that loop for the
largest audience of MicroPython users.

Unlike the AVR/PIC/RISC-V backends, the RP2040 backend does **not** emit assembly
directly. It lowers PyMCU's architecture-agnostic IR to **LLVM IR**, so LLVM handles
register allocation, instruction selection, the AAPCS calling convention and all
optimization passes for `thumbv6m-none-eabi`. `pymcu build` produces a flat flash
image (`firmware.bin`); the build is verified end-to-end against the RP2040Sharp
emulator (`pip install pymcu[rp2040]`, requires LLVM on the host).

| Feature | Status |
|---|---|
| GPIO (`pymcu.hal.gpio.Pin`) | ✅ Single-cycle IO (SIO); zero-cost; all 30 GPIOs |
| UART0 (`pymcu.hal.uart.UART`) | ✅ PL011; compile-time baud divisors |
| `delay_ms` / `delay_us` | ✅ Hardware TIMER (1 MHz); accurate on silicon |
| Single core (core 0) | ✅ |
| Dual-core / SIO FIFO | ⏳ Planned |
| PIO, SPI, I2C, PWM, ADC, USB | ⏳ Planned |
| GC (`list[T]`), exceptions, soft-float | ⏳ Not yet on this backend |

## Planned

| Feature | Notes |
|---|---|
| RP2040 peripherals | SPI / I2C / PWM / ADC / PIO / USB; dual-core launch |
| `fixed16` (Q8.8 fixed-point) | Fixed-point arithmetic without soft-float overhead; `Q8.8` format |
| MicroPython/CircuitPython API alignment | Broaden compat module coverage; close remaining API gaps |
| PIC18 codegen | Extend backend for PIC18Fxxxx family |
| RISC-V 32-bit codegen (publishing) | The CH32V003/V203 backend builds in-tree but is not on PyPI and has no install extra. Also open: it does not truncate to the declared width (PyMCU#222) |
| RP2040 PIO backend | Programmable I/O state machine output |
| Over-the-air (OTA) support | Bootloader + `pymcu flash` over UART |
| ARM Cortex-M3/M4 codegen | STM32, nRF52 — reuses the LLVM backend |

---

## Not planned

| Feature | Reason |
|---|---|
| **Mutable** `dict` / `set` | Dynamic hash tables require heap. Closed literals (read-only lookup tables: `d[k]`, `x in d`, `len(d)`, `KeyError` on missing runtime key) ARE supported |
| Garbage collection beyond `list[T]` | Full GC incompatible with deterministic ISR timing |
| `await` on another coroutine, `await` as an expression | The compile-time state machine (v2) covers `await asyncio.sleep/sleep_ms` anywhere in the body — `if`/`elif`/`else`, `while`, `for`, `break`/`continue`, `return expr` — plus `asyncio.run`/`gather`. A sub-future needs ZCA construction outside `__init__`, which is the remaining gap |
| `f"..."` inline in arbitrary expressions | Streaming (`print(f"...")`) and assignment (`s = f"..."` — built into a fixed buffer, no heap) are supported; other expression positions have no lowering — assign to a name first |
| Closures capturing mutable vars | `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` over a run-time call | The forms are compile-time sequences and mappings: the callee is specialised per call site, so the extra arguments are known there and splice into the callee's named parameters, `super().__init__` included. A `*seq` argument expands a literal, a name bound to a constant sequence, a member held as one, and a call or descriptor read that returns a tuple (`str.format(*seq)` is the same splice). A `**` built from a run-time mapping is refused |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info. **One compile-time form IS supported:** `getattr(mod, "name", default)` on a module folds to the member or the default (CircuitPython's `getattr(board, "SCK", ...)`); the name must be a literal and the receiver a module -- anything else is refused |
| `eval()` / `exec()` | No interpreter on MCU |
