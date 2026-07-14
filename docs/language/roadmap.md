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
| `try / except / else / finally`, `raise`, bare `raise` | AVR only; zero-cost T-flag propagation (`SET`/`CLT`/`BRTS`, no `setjmp`/`longjmp`); errors propagate across calls to any depth and are caught at the call site; `finally` runs on every exit path (caught, propagated, `return`/`break`/`continue`); unhandled raise prints `"E:TypeName\r\n"` to UART0 then halts |
| Integer arithmetic promotion | `+`/`-`/`*`/`<<` promote to the next wider type (`uint8 255 + 45 == 300`); the annotation is a storage width; `uint8(a + b)` is the fixed-width escape hatch; out-of-range literals / folded constants are `CompileError` |
| True division `/` vs `//` | `/` yields `float` (soft-float, warns on integer operands); `//` / `%` are integer floor div / mod; runtime divide-by-zero raises `ZeroDivisionError` |
| f-strings (streamed) | `print(f"...")`, `uart.write_str/println(f"...")`, `lcd.print_str(f"...")` with runtime interpolations and format specs (`{x:02x}`, `{x:08b}`, `{x:04d}`, …); lowered to direct writes, no heap |
| Functions with > 5 arguments | Overflow arguments passed via a fixed SRAM spill region |
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
| `input(prompt?, maxlen?)` | `line: bytearray = input("prompt")` — reads newline-terminated line from UART; auto-injects UART init preamble |
| `int.from_bytes(b, 'little'/'big')` | Compile-time fold or runtime |
| Raw strings `r"\n"` | No escape processing |
| Extended unpacking `first, *rest = tup` | Compile-time tuples only (PEP 3132) |
| Nested list comprehensions | Full outer × inner product unroll; `if` filter supported |
| `for v in [Cls(p) for p in (...)]` | CT unroll of ZCA instance arrays from list comprehensions; plain for-in and enumerate both supported |
| Slice indexing `arr[1:3]`, `arr[::2]` | Compile-time constant indices |
| `lambda x: expr` (no capture) | Inlined as anonymous `@inline` function |
| Dunder operator overloading | `__add__`, `__sub__`, `__mul__`, `__len__`, `__contains__`, `__getitem__`, `__setitem__`, comparisons, bitwise |
| `@extern("symbol")` | External C/C++ symbol interop with AVR ABI |
| `__name__` / `if __name__ == "__main__":` | Compile-time guard; body promoted in main, eliminated in libs |
| Triple-quoted strings `"""..."""` / `'''...'''` | Multiline string literals; leading newline after opening quote stripped; useful for multiline `asm()` |
| `list[T]` heap-allocated list | `x: list[uint8] = list()` / `list(N)` / `[a, b, c]`; `append()`, `len()`, `x[i]`, `for v in x:`; bounded bump allocator + GC; suitable for ATmega328P (2 KB SRAM) and larger |

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
| `float` (soft-float) | IEEE 754 single-precision; AVR only; uses `__fp_add/sub/mul/div/cmp` intrinsics; annotation `x: float = 3.14` supported |
| `@naked` | No compiler prolog/epilog; registers hold raw calling-convention values at function entry; required for precise `uint16` register manipulation |
| `@staticmethod` | Silently ignored — all class methods in PyMCU are effectively static |
| `CompileError` intrinsic | `raise CompileError("msg")` aborts compilation with a `CompileError:` diagnostic; never generates `RaiseExn` IR; used in all HAL modules for unsupported arch/chip guards; cannot be caught by `try/except` |

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

| Package | Activation | Coverage |
|---------|-----------|----------|
| `pymcu-micropython` | `stdlib = ["micropython"]` | `machine` (Pin, UART, ADC, PWM, SPI, I2C, `Timer(id, period, callback)`), `utime`, `micropython` |
| `pymcu-circuitpython` | `stdlib = ["circuitpython"]` | `board`, `digitalio`, `analogio`, `busio` (SPI + I2C), `pwmio`, `time`, `neopixel.NeoPixel` |

### Boards

| Module | Pins |
|---|---|
| `pymcu.boards.arduino_uno` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |
| `pymcu.boards.arduino_mega` | `D0`–`D53`, `A0`–`A15`, `LED_BUILTIN` |
| `pymcu.boards.arduino_leonardo` | `D0`–`D13`, `A0`–`A5`, `LED_BUILTIN` |

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
| RISC-V 32-bit codegen | CH32V003, ESP32-C3 targets |
| RP2040 PIO backend | Programmable I/O state machine output |
| Over-the-air (OTA) support | Bootloader + `pymcu flash` over UART |
| ARM Cortex-M3/M4 codegen | STM32, nRF52 — reuses the LLVM backend |

---

## Not planned

| Feature | Reason |
|---|---|
| `dict` / `set` | Dynamic hash tables require heap; no runtime |
| Garbage collection beyond `list[T]` | Full GC incompatible with deterministic ISR timing |
| `async` / `await` beyond the v1 subset | v1 (compile-time state machine over `asyncio.sleep/sleep_ms`) is implemented; `await` in `if`/nested loops, arbitrary awaitables and `await`-as-expression are the planned v2 |
| `f"..."` into a string **variable** | Streamed f-strings (`print(f"...")` etc.) are supported; only assigning the result to a string object needs a heap |
| Closures capturing mutable vars | `nonlocal` in `@inline` is supported |
| `*args` / `**kwargs` | Requires heap |
| Multiple inheritance | Complexity vs. benefit for ZCA model |
| Reflection / `getattr` / `hasattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |
