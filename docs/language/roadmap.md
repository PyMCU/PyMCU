# Roadmap

This page tracks which language and HAL features have been implemented, and what is planned next.

---

## Alpha (v0.1) — Implemented

### Language

| Feature | Notes |
|---|---|
| `if / elif / else` | Compile-time DCE on `__CHIP__` branches |
| `while` + `break` / `continue` | |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)` |
| `for x in array` / `for x in [1, 2, 3]` | Fixed-size array or constant list literal |
| `for i, x in enumerate(iterable)` | Compile-time index counter |
| `match / case` | Literal, wildcard, OR (`\|`), dotted-name patterns; DCE on `__CHIP__` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level statements |
| `class` | ZCA `@inline` flattening, constructors, `@property` / `@name.setter` |
| Single-level class inheritance | ZCA base + derived; `super()` calls |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| `with obj:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `global` | Cross-function variable access |

### MCU extensions

| Feature | Notes |
|---|---|
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
| `__FREQ__` | Compile-time clock frequency in Hz |

### HAL

| Module | Coverage |
|---|---|
| `pymcu.hal.gpio` | `Pin` — `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART` — `write/read/write_str/println/print_byte` |
| `pymcu.hal.adc` | `AnalogPin` — `start()` + poll; `read()`, `read_u16()` |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — Timer0/1/2 unified |
| `pymcu.hal.pwm` | `PWM` — `start/stop/set_duty` |
| `pymcu.hal.spi` | `SPI` — `with spi:` context; `transfer/write` |
| `pymcu.hal.i2c` | `I2C` — `with i2c:` context; `ping/write/read_*` |
| `pymcu.hal.eeprom` | `EEPROM` — `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` — `enable/disable/feed` |
| `pymcu.hal.power` | `sleep_idle/adc_noise/power_down/power_save/standby` |
| `pymcu.drivers.dht11` | DHT11 temperature/humidity driver |
| `pymcu.time` | `delay_ms`, `delay_us` |
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |
| `pymcu.boards.arduino_mega` | `D0`–`D53`, `A0`–`A15`, `LED_BUILTIN` |
| `pymcu.boards.arduino_leonardo` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |

---

## Beta (v0.2) — Implemented

| Feature | Notes |
|---|---|
| `in` / `not in` operator | Compile-time fold on constant list; runtime equality chain |
| `is` / `is not` | Maps to `==` / `!=` |
| `divmod(a, b)` | Returns `(quotient, remainder)` |
| `bitcast(T, v)` | Reinterpret raw bytes as `T`; float↔uint32 via register swap; compile-time constant folding |
| `hex(n)` / `bin(n)` | Compile-time: `hex(255)` → `"0xff"` |
| `sum(iterable)` | Compile-time fold or unrolled additions |
| `any(iterable)` / `all(iterable)` | Compile-time fold or OR/AND chain |
| `UART.available()` | Returns 1 if byte waiting in receive buffer |

---

## v0.3 — Implemented

| Feature | Notes |
|---|---|
| `zip(a, b)` compile-time | `for x, y in zip(list1, list2):` — unrolled over paired lists |
| `reversed(iterable)` | `for x in reversed([1,2,3]):` — compile-time reverse unroll |
| `str(n)` compile-time | `str(42)` → `"42"` string constant |
| `pow(x, n)` / `x ** n` | Compile-time constant fold |
| `UART.read_nb()` | Non-blocking read; returns byte if RXC set, else 0 |
| `UART.read_byte_isr()` | Direct UDR0 read for use inside `@interrupt` handlers |
| `I2C.write_to(addr, data)` | START + SLA+W + byte + STOP |
| `I2C.read_from(addr)` | START + SLA+R + read byte + STOP |

---

## v0.4 — Implemented

