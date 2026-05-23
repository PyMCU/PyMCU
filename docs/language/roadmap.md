# Roadmap

This page tracks which language and HAL features have been implemented, and what is planned next.

---

## Implemented

### Language

| Feature | Notes |
|---|---|
| `if / elif / else` | Compile-time DCE on `__CHIP__` branches |
| `while` + `break` / `continue` | |
| `for i in range(n)` | Runtime or compile-time bound; `range(start, stop, step)` |
| `for x in array` / `for x in [1, 2, 3]` | Fixed-size array or constant list literal |
| `for i, x in enumerate(iterable)` | Compile-time index counter |
| `for x, y in zip(a, b)` | Compile-time unroll over paired lists |
| `reversed(iterable)` | Compile-time reverse unroll |
| `match / case` | Literal, wildcard, OR (`\|`), guard `if cond`, sequence, capture, dotted-name patterns; DCE on `__CHIP__` |
| `def` | Typed params, defaults, keyword args, overloading by type, tuple multi-return |
| Top-level scripts (no `def main():`) | Compiler synthesizes `main` from top-level statements |
| `class` | ZCA `@inline` flattening, constructors, `@property` / `@name.setter` |
| Single-level class inheritance | ZCA base + derived; `super()` calls |
| `class Foo(Enum)` | Zero-cost integer constants; no SRAM |
| `with obj:` / `with a as x, b as y:` | `__enter__` / `__exit__`; zero-cost for `@inline` methods |
| `assert condition, msg` | Compile-time only; statically false → CompileError |
| `global` / `nonlocal` | Cross-function variable access; `nonlocal` in `@inline` |
| `in` / `not in` | Compile-time fold on constant list; runtime equality chain |
| `is` / `is not` | Maps to `==` / `!=` |
| `divmod(a, b)` | Returns `(quotient, remainder)` |
| `bitcast(T, v)` | Reinterpret raw bytes as `T`; float↔uint32; compile-time folding |
| `hex(n)` / `bin(n)` | Compile-time: `hex(255)` → `"0xff"` |
| `sum(iterable)` / `any(iterable)` / `all(iterable)` | Compile-time fold or unrolled chain |
| `str(n)` compile-time | `str(42)` → `"42"` string constant |
| `pow(x, n)` / `x ** n` | Compile-time constant fold |
| `bytes` literal `b"\x00\xFF"` | Treated as `uint8[N]`; works in `for`, array init, `len()` |
| `bytearray` | Mutable SRAM buffer |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold or runtime |
| Raw strings `r"\n"` | No escape processing |
| Extended unpacking `first, *rest = tup` | Compile-time tuples only (PEP 3132) |
| Nested list comprehensions | Full outer × inner product unroll; `if` filter supported |
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, comparisons, bitwise |
| `@extern("symbol")` | External C/C++ symbol interop with AVR ABI |
| `__name__` / `if __name__ == "__main__":` | Compile-time guard; body promoted in main, eliminated in libs |

### MCU extensions

| Feature | Notes |
|---|---|
| `uint8 / int8 / uint16 / int16 / uint32 / int32` | Required annotation for all variables |
| `int` (built-in) | Maps to `int16`; no import required |
| `ptr[T]` / `ptr(addr)` | Memory-mapped I/O |
| `const[T]` / `const[uint8[N]]` | Compile-time constants; flash-resident arrays via `LPM Z` |
| `asm("instr")` | Inline assembly with register constraints `%N` |
| `delay_ms(n)` / `delay_us(n)` | Intrinsic busy-wait |
| `millis()` / `micros()` | Timer0 overflow; atomic 32-bit read under CLI/SEI |
| `@inline` | Zero-cost expansion |
| `@interrupt(vector)` | ISR handler generation with automatic `sei` |
| `@property` / `@name.setter` | Compile-time expansion |
| `__CHIP__` | Conditional compilation by chip name / architecture |
| `__FREQ__` | Compile-time clock frequency in Hz |
| `[tool.pymcu.ffi]` build config | C/C++ interop: `sources`, `include_dirs`, `cflags` |

### HAL (ATmega328P)

| Module | Coverage |
|---|---|
| `pymcu.hal.gpio` | `Pin` — `high/low/toggle/value/irq/pulse_in` |
| `pymcu.hal.uart` | `UART` — `write/read/read_line/write_str/println/print_byte/available` + RX interrupt |
| `pymcu.hal.adc` | `AnalogPin` — poll + interrupt; `adc_read_temp_raw()` internal sensor |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — Timer0/1/2 unified; CTC mode |
| `pymcu.hal.pwm` | `PWM` — `start/stop/set_duty/set_freq`; multi-channel |
| `pymcu.hal.spi` | `SPI` + `SoftSPI` |
| `pymcu.hal.i2c` | `I2C` + `SoftI2C`; `write_to` / `read_from` / `write_bytes` / `writeto_mem` / `readfrom_mem_into` |
| `pymcu.hal.eeprom` | `EEPROM` — `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` — `enable/disable/feed` |
| `pymcu.hal.power` | `sleep_idle/adc_noise/power_down/power_save/standby` |

### Drivers

| Module | Device |
|---|---|
| `pymcu.drivers.dht11` | DHT11 temperature + humidity |
| `pymcu.drivers.ds18b20` | DS18B20 1-Wire precision temperature (12-bit) |
| `pymcu.drivers.lm35` | LM35 analog temperature (ADC) |
| `pymcu.drivers.hd44780` | HD44780 LCD (4-bit parallel) |
| `pymcu.drivers.ssd1306` | SSD1306 OLED (I2C, 128×64) |
| `pymcu.drivers.max7219` | MAX7219 7-segment display (SPI) |
| `pymcu.drivers.bmp280` | BMP280 barometer (I2C) |
| `pymcu.drivers.neopixel` | WS2812 NeoPixel |

### Compatibility layers

| Module | Status |
|---|---|
| `pymcu.compat.micropython` | `machine`, `utime`, `micropython` — GPIO, UART, ADC, PWM, Timer |
| `pymcu.compat.circuitpython` | `board`, `digitalio`, `analogio`, `pwmio`, `time` — GPIO, UART, ADC, PWM |

### Boards

| Module | Pins |
|---|---|
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |
| `pymcu.boards.arduino_mega` | `D0`–`D53`, `A0`–`A15`, `LED_BUILTIN` |
| `pymcu.boards.arduino_leonardo` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |

---

## Planned

| Feature | Notes |
|---|---|
| `fixed16` (Q8.8 fixed-point) | Fixed-point arithmetic without floats |
| `busio.SPI` / `busio.I2C` (CircuitPython compat) | Expand compat layer coverage |
| `neopixel` (CircuitPython flavor) | `neopixel.NeoPixel` wrapper |
| MicroPython/CircuitPython API alignment | Broaden module coverage |
| `pymcu` pip package | Distribute CLI toolchain via PyPI |

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
