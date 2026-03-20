# Contributing

Contributions to PyMCU are welcome. Please read this guide before opening a PR.

## Repository layout

```
pymcu/
  src/compiler/           # C++ compiler (pymcuc)
    frontend/             # Lexer, Parser, AST
    ir/                   # IRGenerator, Optimizer, Tacky IR
    backend/targets/avr/  # AVR codegen, peephole, register allocator
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

## Building the compiler

```bash
cmake -B build -S src/compiler -DCMAKE_BUILD_TYPE=Release
cmake --build build --target pymcuc -j$(sysctl -n hw.ncpu)
```

## Running integration tests

```bash
dotnet test tests/integration/PyMCU.IntegrationTests.csproj
```

All tests must stay green. Add a new test in `tests/integration/Tests/AVR/` for any new
compiler or HAL feature.

## Adding a stdlib module

1. Add the implementation in `lib/src/pymcu/hal/` (or `drivers/`).
2. Use `@inline` for all public methods (zero-cost abstraction rule).
3. Use `match __CHIP__.arch:` for architecture dispatch.
4. No non-ASCII characters in source (compiler lexer is ASCII-only).
5. No multiline docstrings with code examples — use `# comments` instead.
6. After editing stdlib, rsync to the local virtualenv:
   ```bash
   rsync lib/src/pymcu/ .venv/lib/python3.X/site-packages/pymcu/
   ```

## HAL coding rules

- No em dashes (U+2014) or other non-ASCII in source.
- No statements after `match` blocks — put defaults in `case _:` inside the match.
- Dotted names (`ClassName.ATTR`) are value patterns in `match/case`; bare names are capture patterns.
- `@inline` functions containing `asm()` with labels must delegate to a non-inline sub-helper.

## Commit format

PyMCU uses [Conventional Commits](https://www.conventionalcommits.org/). Every commit must follow:

```
<type>(<scope>): <short description>
```

**Types:** `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `chore`, `style`

**Scopes:** `avr`, `ir`, `parser`, `hal`, `driver`, `stdlib`, `drivers`, `test`, `docs`, `ci`

```
feat(avr): add PROGMEM flash array support
fix(ir): wrong register spill in 16-bit aug-assign
test(avr): add NestedListCompTests for filtered comprehension
docs: update limitations — bytearray and with are now supported
```

## Splitting feature commits

**Each distinct feature must land as small, focused commits** — never bundle unrelated changes.
A typical feature is 2-5 commits:

```
feat(parser): parse @extern decorator on function definitions
feat(ir): emit Extern IR instruction and register extern symbols
feat(avr): emit .extern and CALL with AVR ABI for @extern
test(avr): add ExternCallTests for @extern C interop
docs: add @extern to roadmap and limitations
```

One logical change per commit. Each commit must leave the test suite green.

## Docs site

```bash
cd docs
pip install -r requirements.txt
mkdocs build --strict    # must pass with no warnings
mkdocs serve             # preview at http://127.0.0.1:8000
```

## Pull requests

1. Fork the repository and create your branch from `main`.
2. Each commit must follow Conventional Commits format.
3. All integration tests must pass.
4. Add a test for any new compiler or HAL feature.
5. Update `LANGUAGE_ROADMAP.md` and `docs/docs/roadmap.md` if applicable.
6. Update `docs/docs/limitations.md` if the supported/unsupported status of a feature changes.
