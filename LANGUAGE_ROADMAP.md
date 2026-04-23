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
| `return` | With/without value; tuple multi-return |
| `pass` / `raise` | `raise` is compile-time only |
| `import` / `from ... import` / `import X as Y` | Relative imports, multi-level |
| `global` | Cross-function variable access |

### Expressions

| Feature | Notes |
|---------|-------|
| Integer literals | Decimal, hex, binary, octal, `_` separators |
| `True` / `False` / `None` | `True`/`False` fold to `Constant{1/0}`; `None` on object-reference types folds via `is None` / `is not None`; `None` on numeric types is a TypeError |
| String literals | Single- and double-quoted; mapped to stable compile-time IDs |
| Arithmetic `+ - * / % //` | Full constant folding |
| Comparison `== != < <= > >=` | |
| Bitwise `& | ^ ~ << >>` | |
| Logical `and` / `or` / `not` | Full short-circuit evaluation |
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
| Array indexing `arr[i]` | Constant-index: zero overhead; variable-index: SRAM |
| List comprehension `[x*2 for x in range(n)]` | Compile-time unroll; constant iterable only |
| Tuple literal `(a, b)` / unpacking `a, b = f()` | Stack-allocated; multi-return |
| Member access `obj.x` / method calls `obj.m()` | Inline expansion; zero SRAM |
| Keyword arguments `f(key=val)` | Matched by name in inline binding |
| `print(val)` | Maps to UART; requires `default_uart` in `pyproject.toml` |
| F-strings `f"text={var}"` | Compile-time constant only; all `{expr}` must resolve to string or integer constants |

### MCU-Specific Extensions

| Feature | Notes |
|---------|-------|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Required annotation for all variables |
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
| `pymcu.hal.adc` | `AnalogPin` | AVR, PIC | `start()` + poll; `read()` (10-bit), `read_u16()` (0-65535) |
| `pymcu.hal.timer` | `Timer(n, prescaler)` | All | Timer0/1/2 unified; `start/stop/clear/overflow` |
| `pymcu.hal.pwm` | `PWM` | AVR, PIC | Hardware PWM; `start/stop/set_duty` |
| `pymcu.hal.spi` | `SPI` | AVR | HW SPI master; `with spi:` context |
| `pymcu.hal.i2c` | `I2C` | AVR | TWI master; `with i2c:` context; `ping/write/read_*` |
| `pymcu.hal.eeprom` | `EEPROM` | ATmega328P | `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` | ATmega328P | `enable/disable/feed`; timeout is compile-time const |
| `pymcu.hal.power` | `sleep_*` | ATmega328P | `idle / adc_noise / power_down / power_save / standby` |
| `pymcu.drivers.dht11` | `DHT11` | All | Portable driver; reads humidity + temperature |
| `pymcu.time` | `delay_ms`, `delay_us` | All | Blocking delays |
| `pymcu.boards.arduino_uno` | `D0`-`D13`, `A0`-`A5` | ATmega328P | Pin name constants |

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
| `HD44780` LCD | `LCD(rs, en, d4-d7)` — 4-bit parallel; `init/clear/home/print_str/set_cursor/write_char` |
| `SSD1306` OLED | 128x64 OLED over I2C; `init/clear/draw_pixel/draw_line/print_str` |
| `MAX7219` 8-digit display | SPI 7-segment driver; `set_digit/set_raw/clear/set_brightness` |
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
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices only; produces fixed-size array (PEP 197) |
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

## PIC Backend Tier Model

The `pymcu-pic` extension supports three PIC architecture tiers:

| Tier | Architectures | Status |
|------|--------------|--------|
| **Supported WIP** | PIC18Fxxxx (`arch = "pic18"`) | Full feature set targeted; signed comparisons, flash tables, soft-divide, variable shifts, conditional ISR context saves, banking stub |
| **Experimental** | PIC16Fxxx / PIC14 (`arch = "pic14"`) | 8+16-bit; RETLW flash tables; variable shifts; soft-divide; signed comparisons; FSR ISR save; single-page default (opt-in PAGESEL via `fuses.multipage = "true"`) |
| **Experimental** | PIC12Fxxx / PIC10Fxxx (`arch = "pic12"`) | 8-bit only; compile errors on 16-bit, variable shifts, Mul/Div, runtime-indexed flash; FSR-indirect arrays; BitSet/Clear via FSR |

The compiler emits `[WARNING] PIC12/PIC14 is Experimental` at the top of generated output.

---

## v0.12 — Implemented

### Language (PEP features)

| Feature | Notes |
|---------|-------|
| PEP 526 — Bare class body annotations | `class Foo:\n    x: uint8` registers `x` as a zero-initialised SRAM member without requiring an RHS |
| PEP 695 — `type` alias statement | `type Point = uint8` registers a compile-time-only type alias; emits no SRAM or code |
| PEP 318/614 — Unknown decorator tolerance | Unrecognised decorators are silently ignored rather than raising a `CompileError` |
| PEP 3102 — Keyword-only parameters | `def f(a, *, b)` — `*` separator flags subsequent params as keyword-only; positional call raises `TypeError` |
| PEP 308 — Chained comparisons | `0 <= x <= 255` desugars to `(0 <= x) and (x <= 255)` with `x` evaluated exactly once |
| PEP 701 — f-string format specs | `f"{n:04d}"`, `f"{n:x}"`, `f"{n:X}"`, `f"{n:b}"`, `f"{n:o}"`, width/alignment — compile-time constants only |

