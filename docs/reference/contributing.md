# Contributing

Contributions to PyMCU are welcome. Please read this guide before opening a PR.

---

## Repository layout

```
pymcu/
  src/compiler/           # C# compiler (pymcuc)
    Frontend/             # Lexer, Parser, AST
    IR/                   # IRGenerator, Optimizer, Tacky IR
    Backend/Targets/AVR/  # AVR codegen, peephole, register allocator
  lib/src/pymcu/          # Python stdlib (compiled into firmware)
    hal/                  # GPIO, UART, ADC, Timer, PWM, SPI, I2C
    drivers/              # DHT11 and other device drivers
    boards/               # Board pin name constants
    chips/                # Chip configuration and __CHIP__
  src/driver/             # Python CLI driver (pymcu build/flash/new)
  tests/integration/      # .NET / AVR8Sharp integration tests
  examples/avr/           # Firmware examples (each with a full test suite)
  docs/                   # This documentation site
```

---

## Building the compiler

```bash
dotnet publish src/compiler/PyMCU.Compiler.csproj -c Release -o build/bin --nologo
```

---

## Running integration tests

```bash
dotnet test tests/integration/PyMCU.IntegrationTests.csproj
```

All tests must stay green. Add a new test in `tests/integration/Tests/AVR/` for every new
compiler or HAL feature.

---

## Adding a stdlib module / Driver

When contributing to the standard library or adding new drivers, please adhere to the following principles:

1. **MicroPython/CircuitPython Compatibility (Crucial):** If you are writing a driver for a sensor or exposing an API, **it must match the corresponding MicroPython or CircuitPython API** if one exists. Do not invent your own API names (e.g., use `machine.Pin`, not `pymcu.hal.gpio.Pin` for user-facing code). The internal HAL in `lib/src/pymcu/hal` is meant to be wrapped by these compatibility layers.
2. Add the implementation in `lib/src/pymcu/hal/` (or `drivers/`).
3. Use `@inline` for all public methods (zero-cost abstraction rule).
4. Use `match __CHIP__.arch:` for architecture dispatch.
5. No non-ASCII characters — the compiler lexer is ASCII-only.
6. No multiline docstrings with code examples — use `# comments` instead.
7. After editing stdlib, sync to the local virtualenv:

```bash
rsync lib/src/pymcu/ .venv/lib/python3.X/site-packages/pymcu/
```

---

## HAL coding rules

- No em dashes (U+2014) or other non-ASCII characters in any `.py` file under `lib/`.
- No statements after `match` blocks — put defaults in `case _:` inside the match.
- Dotted names (`ClassName.ATTR`) are value patterns in `match/case`; bare names are
  capture patterns.
- `@inline` functions containing `asm()` with labels must delegate to a non-inline sub-helper
  to avoid label duplication across inline expansion sites.
- Do not use `+=` / augmented assignment inside compile-time unrolled `for` loops.
  Use `acc = acc + x` instead (known compiler limitation).

---

## Commit format

PyMCU uses [Conventional Commits](https://www.conventionalcommits.org/). Every commit must be:

```
<type>(<scope>): <short description under 72 chars>
```

**Types:** `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `chore`, `style`

**Scopes:** `avr`, `ir`, `parser`, `hal`, `driver`, `stdlib`, `drivers`, `test`, `docs`, `ci`

A typical feature implementation splits into 2–5 focused commits:

```
feat(parser): parse @extern decorator on function definitions
feat(ir): emit Extern IR instruction and register extern symbols
feat(avr): emit .extern and CALL with AVR ABI for @extern
test(avr): add ExternCallTests for @extern C interop
docs: mark @extern as implemented in roadmap and limitations
```

**Each commit must leave the test suite green.**

---

## Building this documentation

```bash
cd docs
pip install -r requirements.txt
sphinx-build -b html . _build/html
python -m http.server -d _build/html   # preview at http://localhost:8000
```

For live reload during editing:

```bash
pip install sphinx-autobuild
sphinx-autobuild . _build/html
```

---

## Pull request checklist

1. Fork the repository and create your branch from `main`.
2. Each commit follows Conventional Commits format.
3. All integration tests pass (`dotnet test ...`).
4. A test is added for any new compiler or HAL feature.
5. `LANGUAGE_ROADMAP.md` and `docs/language/roadmap.md` are updated if applicable.
6. `docs/language/limitations.md` is updated if the supported/unsupported status changes.