| Feature | Notes |
|---|---|
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for`, array init, `len()` |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold or runtime |
| `enumerate` on runtime arrays | `for i, x in enumerate(arr):` unrolled |
| `UART.read_blocking()` | Polls RXC until byte arrives |
| SPI CS pin control | `SPI(cs="PB2")` auto-asserts/deasserts CS |

---

## v0.5 — Implemented

| Feature | Notes |
|---|---|
| Timer CTC mode | `Timer.set_compare(val)` — sets OCR + WGM CTC bits |
| ADC interrupt-driven | `AnalogPin.start_conversion()` + `read_result()` |
| PWM multi-channel | Timer0/1/2 OC_A+OC_B channels |
| `neopixel` (WS2812) | `NeoPixel(pin, n).set_pixel(r,g,b)` + `show()` |

---

## v0.6 — Implemented

| Feature | Notes |
|---|---|
| Nested list comprehension | Full outer × inner product unroll |
| `if` filter in list comprehension | Static condition only |
| `bytearray` mutable buffer | `bytearray(8)` → SRAM `uint8[N]` |

---

## v0.7 — Implemented

| Feature | Notes |
|---|---|
| `Pin.irq(trigger, handler)` | Configures INT0/INT1/PCINT hardware |
| USART RX interrupt + ring buffer | `uart.enable_rx_interrupt()` + `uart.rx_isr()` |
| `SoftSPI` bit-bang | `SoftSPI(sck, mosi, miso, cs)` |
| HD44780 LCD driver | 4-bit parallel; `init/clear/home/print_str/set_cursor` |
| SSD1306 OLED driver | 128×64 OLED over I2C |
| MAX7219 8-digit display | SPI 7-segment driver |
| BMP280 barometer | I2C barometric pressure + temperature |

---

## v0.8 — Implemented

| Feature | Notes |
|---|---|
| Raw strings `r"\n"` | No escape processing |
| `match/case` guard `if cond` | PEP 634 guard |
| `match/case` sequence patterns `[a, b, c]` | Destructures fixed-size arrays/tuples |
| `match/case` capture `case x as name` | PEP 634 |
| Multi-item `with a as x, b as y:` | Desugared to nested `with` (PEP 343) |
| Extended unpacking `first, *rest = tup` | Compile-time tuples only (PEP 3132) |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function |
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices |
| `nonlocal` in nested `@inline` | Mutates enclosing scope variable |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, comparisons, bitwise |
| `@extern("symbol")` decorator | Declares and calls external C/C++ symbols with AVR ABI |
| `[tool.pymcu.ffi]` build config | C/C++ interop: `sources`, `include_dirs`, `cflags` |

---

## v0.9 — Implemented

| Feature | Notes |
|---|---|
| `const[uint8[N]]` PROGMEM arrays | Flash-resident lookup tables via `LPM Z` |
| Inline ASM register constraints `%N` | `asm("LDI %0, 42", var)` substitutes scratch regs |
| Signed 16-bit multiplication | Uses `MULSU` for cross-product terms |
| `millis()` / `micros()` | Timer0 overflow; atomic 32-bit read under CLI/SEI |
| `SoftI2C` bit-bang driver | GPIO open-drain emulation |
| `I2C.write_bytes(addr, buf, n)` | Multi-byte I2C write |

---

## v0.10 — Implemented

| Feature | Notes |
|---|---|
| `__name__` compile-time constant | `"__main__"` for entry file, dotted name for libraries |
| `if __name__ == "__main__":` guard | Compile-time guard; body promoted in main, eliminated in libs |
| `const[str]` runtime subscript | Runtime-indexed read on flash string constant via `ArrayLoadFlash` |
| `print()` routes through stdlib | Calls `uart_write_str` / `uart_write_decimal_u8` |

---

## v0.11 — Implemented

| Feature | Status |
|---|---|
| `UART.read_line(buf, max_len)` | ✅ Implemented |
| `DS18B20` 1-Wire driver | ✅ Implemented (`pymcu.drivers.ds18b20`) |
| `machine.Timer(id, period, callback)` | ✅ Implemented (MicroPython compat) |
| Internal temperature sensor (ADC ch 8) | ✅ Implemented (`pymcu.hal.adc`) |
| Programmer plugin system | ✅ Implemented (`pymcu.programmers` entry-point group) |
| `uint16 >> n → uint8` widening shift | ✅ Compiler fix — source type used for shift/bitwise narrowing ops |

---

## Next — v0.12

| Feature | Status |
|---|---|
| `busio.SPI` / `busio.I2C` (CircuitPython compat) | Planned |
| `neopixel` driver (CP flavor) | Planned |
| `fixed16` (Q8.8 fixed-point) | Planned |

---

## Not planned

| Feature | Reason |
|---|---|
| Heap allocation / `list.append` / `dict` / `set` | No heap; 32–2048 bytes SRAM |
| Garbage collection | No runtime |
| `try` / `except` | No runtime |
| `async` / `await` | Use `@interrupt` + polling loop |
| `f"..."` runtime interpolation | Use `uart.write_str()` / `uart.print_byte()` |
| Closures capturing mutable vars | `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
