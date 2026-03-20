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
| `True` / `False` / `None` | Folded to `Constant{1/0/-1}` |
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
| `ptr[T]` | Memory-mapped I/O pointer |
| `const[T]` | Compile-time constant enforcement |
| `asm("instr")` | Inline assembly emission |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic timing |
| `@inline` | Zero-cost abstraction |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion only |
| `@staticmethod` | Silently ignored (all class methods are effectively static) |
| `__CHIP__` | Conditional compilation by chip name / architecture |
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

## v0.8 — Next Tier

These are the highest-value features not yet implemented, in priority order.

### Language

| Feature | Effort | Why |
|---------|--------|-----|
| Soft float / `fixed16` | ~1 week | Q8.8 fixed-point for sensor math (temperature, percentages) |
| `round(x)` / `abs(x)` on `fixed16` | ~2h | Requires `fixed16` |
| `const uint8[N]` (PROGMEM arrays) | ~3h | Read-only lookup tables in flash; `PROGMEM` + `pgm_read_byte` |

### HAL

| Feature | Effort | Why |
|---------|--------|-----|
| `SoftI2C` bit-bang | ~3h | I2C on arbitrary pins; no hardware TWI dependency |
| `I2C.write_to(addr, buf, n)` multi-byte | ~3h | Send N bytes in one transaction; currently single-byte only |
| `UART.read_line(buf, max_len)` | ~3h | Read until `\n` into fixed-size `uint8[N]` buffer |
| `Pin.pulse_in(timeout)` | ~2h | Measure pulse duration in microseconds |
| Timer `millis()` / `micros()` | ~4h | Elapsed-time counter via Timer0 overflow accumulation |
| Internal temperature sensor | ~1h | ATmega328P ADC channel 8; no external component needed |
| `DS18B20` 1-Wire driver | ~4h | Popular temperature sensor; 1-Wire protocol |

### C Interop (avr-as migration)

This tier migrates the assembler backend from `avra` (Intel HEX only) to `avr-as` (GNU binutils,
ELF output), enabling mixed Python + C firmware and proper symbol linking.

| Feature | Effort | Why |
|---------|--------|-----|
| Migrate backend to `avr-as` + `avr-ld` | ~1 week | ELF output; relocations; `.extern`/`.global`; linker script |
| `@extern("symbol")` decorator | ~3h | Declare and call an external C function from PyMCU code |
| `[tool.pymcu.ffi]` build config | ~2h | `sources`, `include_dirs`, `cflags` in `pyproject.toml` |
| `pymcu.ffi` stdlib module | ~1h | Re-export `extern`; no runtime code |
| `avr-gcc` C compilation step | ~2h | Build driver compiles `.c` sources listed in `[tool.pymcu.ffi]` |

**Design: `@extern` decorator**

```python
from pymcu.ffi import extern

# Declare an external C symbol — body is ignored by the compiler
@extern("uart_hw_init")
def uart_hw_init(baud: uint16) -> None: ...

# Call it like any other function
uart_hw_init(9600)
```

The compiler emits `.extern uart_hw_init` in the assembly preamble and a `CALL uart_hw_init`
at every call site using the standard AVR register ABI (arg0→R24, arg1→R22, return→R24:R25).

C sources are compiled separately by the build driver:

```toml
# pyproject.toml
[tool.pymcu.ffi]
sources = ["src/c/mylib.c", "src/c/sensor.c"]
include_dirs = ["src/c/include"]
cflags = ["-O2", "-std=c11"]
```

Build pipeline with avr-as:
```
.py  →  pymcuc  →  firmware.asm
firmware.asm  →  avr-as  →  firmware.o  (ELF)
mylib.c       →  avr-gcc -c  →  mylib.o  (ELF)
firmware.o + mylib.o  →  avr-ld  →  firmware.elf
firmware.elf  →  avr-objcopy -O ihex  →  firmware.hex
```

### Compat

| Feature | Effort | Why |
|---------|--------|-----|
| `machine.Timer(id, period, callback)` | ~3h | Requires `Pin.irq` (now available) |
| `busio.SPI` / `busio.I2C` for CP flavor | ~3h | Wraps existing HAL under CircuitPython API names |
| `neopixel` driver (CP flavor) | ~4h | WS2812 bit-bang via `neopixel.NeoPixel` API |

---

## v0.9 — Longer Horizon

| Feature | Effort | Why |
|---------|--------|-----|
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
| Heap allocation / `list.append` / `dict` / `set` | No heap; MCUs have 32-2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime; use return-code error handling |
| `async` / `await` | Use `@interrupt` + polling loop |
| `float` / `complex` / `Decimal` | Use `fixed16` when available |
| `f"..."` runtime interpolation | Compile-time only (constants only) |
| Closures / nested `def` | Captured variables require heap |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Metaclasses | No runtime type system |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
