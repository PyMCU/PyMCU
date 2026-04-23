# Roadmap

---

## Alpha (v0.1) — Implemented

Everything below is shipped and covered by integration tests.

### Language

| Feature | Notes |
|---------|-------|
| `if` / `elif` / `else` | Compile-time DCE on `__CHIP__` branches |
| `while` + `break` / `continue` | |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)` |
| `for x in array` / `for x in [1, 2, 3]` | Fixed-size array or constant list literal |
| `for i, x in enumerate(iterable)` | Compile-time index counter |
| `match` / `case` | Literal, wildcard `_`, OR (`|`), dotted-name patterns; DCE on `__CHIP__` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return |
| `def main():` | Explicit entry point (optional — top-level scripts compile without it) |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level executable statements |
| `class` | ZCA `@inline` flattening, constructors, `@property` / `@name.setter` |
| Single-level class inheritance | ZCA base + derived; `super()` calls |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| `with obj:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `return` / `pass` / `raise` | `raise` is compile-time only |
| `import` / `from ... import` / `import X as Y` | Relative imports, multi-level |
| `global` | Cross-function variable access |

### Expressions

| Feature | Notes |
|---------|-------|
| Integer literals | Dec, hex, bin, oct, `_` separators |
| `True` / `False` / `None` | `True`/`False` fold to `Constant{1/0}`; `None` on object-reference types folds via `is None` / `is not None`; `None` on numeric types is a TypeError |
| String literals | Single- and double-quoted |
| Arithmetic `+ - * / % //` | Full constant folding |
| Comparison `== != < <= > >=` | |
| Bitwise `& | ^ ~ << >>` | |
| Logical `and` / `or` / `not` | Full short-circuit evaluation |
| Ternary `x if cond else y` | |
| Augmented assignment `+= -= *= //= &= |= ^= <<= >>=` | Variable, subscript, and member targets |
| Type cast `uint8(val)`, `uint16(val)` | Constant-fold; truncate/zero-extend at runtime |
| `abs(x)`, `min(a, b)`, `max(a, b)`, `len(x)` | Intrinsic built-ins |
| `ord('A')`, `chr(n)` | Compile-time constant only |
| Multiple assignment `a = b = 0` | |
| Walrus `:=` | Assign-and-return; UART / sensor polling loops |
| Bit indexing `port[n]` | Compile-time constant index |
| Array indexing `arr[i]` | Const-index: zero overhead; variable-index: SRAM |
| Fixed-size arrays `arr: uint8[N]` | Dual-mode: registers (const) / SRAM (var) |
| List comprehension `[x*2 for x in range(n)]` | Compile-time unroll; constant iterable only |
| Tuple literal `(a, b)` / unpacking `a, b = f()` | Stack-allocated multi-return |
| Member access `obj.x` / `obj.m()` | Inline expansion; zero SRAM |
| `print(val)` | Maps to UART; requires `default_uart` in `pyproject.toml` |
| F-strings `f"text={var}"` | Compile-time constant only; `{expr}` must resolve to a string or integer constant |

### MCU Extensions

| Feature | Notes |
|---------|-------|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Required annotation for all variables |
| `int` (built-in) | Maps to `int16`; no import required |
| `ptr[T]` / `ptr(addr)` | Memory-mapped I/O |
| `const[T]` | Compile-time constant enforcement |
| `asm("instr")` | Inline assembly |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic busy-wait |
| `@inline` | Zero-cost expansion |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion |
| `__CHIP__` | Conditional compilation by chip name / architecture |
| `__FREQ__` | Compile-time clock frequency in Hz (e.g. `16000000` at 16 MHz); use for timing calculations |

### HAL

| Module | Coverage |
|--------|----------|
| `pymcu.hal.gpio` | `Pin` — `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART` — `write/read/write_str/println/print_byte` |
| `pymcu.hal.adc` | `AnalogPin` — `start()` + poll; `read()` (10-bit), `read_u16()` (0-65535) |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — Timer0/1/2 unified |
| `pymcu.hal.pwm` | `PWM` — `start/stop/set_duty` |
| `pymcu.hal.spi` | `SPI` — `with spi:` context; `transfer/write` |
| `pymcu.hal.i2c` | `I2C` — `with i2c:` context; `ping/write/read_*` |
| `pymcu.hal.eeprom` | `EEPROM` — `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` — `enable/disable/feed` |
| `pymcu.hal.power` | `sleep_idle/adc_noise/power_down/power_save/standby` |
| `pymcu.drivers.dht11` | `DHT11` — portable temperature/humidity driver |
| `pymcu.time` | `delay_ms`, `delay_us` |
| `pymcu.boards.arduino_uno` | `D0`-`D13`, `A0`-`A5`, `LED_BUILTIN` |

### Compat Packages

| Package | Activation | Modules |
|---------|-----------|---------|
| `pymcu-circuitpython` | `stdlib = ["circuitpython"]` | `board`, `digitalio`, `busio`, `analogio`, `time` |
| `pymcu-micropython` | `stdlib = ["micropython"]` | `machine` (Pin/UART/ADC/PWM/SPI/I2C), `utime`, `micropython` |

