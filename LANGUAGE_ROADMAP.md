# PyMCU Language Features Roadmap

---

## Alpha (v0.1) — Implemented

Everything in this section is shipped and tested in the current alpha build.

### Statements

| Feature | Notes |
|---------|-------|
| `if` / `elif` / `else` | Compile-time DCE for `__CHIP__` branches |
| `while` + `break` / `continue` | Full support |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)` |
| `for x in array` / `for x in [1,2,3]` | Fixed-size array or constant list literal |
| `for i, x in enumerate(iterable)` | Compile-time index counter |
| `match` / `case` | Literal, wildcard `_`, OR (`|`) patterns; DCE on `__CHIP__` |
| `def` (functions) | Typed params, defaults, keyword args, overloading by type |
| `def main():` | Explicit entry point (optional — top-level scripts compile without it) |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level executable statements |
| `class` | Zero-cost flattening, `@inline` methods, constructors |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| Single-level class inheritance | ZCA base + derived; `super()` calls |
| `with obj:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `return` | With/without value; tuple multi-return from `@inline` functions, optionally annotated `-> (T1, T2)` or `-> tuple[T1, T2]` (the element types set the result widths) |
| `pass` / `raise` | `raise ExnType` signals an error via the T flag and returns; caught at the call site by an enclosing `try` (SET/BRTS, no `longjmp`); `ValueError`/`TypeError`/`IndexError`/`KeyError`/`NotImplementedError` are builtins — no import required |
| `raise CompileError(msg)` | Compile-time intrinsic — aborts compilation with `CompileError:` diagnostic; never generates `RaiseExn` IR; cannot be caught by `try/except`; used in all HAL modules for unsupported arch/chip guards |
| `import` / `from ... import` / `import X as Y` | Relative imports, multi-level |
| `global` | Cross-function variable access |

### Expressions

| Feature | Notes |
|---------|-------|
| Integer literals | Decimal, hex, binary, octal, `_` separators |
| `True` / `False` | Folded to `Constant{1/0}` |
| `None` | Real null literal (not the integer `-1`). `x is None` / `== None` / `!= None` compile to a null check; assigning `None` to a scalar (`int` / `uintN`) is a `TypeError` — `None` is for reference / optional-typed values |
| String literals | Single- and double-quoted; mapped to stable compile-time IDs |
| Arithmetic `+ - * / % //` | Full constant folding. `%` and `//` follow Python's floored sign (`-7 % 3 == 2`, `-7 // 2 == -4`). **`/` is Python 3 true division and always yields a `float`** (`4 / 2 == 2.0`), even on two integers — the compiler warns once per site because it links the soft-float routines into the firmware; use `//` when you want integer division. |
| Comparison `== != < <= > >=` | Chained comparisons (`lo < x < hi`) evaluate as `(lo < x) and (x < hi)`, Python semantics |
| Bitwise `& | ^ ~ << >>` | |
| Logical `and` / `or` / `not` | Short-circuit; `and`/`or` evaluate to the **operand**, not a bool (`a or default`, `x and x.field` work as in Python) |
| Ternary `x if cond else y` | Compiles to JumpIfZero chain |
| Unary `- ~ not` | Constant folding |
| Augmented assignment `+= -= *= //= &= |= ^= <<= >>=` | Variable, subscript, and member targets |
| Type cast `uint8(val)`, `uint16(val)` | Constant-fold; truncate/zero-extend at runtime |
| `abs(x)`, `min(a, b)`, `max(a, b)` | Intrinsic built-ins |
| `len(arr)` / `len([...])` | Compile-time constant fold |
| `ord('A')`, `chr(n)` | Compile-time constant only |
| Multiple assignment `a = b = 0` | Left-to-right Copy chain |
| Walrus `:=` | Assign-and-return; essential for UART / sensor polling loops |
| Bit indexing `port[n]` | `n` must be compile-time constant |
| Array indexing `arr[i]` | Constant-index: zero overhead; variable-index: SRAM. Negative constant index `arr[-1]` is the last element (Python); out-of-range constant index is a compile error |
| List comprehension `[x*2 for x in range(n)]` | Compile-time unroll; constant iterable only |
| Tuple literal `(a, b)` / unpacking `a, b = f()` / `a, b = b, a` | Stack-allocated; multi-return (`f` must be `@inline` — a real subroutine has one return register); bare-tuple RHS supported, so swap evaluates the RHS before assigning |
| Member access `obj.x` / method calls `obj.m()` | Inline expansion; zero SRAM |
| Keyword arguments `f(key=val)` | Matched by name in inline binding |
| `print(val)` | Maps to UART; requires `default_uart` in `pyproject.toml` |
| `input(prompt?, maxlen?)` | `line: bytearray = input("prompt")` — reads newline-terminated line from UART; auto-injects UART preamble |
| F-strings `f"text={var}"` | Streamed to a sink (`print(f"...")`, `uart.write_str/println(f"...")`, `lcd.print_str(f"...")`) with runtime interpolations and format specs — no heap. As a *value* (`s = f"..."`) since v0.14: built into a compiler-managed fixed buffer |

