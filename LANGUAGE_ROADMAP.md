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
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5` | ATmega328P | Pin name constants |

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

## v0.4 — Next Tier

These are the highest-value features not yet implemented, in priority order.

### Language

| Feature | Effort | Why |
|---------|--------|-----|
| `Pin.irq(trigger, handler=callback)` | ~8h | MicroPython idiom; requires compile-time `compile_isr` intrinsic |
| `enumerate` on runtime arrays | ~2h | `for i, x in enumerate(arr):` where `arr` is a variable-index array |
| `bytes` literal / `bytearray` | ~4h | `b"\x00\xFF"` → flash or SRAM byte array |
| `int.from_bytes(b, 'little')` | ~2h | Pack two uint8 into uint16; common in protocol parsing |
| Nested list comprehension | ~3h | `[f(x,y) for x in row for y in col]` |
| `round(x)` / `abs(x)` on fixed16 | ~2h | Once `fixed16` lands (v0.5) |
| Soft float / `fixed16` | ~1 week | Q8.8 fixed-point for sensor math |

### HAL

| Feature | Effort | Why |
|---------|--------|-----|
| USART RX interrupt + ring buffer | ~4h | Non-blocking receive; pairs with `UART.available()` + `UART.read_nb()` |
| `UART.read_line(buf, max_len)` | ~2h | Read until `\n`; fills fixed-size `uint8[N]` buffer |
| SPI CS pin control from HAL | ~2h | `SPI(cs=Pin("PB2"))` auto-drives CS inside `with spi:` |
| Timer compare match (CTC mode) | ~3h | `Timer.set_compare(val)` + `@interrupt(OCR_vect)` for precise periods |
| ADC interrupt-driven mode | ~3h | Non-blocking ADC via `ADIF` vector `0x001a` |
| PWM multi-channel | ~2h | Timer1 + Timer2 simultaneously; `PWM(channel=1)` |
| `SoftSPI` / `SoftI2C` (bit-bang) | ~5h | Bit-banged fallback for non-hardware-SPI pins |

### Drivers

| Feature | Effort | Why |
|---------|--------|-----|
| `neopixel` (WS2812) driver | ~4h | WS2812 bit-bang; widely used in CP / uPy |
| `HD44780` LCD driver | ~4h | Common 16x2 character LCD over GPIO or I2C |
| `SSD1306` OLED driver | ~5h | 128x64 OLED over I2C; very common in maker projects |
| `BMP280` pressure/temp sensor | ~3h | I2C; popular environmental sensor |
| `DS18B20` temperature sensor | ~4h | 1-Wire; very common; requires 1-wire driver first |
| `MAX7219` LED matrix | ~3h | SPI 8x8 LED matrix |

### Compat

| Feature | Effort | Why |
|---------|--------|-----|
| `machine.Timer(id, period, callback)` | ~3h | Requires `Pin.irq` / `compile_isr` intrinsic |
| `busio.SPI` / `busio.I2C` for CP flavor | ~3h | Wraps existing HAL under CircuitPython API names |
| `neopixel` driver (CP flavor) | ~4h | WS2812 bit-bang via `neopixel.NeoPixel` API |

---

## v0.5 — Longer Horizon

| Feature | Effort | Why |
|---------|--------|-----|
| `fixed16` (Q8.8 fixed-point) | ~1 week | Float-like sensor math without FPU |
| PIC18 codegen | ~2 weeks | Extend backend for PIC18Fxxxx family |
| RISC-V 32-bit codegen | ~2 weeks | CH32V003, ESP32-C3 |
| RP2040 PIO backend | ~1 week | Programmable I/O state machine output |
| Over-the-air (OTA) support | ~1 week | Bootloader + pymcu flash over UART |
| LLVM IR backend | ~4 weeks | Unlocks all LLVM targets (ARM Cortex-M, etc.) |

---

## Not Planned

These Python features are architecturally incompatible with bare-metal, no-heap firmware:

| Feature | Reason |
|---------|--------|
| Heap allocation / `list.append` / `dict` / `set` | No heap; MCUs have 32–2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime; use return-code error handling |
| `async` / `await` | Use `@interrupt` + polling loop |
| `float` / `complex` / `Decimal` | Use `fixed16` when available (Beta) |
| `f"..."` runtime interpolation | Use compile-time only (Beta) |
| Closures / nested `def` | Captured variables require heap |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Metaclasses | No runtime type system |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