---

## Beta (v0.2) — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `in` / `not in` operator | Compile-time fold on constant list; runtime OR/AND equality chain |
| `is` / `is not` | Maps to `==` / `!=` (identity = equality on bare-metal) |
| `divmod(a, b)` | Returns `(quotient, remainder)`; compile-time fold or `__div8`/`__mod8` |
| `hex(n)` / `bin(n)` | Compile-time: `hex(255)` → `"0xff"`, `bin(5)` → `"0b101"` |
| `sum(iterable)` | Compile-time fold or unrolled additions |
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
| `pow(x, n)` / `x ** n` | Compile-time constant fold |

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
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for`, array init, `len()` |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold for byte literals; runtime `(hi<<8)|lo` for variables |
| `enumerate` on runtime arrays | `for i, x in enumerate(arr):` unrolled with `ArrayLoad` per element |
| `UART.read_blocking()` | Polls RXC until byte arrives, returns it |

### HAL

| Feature | Notes |
|---------|-------|
| SPI CS pin control | `SPI(cs="PB2")` auto-asserts/deasserts CS via `select()`/`deselect()` |

---

## v0.5 — Implemented

### HAL

| Feature | Notes |
|---------|-------|
| Timer CTC mode | `Timer.set_compare(val)` sets OCR + WGM CTC bits; use `@interrupt` for COMPA vector |
| ADC interrupt-driven | `AnalogPin.start_conversion()` + `read_result()`; handle ADC complete at `0x002A` |
| PWM multi-channel | Timer0/1/2 OC_A+OC_B channels; `PWM("PB1")` auto-selects Timer1 OC1A |

### Drivers

| Feature | Notes |
|---------|-------|
| `neopixel` (WS2812) | `NeoPixel(pin, n).set_pixel(r,g,b)` + `show()`; GRB order; AVR asm bit-bang |

---

## v0.6 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| Nested list comprehension | `[f(x, y) for x in row for y in col]` — full outer x inner product unroll |
| `if` filter in list comprehension | `[x for x in [1,2,3,4] if x > 2]` — static condition only |
| `bytearray` mutable buffer | `bytearray(8)` / `bytearray(b"...")` → SRAM `uint8[N]`; all array ops work |

---

## v0.7 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `Pin.irq(trigger, handler)` | Configures INT0/INT1/PCINT hardware; `IRQ_FALLING`/`IRQ_RISING`/`IRQ_CHANGE`; no `@interrupt` decorator needed on handler |

### HAL

| Feature | Notes |
|---------|-------|
| USART RX interrupt + ring buffer | `uart.enable_rx_interrupt()` + `uart.rx_isr()` + `available()` / `read_nb()` |
| `SoftSPI` bit-bang | `SoftSPI(sck, mosi, miso, cs)` with `transfer()`, `write()`, `with softspi:` |

### Drivers

| Driver | Notes |
|--------|-------|
| `HD44780` LCD | `LCD(rs, en, d4-d7)` — 4-bit parallel; `init/clear/home/print_str/set_cursor/write_char` |
| `SSD1306` OLED | 128x64 OLED over I2C; `init/clear/draw_pixel/draw_line/print_str` |
| `MAX7219` 8-digit display | SPI 7-segment driver; `set_digit/set_raw/clear/set_brightness` |
| `BMP280` barometer | I2C barometric pressure + temperature sensor; `read_pressure/read_temp` |

---

## v0.8 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| Raw strings `r"\n"` | Suppress escape processing; `r"\n"` = backslash + n, not newline |
| `match/case` guard `if cond` | `case x if x > 100:` — guard evaluated after pattern match (PEP 634) |
| `match/case` sequence patterns `[a, b, c]` | Destructures fixed-size arrays/tuples by position (PEP 634) |
| `match/case` capture `case x as name` | Bare identifier capture; `or-pattern as name` binding (PEP 634) |
| Multi-item `with a as x, b as y:` | Desugared to nested `with` at parse time (PEP 343) |
| Extended unpacking `first, *rest = tup` | Starred target captures middle slice; compile-time tuples only (PEP 3132) |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function; no closure capture |
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices; produces fixed-size array (PEP 197) |
| `nonlocal` in nested `@inline` | Mutates enclosing scope variable via SRAM alias (PEP 3104) |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, and all comparison/bitwise dunders |

### C/C++ Interop

| Feature | Notes |
|---------|-------|
| `@extern("symbol")` decorator | Declares and calls external C/C++ symbols with AVR ABI |
| `[tool.pymcu.ffi]` build config | `sources`, `include_dirs`, `cflags` in `pyproject.toml` |
| C compilation (`avr-gcc`) | Compiles `.c` sources listed in `ffi.sources` |
| C++ compilation (`avr-g++`) | Compiles `.cpp` / `.cc` / `.cxx`; `-fno-exceptions -fno-rtti`; enables Arduino library interop |

```python
from pymcu.ffi import extern
from pymcu.types import uint16