### MCU-Specific Extensions

| Feature | Notes |
|---------|-------|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Annotation for variables; unannotated `def` params/returns of outlined functions are inferred from call sites (v0.14) |
| `int` (built-in) | Maps to `int16`; no import required |
| `ptr[T]` | Memory-mapped I/O pointer |
| `const[T]` | Compile-time constant enforcement |
| `asm("instr")` | Inline assembly emission |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic timing |
| `@inline` | Zero-cost abstraction |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion only |
| `@staticmethod` | Silently ignored (all class methods are effectively static) |
| `__CHIP__` | Conditional compilation by chip name / architecture |
| `__FREQ__` | Compile-time clock frequency in Hz (e.g. `16000000` at 16 MHz); use for timing calculations |
| `.value` dereference | 8/16-bit memory read/write via `ptr` |

### HAL

| Module | Class / Function | Targets | Notes |
|--------|-----------------|---------|-------|
| `pymcu.hal.gpio` | `Pin` | All | `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART` | All | `write/read/write_str/println/print_byte` |
| `pymcu.hal.adc` | `AnalogPin` | AVR, PIC | `start()` + poll; `read()` (10-bit), `read_u16()` (0-65535); ATtiny85: PB2/PB3/PB4 |
| `pymcu.hal.timer` | `Timer(n, prescaler)` | All | Timer0/1/2 unified; `start/stop/clear/overflow`; ATtiny85: Timer0+Timer1 (15 prescaler steps) |
| `pymcu.hal.pwm` | `PWM` | AVR, PIC | Hardware PWM; `start/stop/set_duty` |
| `pymcu.hal.spi` | `SPI` | AVR | HW SPI master; `with spi:` context |
| `pymcu.hal.i2c` | `I2C` | AVR | TWI master; `with i2c:` context; `ping/write/read_*` |
| `pymcu.hal.eeprom` | `EEPROM` | ATmega328P, ATmega2560, ATmega32U4, ATtiny85/45/25 | `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` | ATmega328P, ATmega2560, ATmega32U4, ATtiny85/45/25 | `enable/disable/feed`; timeout is compile-time const |
| `pymcu.hal.power` | `sleep_*` | ATmega328P | `sleep_idle / sleep_adc_noise / sleep_power_down / sleep_power_save / sleep_standby / sleep_extended_standby` |
| `pymcu.drivers.dht11` | `DHT11` | All | Portable driver; reads humidity + temperature |
| `pymcu.time` | `delay_ms`, `delay_us` | All | Blocking delays |
| `pymcu.boards.arduino_uno` | `D0`-`D13`, `A0`-`A5` | ATmega328P | Pin name constants |
| `pymcu.boards.arduino_mega` | `D0`-`D53`, `A0`-`A15` | ATmega2560 | Pin name constants |
| `pymcu.boards.arduino_leonardo` | `D0`-`D13`, `A0`-`A5` | ATmega32U4 | Pin name constants (the CLI board key for the 32U4 is `arduino_micro`) |