---

## v0.11 — Next Tier

These are the highest-value features not yet implemented, in priority order.

### CLI / Driver

| Feature | Effort | Notes |
|---------|--------|-------|
| Plugin-based toolchain system | ✅ Implemented | `pymcu.toolchains` entry-point group; `pip install pymcu[avr]` / `pymcu[pic]` |
| `pymcu-plugin-sdk` | ✅ Implemented | Standalone SDK package; base classes, `BackendPlugin` + `ToolchainPlugin` ABCs |
| AVR toolchain plugin | ✅ Implemented | Merged into `pymcu-avr` (GNU AVR binutils); independent of core `pymcu` |
| PIC toolchain plugin | ✅ Implemented | Merged into `pymcu-pic` (GNU PIC Utilities); independent of core `pymcu` |
| RISC-V toolchain plugin | ✅ Implemented | Merged into `pymcu-riscv` (GNU RISC-V bare-metal); riscv-none-elf / riscv32-unknown-elf |
| PIO toolchain plugin | ✅ Implemented | Merged into `pymcu-pio` (pioasm from Raspberry Pi Pico SDK) |
| Programmer plugin system | ~1 week | `pymcu.programmers` entry-point group; `pymcu-programmer-avrdude`, etc. |

### Language

| Feature | Effort | Why |
|---------|--------|-----|
| ~~Soft float~~ / `fixed16` | ~~1 week~~ | ✅ Soft-float IEEE 754 implemented (AVR) — `__fp_add/sub/mul/div/cmp` + int↔float conversions. `fixed16` deferred. |
| `round(x)` / `abs(x)` on `fixed16` | ~2h | Requires `fixed16` |
| `const uint8[N]` (PROGMEM arrays) | ~3h | ✅ Implemented in v0.9 |

### HAL

| Feature | Effort | Why |
|---------|--------|-----|
| `SoftI2C` bit-bang | ~3h | ✅ Implemented in v0.9 |
| `I2C.write_to(addr, buf, n)` multi-byte | ~3h | ✅ Implemented in v0.9 as `I2C.write_bytes` |
| `UART.read_line(buf, max_len)` | ~3h | Read until `\n` into fixed-size `uint8[N]` buffer |
| Timer `millis()` / `micros()` | ~4h | ✅ Implemented in v0.9 |
| Internal temperature sensor | ~1h | ATmega328P ADC channel 8; no external component needed |
| `DS18B20` 1-Wire driver | ~4h | Popular temperature sensor; 1-Wire protocol |

### Compat

| Feature | Effort | Why |
|---------|--------|-----|
| `machine.Timer(id, period, callback)` | ~3h | Requires `Pin.irq` (now available) |
| `busio.SPI` / `busio.I2C` for CP flavor | ~3h | Wraps existing HAL under CircuitPython API names |
| `neopixel` driver (CP flavor) | ~4h | WS2812 bit-bang via `neopixel.NeoPixel` API |

---

## v1.0 — Longer Horizon

| Feature | Effort | Why |
|---------|--------|-----|
| `fixed16` (Q8.8 fixed-point) | ~1 week | Float-like sensor math without FPU |
| PIC18 full production support | ~2 weeks | Signed comparisons, flash tables, soft-divide, variable shifts, ISR context, banking — all implemented as Supported WIP |
| RISC-V 32-bit codegen | ~2 weeks | CH32V003, ESP32-C3 |
| RP2040 PIO backend | ~1 week | Programmable I/O state machine output |
| Over-the-air (OTA) support | ~1 week | Bootloader + pymcu flash over UART |
| LLVM IR backend (ARM) | 🚧 In progress | `pymcu-arm` package; emits LLVM IR for clang |
| ARM Cortex-M backend (RP2040 / RP2350) | 🚧 In progress | `thumbv6m-none-eabi` (M0+), `thumbv8m.main-none-eabi` (M33); via LLVM |
| ARM Cortex-M (STM32, nRF52) | future | Extend `pymcu-arm` with additional chip targets |

---

## Not Planned

These Python features are architecturally incompatible with bare-metal, no-heap firmware:

| Feature | Reason |
|---------|--------|
| Heap allocation / `list.append` / `dict` / `set` | No heap; MCUs have 32-2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime; use return-code error handling |
| `async` / `await` | Use `@interrupt` + polling loop |
| `float` / `complex` / `Decimal` | Use `fixed16` when available |
| `f"..."` runtime interpolation | Compile-time only (constants only) |
| Closures capturing mutable vars | Captured variables require heap; `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Metaclasses | No runtime type system |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