@extern("arduino_millis")
def millis() -> uint16: ...

t: uint16 = millis()
```

```toml
[tool.pymcu.ffi]
sources      = ["src/sensor.c", "src/ArduinoLib.cpp"]
include_dirs = ["src/include"]
cflags       = ["-O2"]
```

---

## v0.9 — Implemented

### Language

| Feature | Notes |
|---------|-------|
| `const[uint8[N]]` PROGMEM arrays | Flash-resident byte lookup tables via `LPM Z`; no SRAM allocated |
| Inline ASM register constraints `%N` | `asm("LDI %0, 42", var)` substitutes `%0`–`%3` with scratch registers R16–R19 |

### Compiler

| Feature | Notes |
|---------|-------|
| Signed 16-bit multiplication | Uses `MULSU` for cross-product terms; correct negative operands |

### HAL

| Feature | Notes |
|---------|-------|
| `millis()` / `micros()` | Timer0 overflow at prescaler 64; atomic 32-bit read under CLI/SEI |
| `SoftI2C` bit-bang driver | GPIO open-drain emulation; `start`, `stop`, `write`, `read`, `write_to`, `write_bytes`, `read_from`, `ping` |
| `I2C.write_bytes(addr, buf, n)` | Multi-byte I2C write: START + SLA+W + N bytes + STOP |

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
| `print()` routes through stdlib | `print()` calls `uart_write_str` (inline) for strings, `uart_write_decimal_u8` for numbers |

### HAL

| Feature | Notes |
|---------|-------|
| `uart_write_str` pure PyMCU loop | Replaces compiler-intrinsic `uart_send_string` with idiomatic inline loop |
| `pymcu.hal.console` module | Arch-dispatched `print_str` / `print_u8` wrappers for portable console output |

---

## v0.12 — Implemented

### Language (PEP features)

| Feature | Notes |
|---------|-------|
| PEP 526 — Bare class body annotations | `class Foo:\n    x: uint8` registers `x` as an SRAM member without an RHS |
| PEP 695 — `type` alias statement | `type Point = uint8` — compile-time type alias; zero SRAM cost |
| PEP 318/614 — Unknown decorator tolerance | Unrecognised decorators are silently ignored |
| PEP 3102 — Keyword-only parameters | `def f(a, *, b)` — positional call to `b` raises `TypeError` at compile time |
| PEP 308 — Chained comparisons | `0 <= x <= 255` — desugared to an `and`-chain; middle operand evaluated once |
| PEP 701 — f-string format specs | `f"{n:04d}"`, `f"{n:x}"`, `f"{n:X}"`, `f"{n:b}"`, width/align — compile-time only |

---

## v0.11 — Next Tier

Highest-value features not yet implemented, in priority order.

### Language

| Feature | Effort | Notes |
|---------|--------|-------|
| Soft float / `fixed16` | ~1 week | Q8.8 fixed-point for sensor math |
| `const uint8[N]` (PROGMEM) | ~3h | ✅ Implemented in v0.9 |

### HAL

| Feature | Effort | Notes |
|---------|--------|-------|
| `SoftI2C` bit-bang | ~3h | ✅ Implemented in v0.9 |
| `I2C.write_to(addr, buf, n)` multi-byte | ~3h | ✅ Implemented in v0.9 as `I2C.write_bytes` |
| `UART.read_line(buf, max_len)` | ~3h | Read until `\n` into fixed-size buffer |
| Timer `millis()` / `micros()` | ~4h | ✅ Implemented in v0.9 |
| `DS18B20` 1-Wire driver | ~4h | Popular temperature sensor |

### Compat

| Feature | Effort | Notes |
|---------|--------|-------|
| `machine.Timer(id, period, callback)` | ~3h | Timer callback via `@interrupt` + `compile_isr` |
| `busio.SPI` / `busio.I2C` for CP flavor | ~3h | Wraps existing HAL under CircuitPython API |
| `neopixel` driver (CP flavor) | ~4h | WS2812 bit-bang via `neopixel.NeoPixel` API |

### CLI / Driver

| Feature | Notes |
|---------|-------|
| Plugin-based toolchain system | `pymcu.toolchains` entry-point group; `pip install pymcu[avr]` / `pymcu[pic]` |
| `pymcu-toolchain-sdk` | Standalone SDK package with base classes and `ToolchainPlugin` ABC |
| `pymcu-toolchain-avr` | AVR toolchain plugin (GNU AVR binutils); decoupled from core `pymcu` |
| `pymcu-toolchain-pic` | PIC toolchain plugin (GNU PIC Utilities); decoupled from core `pymcu` |

---

## Not Planned

| Feature | Reason |
|---------|--------|
| Heap allocation / `list.append` / `dict` / `set` | No heap; 32-2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime; use return-code error handling |
| `async` / `await` | Use `@interrupt` + polling loop |
| `float` / `complex` | Use `fixed16` or integer-scaled arithmetic |
| `f"..."` runtime interpolation | Use `uart.write_str()` / `uart.print_byte()` |
| Closures capturing mutable vars | Captured variables require heap; `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