> **RP2040 (alpha):** the RP2040 backend currently implements only `pymcu.hal.gpio`
> (`Pin`), `pymcu.hal.uart` (`UART0`) and `pymcu.time` (`delay_ms` / `delay_us`).
> Other HAL modules in the table above are not wired up on RP2040 yet. Boards:
> `raspberry_pi_pico` / `pico` → `rp2040`.

### Compat Packages

| Package | Activation | Coverage |
|---------|-----------|----------|
| `pymcu-circuitpython` | `stdlib = ["circuitpython"]` | `board`, `digitalio`, `busio`, `analogio`, `time` |
| `pymcu-micropython` | `stdlib = ["micropython"]` | `machine` (Pin/UART/ADC/PWM/SPI/I2C), `utime`, `micropython` |

---

## Beta (v0.2) — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `in` / `not in` operator | Compile-time fold on constant list; runtime OR/AND chain |
| `is` / `is not` | Maps to `==` / `!=` (identity = equality on bare-metal) |
| `divmod(a, b)` built-in | Returns `(quotient, remainder)`; compile-time fold or `__div8`/`__mod8` |
| `bitcast(T, v)` built-in | Reinterpret raw bytes as type `T`; float<->uint32 via register swap; compile-time fold for constant operands |
| `hex(n)` / `bin(n)` (compile-time) | Fold to `"0xff"` / `"0b101"` string constant |
| `sum(iterable)` | Compile-time fold or unrolled additions over fixed-size array |
| `any(iterable)` / `all(iterable)` | Compile-time fold or OR/AND chain |

### HAL

| Feature | Notes |
|---------|-------|
| `UART.available()` | Returns 1 if RXC bit set (byte waiting in receive buffer) |

---

## v0.3 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `zip(a, b)` compile-time | `for x, y in zip(list1, list2):` — unrolled over paired constant lists |
| `reversed(iterable)` | `for x in reversed([1,2,3]):` — compile-time reverse unroll |
| `str(n)` compile-time | `str(42)` → `"42"` string constant; compile-time `n` only |
| `pow(x, n)` / `x ** n` | Compile-time constant fold; `BinaryOp::Pow` |

### HAL

| Feature | Notes |
|---------|-------|
| `UART.read_nb()` | Non-blocking read; returns byte if RXC set, else 0 |
| `UART.read_byte_isr()` | Direct UDR0 read for use inside `@interrupt` handlers |
| `I2C.write_to(addr, data)` | START + SLA+W + byte + STOP; returns 1 on ACK / 0 on NACK |
| `I2C.read_from(addr)` | START + SLA+R + read byte + NACK + STOP; returns byte |

---

## v0.4 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for x in b"..."`, array init, `len()` |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold for byte literals; runtime `(hi<<8)|lo` for variables |
| `enumerate` on runtime arrays | `for i, x in enumerate(arr):` unrolled with `ArrayLoad` per element |
| `UART.read_blocking()` | Polls RXC until byte arrives, returns it |

### HAL

| Feature | Notes |
|---------|-------|
| SPI CS pin control | `SPI(cs="PB2")` auto-asserts/deasserts CS; `select()`/`deselect()` methods |

---

## v0.5 — Implemented

### HAL

| Feature | Notes |
|---------|-------|
| Timer CTC mode | `Timer.set_compare(val)` sets OCR + WGM CTC bits; `@interrupt` handles COMPA vector |
| ADC interrupt-driven | `AnalogPin.start_conversion()` sets ADIE+ADSC; `read_result()` reads ADCL/ADCH |
| PWM multi-channel | Timer0/1/2 OC_A+OC_B channels; `PWM("PB1")` auto-selects Timer1 OC1A |

### Drivers

| Feature | Notes |
|---------|-------|
| `neopixel` (WS2812) | `NeoPixel(pin, n).set_pixel(r,g,b)` + `show()`; GRB wire order; AVR asm bit-bang |

---

## v0.6 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| Nested list comprehension | `[f(x,y) for x in outer for y in inner]` — full outer x inner product unroll |
| `if` filter in list comprehension | `[x for x in [1,2,3,4] if x > 2]` — static condition only |
| `for v in [Cls(p) for p in (...)]` | CT unroll of ZCA instance array from list comp; `enumerate` also supported |
| `bytearray` mutable buffer | `bytearray(8)` / `bytearray(b"...")` → SRAM `uint8[N]`; all array ops work |

