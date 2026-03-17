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
| `match` / `case` | Literal, wildcard `_`, OR (`\|`), dotted-name patterns; DCE on `__CHIP__` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return |
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
| `True` / `False` / `None` | Folded to `Constant{1/0/-1}` |
| String literals | Single- and double-quoted |
| Arithmetic `+ - * / % //` | Full constant folding |
| Comparison `== != < <= > >=` | |
| Bitwise `& \| ^ ~ << >>` | |
| Logical `and` / `or` / `not` | Full short-circuit evaluation |
| Ternary `x if cond else y` | |
| Augmented assignment `+= -= *= //= &= \|= ^= <<= >>=` | Variable, subscript, and member targets |
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
| `ptr[T]` / `ptr(addr)` | Memory-mapped I/O |
| `const[T]` | Compile-time constant enforcement |
| `asm("instr")` | Inline assembly |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic busy-wait |
| `@inline` | Zero-cost expansion |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion |
| `__CHIP__` | Conditional compilation by chip name / architecture |

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
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |

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

## v0.4 — Next Tier

Highest-value features not yet implemented, in priority order.

### Language

| Feature | Effort | Notes |
|---------|--------|-------|
| `Pin.irq(trigger, handler=callback)` | ~8h | MicroPython idiom; requires `compile_isr` compiler intrinsic |
| `enumerate` on runtime arrays | ~2h | `for i, x in enumerate(arr):` where `arr` has variable-index elements |
| `bytes` literal | ~4h | `b"\x00\xFF"` → flash or SRAM byte array |
| `int.from_bytes(b, 'little')` | ~2h | Pack two `uint8` into `uint16`; common in protocol parsing |
| Nested list comprehension | ~3h | `[f(x, y) for x in row for y in col]` |
| Soft float / `fixed16` | ~1 week | Q8.8 fixed-point for sensor math (temperature, percentages) |

### HAL

| Feature | Effort | Notes |
|---------|--------|-------|
| USART RX interrupt + ring buffer | ~4h | Non-blocking receive; pairs with `UART.available()` + `UART.read_nb()` |
| `UART.read_line(buf, max_len)` | ~2h | Read until `\n`; fills fixed-size `uint8[N]` buffer |
| SPI CS pin control from HAL | ~2h | `SPI(cs=Pin("PB2"))` auto-drives CS inside `with spi:` |
| Timer compare match (CTC mode) | ~3h | `Timer.set_compare(val)` + `@interrupt(OCR_vect)` for precise periods |
| ADC interrupt-driven mode | ~3h | Non-blocking conversion via `ADIF` vector |
| PWM multi-channel | ~2h | Timer1 + Timer2 simultaneously; `PWM(channel=1)` |

### Drivers

| Feature | Effort | Notes |
|---------|--------|-------|
| `neopixel` (WS2812) | ~4h | WS2812 bit-bang; used in many CP / uPy projects |
| `HD44780` LCD | ~4h | Common 16x2 character LCD over GPIO or I2C |
| `SSD1306` OLED | ~5h | 128x64 OLED over I2C; very common in maker projects |
| `BMP280` pressure/temp sensor | ~3h | I2C; popular environmental sensor |

### Compat

| Feature | Effort | Notes |
|---------|--------|-------|
| `machine.Timer(id, period, callback)` | ~3h | Requires `Pin.irq` compiler intrinsic |
| `busio.SPI` / `busio.I2C` for CP flavor | ~3h | Wraps existing HAL under CircuitPython API |
| `neopixel` driver (CP flavor) | ~4h | WS2812 bit-bang via `neopixel.NeoPixel` API |
| `analogio.AnalogOut` (DAC stub) | ~1h | Raise CompileError with guidance (no DAC on ATmega328P) |

---

## Not Planned

| Feature | Reason |
|---------|--------|
| Heap allocation / `list.append` / `dict` / `set` | No heap; 32–2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime; use return-code error handling |
| `async` / `await` | Use `@interrupt` + polling loop |
| `float` / `complex` | Use `fixed16` (Beta) or integer-scaled arithmetic |
| `f"..."` runtime interpolation | Use `uart.write_str()` / `uart.print_byte()` |
| Closures / nested `def` | Captured variables require heap |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
