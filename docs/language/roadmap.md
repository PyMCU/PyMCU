# Roadmap

This page tracks which language and HAL features have been implemented, and what is planned next.

---

## Implemented

### Language

| Feature | Notes |
|---|---|
| `if / elif / else` | Compile-time DCE on `__CHIP__` branches |
| `while` + `break` / `continue` | |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)`. The counter is as wide as the bounds need: 8-bit for `range(n)` with `n: uint8`, 16-bit for `range(300)`, signed for `range(200, -1, -1)`; a declared type on the loop variable is used as written. A constant range of at most 8 steps unrolls. After the loop the variable holds the last value visited, as in Python |
| `for x in array` / `for x in [1, 2, 3]` | Fixed-size array or constant list literal |
| `for x in named` where `named = [...]` / `(...)` | List and tuple alike, at any length: up to 8 constant elements the loop unrolls against the literal, past that the name gets a fixed array. The element width comes from the widest element |
| `for x in ["PD2", "PD3"]` / `for x in (board.D2, board.D3)` | A constant list of STRINGS unrolls too, and the loop variable binds as a string constant, so a `const` parameter receiving it resolves as it would from a literal. Also the pair form, `for pin, name in [(board.D2, "D2"), ...]` |
| `for i, x in enumerate(iterable)` | Compile-time index counter; `enumerate(range(...))` with runtime bounds keeps a runtime index |
| `for x, y in zip(a, b)` | Compile-time unroll over paired lists |
| `reversed(iterable)` | Compile-time reverse unroll; `reversed(range(...))` is the descending range |
| `match / case` | Literal, wildcard, OR (`\|`), guard `if cond`, sequence, capture, dotted-name patterns; DCE on `__CHIP__` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return (a tuple-returning function force-inlines; annotated `-> (T1, T2)`, `-> tuple[T1, T2]` or `-> Tuple[T1, T2]`). Buffer parameters may be annotated `bytearray`, `WriteableBuffer` or `ReadableBuffer` |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level statements |
| Module-level `main()` (bare, or under `if __name__ == "__main__":`) | Says where the entry point's body runs: what is written after the call runs after the body. A second call, and an early `return` with module-level code after the call, are refused |
| `class` | ZCA `@inline` flattening, constructors, `@property` / `@name.setter`; a class attribute whose class defines `__get__`/`__set__` is a descriptor, and `obj` is the owning instance even when annotated with a typing-only name (#360, #419); `type(inst)` in that rewrite is the source class name, including for an imported class; `value: Any` on `__set__` is the written value, not a missing width; `value <<= n` keeps `value` so a later read of it is the shifted bits |
| Nested `class` | Constructible, and its constants readable through both names: `Outer.Inner.A`, and `mod.Outer.Inner.A` through the declaring module (`busio.UART.Parity.ODD`) |
| Single-level class inheritance | ZCA base + derived; `super()` calls |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| `with obj:` / `with a as x, b as y:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `global` / `nonlocal` | Cross-function variable access; `nonlocal` in `@inline` |
| `try / except / else / finally`, `raise`, bare `raise` | AVR + ARM (RP2040/RP2350); zero-cost T-flag propagation (AVR: `SET`/`CLT`/`BRTS`; ARM: an internal flag+code global pair — no `setjmp`/`longjmp` on either); errors propagate across calls to any depth and are caught at the call site; `finally` runs on every exit path (caught, propagated, `return`/`break`/`continue`); unhandled raise prints `"E:TypeName\r\n"` to UART0 then halts; `except E as e` binds a bounded object -- `print(e)`, `str(e)`, `e.args[0]` read the raise's message (a string literal, or a deferred print of an f-string / concatenation / call, #435) and `isinstance(e, X)` compares the code, at the cost of one module word and one store per raise, emitted only when some handler in the program binds a name; `raise X(...) from Y` compiles as `raise X(...)` (#434) |
| Integer arithmetic promotion | `+`/`-`/`*`/`<<` promote to the next wider type (`uint8 255 + 45 == 300`); the annotation is a storage width; `uint8(a + b)` is the fixed-width escape hatch; out-of-range literals / folded constants are `CompileError` |
| True division `/` vs `//` | `/` yields `float` (soft-float, warns on integer operands); `//` / `%` are integer floor div / mod; runtime divide-by-zero raises `ZeroDivisionError` |
| f-strings (streamed) | `print(f"...")`, `uart.write_str/println(f"...")`, `lcd.print_str(f"...")` with runtime interpolations and format specs (`{x:02x}`, `{x:08b}`, `{x:04d}`, …); lowered to direct writes, no heap. `float` interpolations print two rounded decimals |
| `print()` of a buffer | `print(bytearray)`, `print(arr[a:b])` and `print(obj[a:b])` (via `__getitem__`/`__len__`) emit the CPython repr — `bytearray(b'\xcc\x10\xca\xfe')`; the length must be compile-time |
| `print(float)` | Two rounded decimals, trailing zero trimmed but never past the first: `3.25`, `-2.25`, `0.05`, `123.75`, `1234.5` |
| Functions with > 5 arguments | Overflow arguments passed via a fixed SRAM spill region |
| `in` / `not in` | Compile-time fold on constant list; runtime equality chain. A call that returns an instance with `__contains__` dispatches the dunder (`"Linux" not in uname()`, #466); two compile-time strings are substring membership (`"RP2350" in uname().machine`)
| `isinstance(x, T)` | Folds at compile time: a ZCA instance against a class or subclass (#424), or a value against the builtins `tuple`/`list`/`int` from its known shape (#423) |
| `is` / `is not` | Maps to `==` / `!=` |
| `divmod(a, b)` | Returns `(quotient, remainder)` |
| `bitcast(T, v)` | Reinterpret raw bytes as `T`; float↔uint32; compile-time folding |
| `hex(n)` / `bin(n)` | Compile-time: `hex(255)` → `"0xff"` |
| `sum(iterable)` / `any(iterable)` / `all(iterable)` | Compile-time fold or unrolled chain |
| `str(n)` compile-time | `str(42)` → `"42"` string constant |
| `pow(x, n)` / `x ** n` / `math.pow(x, n)` | Compile-time integer fold; runtime integer unroll; runtime float via `__pymcu_powf` (#463) |
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for`, array init, `len()` |
| `bytearray` | Mutable SRAM buffer. A function that fills one and `return`s it is expanded at the call site so the caller indexes the same storage (#464). `memoryview` is a CPython builtin type this compiler stores, so `-> memoryview` is the same view `memoryview()` already wraps. Replaying `name = bytearray(n)` does not undo a `.extend()` that already grew it, so a class-body `_fit(2)` keeps a 3-byte `_BUFFER` |
| `bytes([...])` / `bytes(N)` as a call argument | Written inline at a call site: unrolls into an `@inline` callee's unannotated buffer parameter the same way a list literal does, or lays out a hidden fixed buffer for a `bytearray`/`bytes`-annotated parameter of a real function. `bytes(n)` with a run-time `n` is refused (`bytearray(n)` takes one) |
| `Union[A, B]` on an `@inline`/constructor parameter | Read as the argument's type AT THAT CALL SITE, which must be one of the members -- the same way an `@inline` overload dispatches. A field assigned from it takes the site's type. `List[X]`/`Tuple[X, ...]` matches a fixed array/list literal; `Callable[...]` matches a function reference. A `Protocol` member is structural (#465): a class that has the protocol's members matches even when it is not named as the protocol. A non-matching argument is refused, naming the members. A real subroutine's parameter, or any non-parameter position, keeps the union refusal |
| `input(prompt?, maxlen?)` | `line: bytearray = input("prompt")` — reads newline-terminated line from UART; auto-injects UART init preamble |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold or runtime |
| Raw strings `r"\n"` | No escape processing |
| Extended unpacking `first, *rest = tup` | Compile-time tuples only (PEP 3132) |
| Nested list comprehensions | Full outer × inner product unroll; `if` filter supported |
| `for v in [Cls(p) for p in (...)]` | CT unroll of ZCA instance arrays from list comprehensions; plain for-in and enumerate both supported |
| A list given to a class (`Bar([Pin(a), Pin(b)])`, `Bar(pins)`) | Compile-time sequence bound to the parameter and to the `self` field: constant subscript, `for`, `len()`, and a run-time subscript that calls a method (up to 8 elements, lowered as a selection) |
| A list of numbers or a `bytearray` given to a class | The field is another name for the values or the buffer: constant subscript and `for` on the values, run-time indexed load and store on the buffer |
| `str.join` | `s = sep.join([...])` folds compile-time strings; `s = ''.join([chr(b) for b in buf])` lowers to a runtime string (the MicroPython/CircuitPython bytes-to-string idiom). Outside an assignment it is a diagnostic |
| Slice indexing `arr[1:3]`, `arr[::2]` | READ needs compile-time constant bounds and yields a fixed-size array. Equal-length slice ASSIGNMENT (`arr[a:b] = src`) with list/`bytes`/array/slice sources, incl. overlapping same-array copies (snapshot semantics), through `__setitem__` objects (`nvm[0:4] = b'...'`), a module `bytearray`, an instance-member buffer (`self.buf`), and a run-time start whose length is compile-time (`buf[i:i+n] = bytes(fill)`). ITERATION accepts runtime bounds (`for b in buf[0:n]`); a runtime `step` is a diagnostic |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, comparisons, bitwise |
| `@extern("symbol")` | External C/C++ symbol interop with AVR ABI |
| `__name__` / `if __name__ == "__main__":` | Compile-time guard; body promoted in main, eliminated in libs |
| Triple-quoted strings `"""..."""` / `'''...'''` | Multiline string literals; leading newline after opening quote stripped; useful for multiline `asm()` |
| `list[T]` heap-allocated list | `x: list[uint8] = list()` / `list(N)` / `[a, b, c]`; `append()`, `len()`, `x[i]`, `for v in x:`; bounded bump allocator + GC; suitable for ATmega328P (2 KB SRAM) and larger. A `list[T]` parameter or return also works on a real (non-`@inline`) function, expanded at each call site |
| `import os` / `os.uname()` | Compile-time five-field record of `__CHIP__` (`sysname` `"PyMCU"`, `machine` the chip, with an `RP2040`/`RP2350` token on those parts). `"Linux" not in uname()` and `"RP2350" in uname().machine` fold. `os.name` is `"posix"`, `os.sep` is `"/"`. `listdir` / `getenv` / `stat` are not defined (#466) |
| Unannotated field first store | A string literal or `bytearray(...)` / `bytes(...)` is that kind, not uint8. `self._message = ""` then a `str` setter and `self._gpio = bytearray(n)` then a buffer setter are the same field; an int then a str is still refused |
| Constant tuple field | `self.scale = (524288, ...)` is the same fixed array as `self.buf = [0, 0, 0]`. Counted as a scalar the class became one-field and `self.scale[n]` was a bit index (adafruit_dps310) |
| `str` parameter text | A compile-time string of any length bound to a `str` parameter keeps its text, so `struct.calcsize(fmt)` folds (`StructArray(0x06, "<HH", 16)` in adafruit_pca9685) |
| `[None] * n` | A repeated list of None (or a constant) is a fixed SRAM array. `coeffs = [None] * 18` and `self.ch = [None] * len(self)` are indexable; None is a 0 slot (adafruit_dps310, adafruit_pca9685) |
| `x = a, b, c` | An unparenthesized comma RHS is a tuple, the same wrap `return a, b` already had. `fill = (color >> 16) & 255, (color >> 8) & 255, color & 255` (adafruit_framebuf) |
| `buf[i:i+n] = bytes(fill)` | Equal-length slice assign onto a `bytearray` (and onto `self.buf`), with a run-time start whose length is compile-time (`i:i+3`) and `bytes(named_seq)` as the source (adafruit_framebuf RGB888 fill) |
| TYPE_CHECKING inner `except NotImplementedError` | The try body's import stays in scope. `from pwmio import PWMOut` is not dropped, and the stub handler is not loaded (#480, #481) |
| `for p in (inst, inst)` | A tuple or list of already-constructed ZCA instances unrolls the same way `for p in self._pins` does. `pin.direction = OUTPUT` through the loop variable is the `@property` setter (adafruit_character_lcd) |
| `bytearray(self.field)` | A field that holds a compile-time integer is a compile-time size (adafruit_74hc595's `self._gpio = bytearray(self._number_of_shift_registers)`) |
| Local class vs imported name | A class defined in a module shadows an import of the same name from another module. `DigitalInOut(pin, self)` in adafruit_74hc595 is that file's two-argument class, even when main imported `digitalio.DigitalInOut` |
| Constructor not outlined | `__init__` is expanded at each construction. A class-typed parameter is the argument's class, not the annotation (`Lcd(mcp.get_pin(1), ...)` annotated `digitalio.DigitalInOut` is still the expander pin) |
| Rebound module alias | `from adafruit_motor import servo` then `servo = servo.Servo(pwm)` rebinds the name; later reads and calls see the instance, not the module (#467) |
| `time.struct_time` | Stdlib stub with the nine CPython field names, so Adafruit RTC `from time import struct_time` in a typing try does not fail |
| `collections.namedtuple` | Compile-time class factory: `Name = namedtuple("Name", ("a", "b"))` becomes a ZCA class with those fields, `__len__` and `__match_args__`. The bound name is the class (adafruit_irremote's `IRMessage`) |
| Closed `dict` / `set` literals | `d = {0: 10, "mid": 2}` / `OK = {1, 3, 5}` bind compile-time lookup tables with no storage: `d[const]` folds, `d[runtime]` compare-chains and raises `KeyError`, `x in d` and `len(d)` fold. A class-body dict (`self.gain_values[gain]`) is the same table, including mixed int/float values. Read-only |
| `pymcu.collections.FixedDict` | Mutable fixed-capacity integer dict — open addressing over per-instance fixed arrays, no heap and no GC |
| f-string as a **value** | `s = f"t={t} C"` builds into a compiler-managed fixed `bytearray`; `len(s)`, `s[i]`, `print(s)`, buffer reuse on re-assignment. No float interpolations in this form |
| `async def` / `await`, generators (`yield`) | Lowered to a zero-cost state-machine class with `poll()`; `await asyncio.sleep/sleep_ms` anywhere in the body; executors `asyncio.run` / `asyncio.gather`; `for x in gen(...)` desugars to a poll loop |
| Type inference for unannotated `def` params/returns | Outlined functions join call-site evidence, defaults and return expressions (safe integer widening) instead of defaulting to `uint8` |
| Value-returning methods on nested ZCA fields | `self.pin.read()` on a class-typed field dispatches through facade re-exports and single-level inheritance — the shape the compat layers are built on |

### MCU extensions

| Feature | Notes |
|---|---|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Annotation for variables; unannotated `def` params/returns of outlined functions are inferred from call sites (v0.14) |
| `int` (built-in) | Maps to `int16`; no import required |
| `ptr[T]` / `ptr(addr)` | Memory-mapped I/O |
| `const[T]` / `const[uint8[N]]` | Compile-time constants, integer / string / **float** (`Timer(freq=2.5)`); flash-resident arrays via `LPM Z`. A runtime-varying argument is a located `CompileError`, not a silent fold |
| `asm("instr")` | Inline assembly with register constraints `%N` |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic busy-wait |
| `millis()` / `micros()` | Timer0 overflow; atomic 32-bit read under CLI/SEI. `millis()` carries the Arduino-style fractional correction (an overflow is 1024 µs, not 1000 µs); `micros()` is monotonic across an overflow |
| `@inline` | Zero-cost expansion |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion |
| `__CHIP__` | Conditional compilation by chip name / architecture |
| `__FREQ__` | Compile-time clock frequency in Hz |
| `[tool.pymcu.ffi]` build config | C/C++ interop: `sources`, `include_dirs`, `cflags` |
| `float` (soft-float) | IEEE 754 single-precision; AVR (`__fp_*` intrinsics) and RP2040 (bootrom fast-float library via `__aeabi_f*` shims); annotation `x: float = 3.14`; float↔int conversions truncate toward zero. RP2350 pending (M33 FPU) |
| `@naked` | No compiler prolog/epilog; registers hold raw calling-convention values at function entry; required for precise `uint16` register manipulation |
| `@staticmethod` | Accepted and ignored: what makes a method callable through the class is having no `self` parameter. `def f(x)` in a class body compiles as `Class_f` and is reached by `A.f(x)` or by `obj.f(x)`, neither of which consumes the argument. A method that DOES take `self` cannot be called as `A.f(x)` and is refused where it is written, not at the linker (PyMCU#201) |
| `CompileError` intrinsic | `raise CompileError("msg")` aborts compilation with a `CompileError:` diagnostic; never generates `RaiseExn` IR; used in all HAL modules for unsupported arch/chip guards; cannot be caught by `try/except` |

### HAL (ATmega328P)

| Module | Coverage |
|---|---|
| `pymcu.hal.gpio` | `Pin` — `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART` — `write/read/read_line/write_str/println/print_byte/available` + RX interrupt |
| `pymcu.hal.adc` | `AnalogPin` — poll + interrupt; channels `"PC0"`–`"PC5"`, `"TEMP"` (internal sensor), `"VBG"`, `"ADC8"` |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — Timer0/1/2 unified; CTC mode |
| `pymcu.hal.pwm` | `PWM` — `start/stop/set_duty/set_freq`; multi-channel (two channels of the same timer coexist — the COM bits are OR-ed). `set_freq` picks the **nearest** reachable prescaler bucket |
| `pymcu.hal.spi` | `SPI` + `SoftSPI` |
| `pymcu.hal.i2c` | `I2C` + `SoftI2C`; `write_to` / `read_from` / `write_bytes` / `writeto_mem` / `readfrom_mem` |
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
| `*args` / `**kwargs` over a run-time call | The forms are compile-time sequences and mappings: the callee is specialised per call site, so the extra arguments are known there and splice into the callee's named parameters, `super().__init__` included. A `**` built from a run-time mapping is refused |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