---

## v0.7 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `Pin.irq(trigger, handler)` | Configures INT0/INT1/PCINT hardware; `IRQ_FALLING`/`IRQ_RISING`/`IRQ_CHANGE` |

### HAL

| Feature | Notes |
|---------|-------|
| USART RX interrupt + ring buffer | `uart.enable_rx_interrupt()` + `uart.rx_isr()` + `available()` / `read_nb()` |
| `SoftSPI` bit-bang | `SoftSPI(sck, mosi, miso, cs)` with `transfer()`, `write()`, `with softspi:` |

### Drivers

| Driver | Notes |
|--------|-------|
| `HD44780` LCD (`pymcu.drivers.lcd`) | `LCD(rs, en, d4-d7)` — 4-bit parallel; `init/clear/home/print_str/set_cursor/write_char/print_fmt` |
| `SSD1306` OLED | 128x64 OLED over I2C; `init/clear/draw_pixel/draw_line/print_str` |
| `MAX7219` 8x8 LED matrix | SPI matrix driver; `init/clear/set_row/set_brightness` |
| `BMP280` barometer | I2C barometric pressure + temperature sensor; `read_pressure/read_temp` |

---

## v0.8 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| Raw strings `r"\n"` | `r"..."` / `r'...'` suppress all escape processing (PEP 3) |
| `match/case` guard `if cond` | `case x if x > 100:` — guard evaluated after pattern match (PEP 634) |
| `match/case` sequence patterns `[a, b, c]` | Destructures fixed-size arrays/tuples by position (PEP 634) |
| `match/case` capture `case x as name` | Bare identifier capture; `or-pattern as name` binding (PEP 634) |
| Multi-item `with a as x, b as y:` | Desugared to nested `with` at parse time (PEP 343) |
| Extended unpacking `first, *rest = tup` | Starred target captures middle slice; compile-time tuples only (PEP 3132) |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function; no closure capture (PEP 3) |
| `str.join` | `sep.join([...])` folds compile-time strings; `''.join([chr(b) for b in buf])` lowers to a runtime string (the MicroPython/CircuitPython bytes-to-string idiom) |
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices produce a fixed-size array. Equal-length slice ASSIGNMENT (`arr[a:b] = src`, v0.14) with list/bytes/array/slice sources incl. overlapping same-array copies, and through `__setitem__` objects (`nvm[0:4] = b'...'`). ITERATION over a slice accepts runtime bounds (`for b in buf[0:n]`, rewritten to a range loop). `print(bytearray)` / `print(obj[a:b])` stream the CPython `bytearray(b'...')` repr |
| `nonlocal` in nested `@inline` | Mutates enclosing scope variable via SRAM alias (PEP 3104) |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__floordiv__`, `__mod__`, `__and__`, `__or__`, `__xor__`, `__lshift__`, `__rshift__`, `__eq__`, `__ne__`, `__lt__`, `__le__`, `__gt__`, `__ge__`, `__neg__`, `__invert__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__` |

### C/C++ Interop

All C interop features are implemented. The build pipeline uses `avr-as` + `avr-ld`
instead of `avra` whenever `[tool.pymcu.ffi]` is present in `pyproject.toml`.

| Feature | Notes |
|---------|-------|
| `@extern("symbol")` decorator | Declares and calls external C/C++ symbols with AVR ABI |
| `[tool.pymcu.ffi]` build config | `sources`, `include_dirs`, `cflags` in `pyproject.toml` |
| `pymcu.ffi` stdlib module | Re-exports `extern`; no runtime code |
| C compilation (`avr-gcc`) | Compiles `.c` sources listed in `ffi.sources` |
| C++ compilation (`avr-g++`) | Compiles `.cpp` / `.cc` / `.cxx` sources; `-fno-exceptions -fno-rtti -std=c++17` |

```python
from pymcu.ffi import extern

@extern("arduino_millis")
def millis() -> uint16: ...

t: uint16 = millis()
```

```toml
# pyproject.toml — supports both C and C++ sources
[tool.pymcu.ffi]
sources      = ["src/c/sensor.c", "src/cpp/ArduinoLib.cpp"]
include_dirs = ["src/include"]
cflags       = ["-O2"]
```

Build pipeline:
```
.py → pymcuc → firmware.asm
firmware.asm   → avr-as  → firmware.o
sensor.c       → avr-gcc → sensor.o
ArduinoLib.cpp → avr-g++ → ArduinoLib.o
firmware.o + sensor.o + ArduinoLib.o → avr-ld → firmware.elf → firmware.hex
```

---

## v0.9 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `const[uint8[N]]` PROGMEM arrays | Global flash-resident byte lookup tables; accessed via `LPM Z` instruction |
| Inline ASM register constraints `%N` | `asm("LDI %0, 42", var)` — `%0`–`%3` substituted with scratch registers R16–R19 |

### Compiler

| Feature | Notes |
|---------|-------|
| Signed 16-bit multiplication (`int16 * int16`) | Uses `MULSU` for cross-product terms; matches avr-gcc output |

### HAL

| Feature | Notes |
|---------|-------|
| `millis()` / `micros()` elapsed-time counter | Timer0 overflow ISR at prescaler 64; atomic 32-bit read; 1024 µs / overflow |
| `SoftI2C` bit-bang I2C | GPIO open-drain emulation; `start`, `stop`, `write`, `read`, `write_to`, `write_bytes`, `read_from`, `ping` |
| `I2C.write_bytes(addr, buf, n)` multi-byte | Sends START + SLA+W + N data bytes + STOP in one call |

---

## v0.10 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `__name__` compile-time constant | `"__main__"` for entry file, dotted module name for libraries — matches CPython semantics |
| `if __name__ == "__main__":` guard | Compile-time guard for entry-point code; body promoted to top-level in main, eliminated in libs |
| `const[str]` runtime subscript | Runtime-indexed access on compile-time string constants via `ArrayLoadFlash` (LPM Z on AVR) |

### Compiler

| Feature | Notes |
|---------|-------|
| Remove `UARTSendString` IR instruction | String output decomposed to `FlashData` + `ArrayLoadFlash` inline loop — no UART knowledge in IR layer |
| `print()` routes through stdlib | `print()` calls `uart_write_str` (inline) for strings, `uart_write_decimal_u8` for numbers — arch code lives in stdlib |

### HAL

| Feature | Notes |
|---------|-------|
| `uart_write_str` pure PyMCU loop | Replaces compiler-intrinsic `uart_send_string` with idiomatic `while b != 0: uart_write(b)` inline loop |
| `pymcu.hal.console` module | Arch-dispatched `print_str` / `print_u8` wrappers for portable console output |

---

## v0.13 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| Triple-quoted strings (`"""..."""` / `'''...'''`) | ✅ Implemented — lexer captures content including embedded newlines; leading newline after opening quote is stripped. Enables readable multi-line `asm()` blocks. |

---

## v0.14 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `async def` / `await` (v2) | Lowered at compile time to a zero-cost state-machine class with `poll()` — no heap, no interpreter. Requires `import asyncio`; awaitable is `asyncio.sleep(n)` / `asyncio.sleep_ms(n)`, now anywhere in the body: inside `if/elif/else`, `while <cond>` and `for i in range(...)` at any nesting, with `break`/`continue`, and `return expr` exposing the result via `._value`. Locals become fields only when they survive a suspension. Executors: `asyncio.run(coro)` / `asyncio.gather(a, b)`. Time base: hardware microsecond TIMER on RP2040/RP2350, Timer0 millis/micros on ATmega (4 us resolution; `millis_init()` auto-injected by `pymcu build`). Not yet: awaiting another coroutine/future, `await` as an expression, a time base on PIC/RISC-V (awaits there never complete) |
| f-string as a **value** (`s = f"t={t} C"`) | Builds the string into a compiler-managed fixed `bytearray` (no heap): the size is statically bounded per part, formatting lowers to `pymcu.strfmt` calls (auto-injected). Consumers: `print(s)`, `uart.write_str/println(s)`, `len(s)` (formatted length), `s[i]`, re-assignment in a loop (buffer reuse), passing as a `bytearray` param. Not yet: f-string inline in other expression positions, float interpolations in the value form, `s == "lit"` |
| Closed dict/set literals | `d = {0: 10, "mid": 2}` / `OK = {1, 3, 5}` bind compile-time lookup tables (no storage): `d[const]` folds, `d[runtime]` lowers to a compare chain raising `KeyError` (catchable), `x in d` / `x in {...}` membership chains, `len(d)` folds. String keys fold on constant lookups. Read-only — for mutation use `FixedDict` |
| `pymcu.collections.FixedDict` | Mutable fixed-capacity integer dict (open addressing over per-instance fixed arrays — no heap, no GC): `d[k]`/`d[k]=v` (`KeyError` on missing, `ValueError` when full), `k in d`, `len(d)`, `get(k, default)`, `pop(k)` (tombstones), `clear()`. Capacity is a compile-time constant |
| Type inference for unannotated `def` params/returns | Outlined functions infer missing param/return annotations from call-site evidence + defaults + return expressions (safe integer-widening join). Fixes the silent uint8-default truncation (`scale(300, 2)` used to print 88). `@inline` functions keep their compile-time polymorphism; overloaded names are untouched |
| Generators (`yield`) | A top-level function containing `yield` lowers to the same zero-cost state-machine class as `async def` (poll() returns 2 = yielded / 1 = working / 0 = done, value via `._value`); `for x in gen(...)` desugars to a poll loop with Python break/continue semantics. Not inside `@inline`/methods; `yield` as an expression not supported |
| Module-level statements with explicit `def main()` | Module-scope executable statements (peripheral constructions, calls) run at startup before `main()`'s body, mirroring Python — previously rejected |
| Nested class-typed ZCA fields | Method calls / field reads on a class-typed field (`machine.Pin` wrapping the HAL `Pin`) dispatch correctly, including through facade re-exports and single-level inheritance |

### Tooling / Targets

| Feature | Notes |
|---------|-------|
| `pymcu lint` | MicroPython/CircuitPython porting assistant: flags dict/set, runtime f-strings, reflection, unbounded `append`, `*args`/`**kwargs`, untyped params, `yield`, … with severity + suggestion per finding |
| RP2350 (Pico 2) target | Cortex-M33: `crt0_m33` + picobin image block + linker script; full peripheral HAL (GPIO/UART/I2C/SPI/PWM/ADC/DMA) on RP2040 and RP2350 |
| CYW43439 WiFi HAL (Pico W / Pico 2 W) | gSPI bring-up, WLAN join, TCP and MQTT publish via `pymcu.hal.wifi`; validated against the RP2350.Wireless chip model |
| Flash-resident const data on ARM | Interned strings and `const[uint8[N]]` tables emit as `.rodata`; `const[str]` runtime subscript works on RP2040/RP2350 |

---

## v0.12 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `list[T]` heap-allocated list | `x: list[uint8] = list()` / `list(N)` / `[a, b, c]`; GC-managed; `append()`, `len()`, `x[i]`, `for v in x:`. Overflow triggers automatic realloc (capacity × 2). |

---

## v0.11 — Implemented

### CLI / Driver

| Feature | Notes |
|---------|-------|
| Plugin-based toolchain system | ✅ Implemented | `pymcu.toolchains` entry-point group; `pip install pymcu[avr]` / `pymcu[pic]` |
| `pymcu-toolchain-sdk` | ✅ Implemented | Standalone SDK package; base classes + `ToolchainPlugin` ABC |
| `pymcu-toolchain-avr` | ✅ Implemented | AVR plugin (GNU AVR binutils); independent of core `pymcu` |
| `pymcu-toolchain-pic` | ✅ Implemented | PIC plugin (GNU PIC Utilities); independent of core `pymcu` |
| `pymcu-rp2040` (backend + toolchain) | 🧪 Alpha | RP2040 / Cortex-M0+ backend that emits **LLVM IR** (not asm); LLVM toolchain (`opt`/`llc`/`llvm-mc`/`ld.lld`/`llvm-objcopy`) → `firmware.bin`. `pip install pymcu[rp2040]`. MVP: GPIO + UART0, single core |
| Programmer plugin system | ✅ Implemented | `pymcu.programmers` entry-point group; `pymcu-programmer-avrdude`, etc. |

### Language

| Feature | Notes |
|---------|-------|
| ~~Soft float~~ / `fixed16` | ✅ IEEE 754 single precision implemented on all ARM targets — AVR (`__fp_*`), RP2040 (bootrom fast-float via `__aeabi_f*`, v0.14) and RP2350 (Cortex-M33 FPU, FPv5-SP in softfp mode). `fixed16` deferred. |
| `const uint8[N]` (PROGMEM arrays) | ✅ Implemented in v0.9 |
| `uint16 >> n → uint8` widening shift | ✅ Compiler correctly loads wider source type for shift/bitwise ops narrowing to uint8 |

### HAL

| Feature | Notes |
|---------|-------|
| `SoftI2C` bit-bang | ✅ Implemented in v0.9 |
| `I2C.write_to(addr, buf, n)` multi-byte | ✅ Implemented in v0.9 as `I2C.write_bytes` |
| `UART.read_line(buf, max_len)` | ✅ Implemented — reads until `\n` or max_len into fixed-size `uint8[N]` buffer |
| Timer `millis()` / `micros()` | ✅ Implemented in v0.9 |
| Internal temperature sensor | ✅ Implemented — ATmega328P ADC channel 8; `AnalogPin("TEMP")` from `pymcu.hal.adc` |
| `DS18B20` 1-Wire driver | ✅ Implemented — `pymcu.drivers.ds18b20`; `DS18B20(pin)` / `read_temp_raw()` / `read_temp_celsius()` |

### Compat

| Feature | Notes |
|---------|-------|
| `machine.Timer(id, period, callback)` | ✅ Implemented — CTC mode on Timer1; period in ms; callback as ISR |
| `busio.SPI` / `busio.I2C` for CP flavor | ✅ Implemented | Wraps existing HAL under CircuitPython API names |
| `neopixel` driver (CP flavor) | ✅ Implemented | WS2812 bit-bang via `neopixel.NeoPixel` API |

---

## v1.0 — Longer Horizon

| Feature | Effort | Why |
|---------|--------|-----|
| **MicroPython/CircuitPython API Alignment** | High Priority | Standardize the user-facing API for portability and ease of use. This is the main focus. |
| `fixed16` (Q8.8 fixed-point) | ~1 week | Float-like sensor math without FPU |
| PIC18 codegen | ~2 weeks | Extend backend for PIC18Fxxxx family |
| RISC-V 32-bit codegen | ~2 weeks | CH32V003, ESP32-C3 |
| RP2040 PIO backend | ~1 week | Programmable I/O state machine output |
| Over-the-air (OTA) support | ~1 week | Bootloader + pymcu flash over UART |
| LLVM IR backend | ~4 weeks | Unlocks all LLVM targets (ARM Cortex-M, etc.) |
| ARM Cortex-M0/M4 backend | ~3 weeks | STM32, nRF52; via LLVM or direct codegen |

---

## Not Planned

These Python features are architecturally incompatible with bare-metal, no-heap firmware:

| Feature | Reason |
|---------|--------|
| General mutable `dict` / `set` | Hash tables require heap. **Closed literals ARE supported** (v0.14): `d = {...}` binds a compile-time lookup table — `d[k]` folds or compare-chains (missing key raises `KeyError`), `x in {...}` tests membership, `len(d)` folds. Mutation is not supported |
| Garbage collection beyond `list[T]` | Full GC incompatible with deterministic ISR timing |
| `complex` / `Decimal` | Not available |
| `f"..."` inline in arbitrary expressions | Streaming (`print(f"...")`) and assignment (`s = f"..."`, fixed buffer) are supported; an f-string used inline in any other expression position has no lowering — assign it to a name first |
| Closures capturing mutable vars | Captured variables require heap; `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Metaclasses | No runtime type system |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |